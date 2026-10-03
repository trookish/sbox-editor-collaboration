using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// What a spectator may ever create from a played run. The spectator mirrors state it must never execute: a component of a game's own type runs
/// that game's code the moment it exists, and the sender is another person's editor, so neither "the sender filtered it" nor "the type looks
/// harmless" is enough. Two independent rules hold, and both run on both sides (the sender strips before it sends, the receiver refuses again):
/// <list type="number">
/// <item>The component's <c>__type</c> must equal, character for character, one of <see cref="AllowedTypes"/>: engine renderer and light types
/// that draw something and run nothing of the game's.</item>
/// <item>Only the properties listed for that type survive, and only when the value is plain data: no nested type discriminator (<c>__type</c>), no
/// prefab patch (<c>__Prefab...</c>), no <c>$</c> directive, bounded depth, size and string length. Everything else the engine writes (the
/// <c>OnComponent*</c> hooks, flags, colliders' trigger actions) is dropped, so a callback can never ride along.</item>
/// </list>
/// A property that is missing takes its engine default on the spectator, which is the correct behaviour for something the sender chose not to
/// send. A type this list cannot justify stays OFF: the audit below was done against the decompiled engine (Sandbox.Engine.dll), and each entry
/// says why it is safe to create. No spectator ever instantiates a <c>CameraComponent</c>: it has its own camera.
/// </summary>
public static class SpectateWhitelist
{
    public const int MaxStringLength = 65536;

    public const int MaxValueNodes = 20000;

    public const int MaxValueDepth = 8;

    public const int MaxComponentsPerObject = 16;

    /// <summary>The whole component, serialised, may not exceed this: a frame is bounded and one component may not take all of it.</summary>
    public const int MaxComponentBytes = 131072;

    /// <summary>Types that are never created on a spectator, kept explicit so a future edit of the allow list cannot add one silently (a test pins both).</summary>
    public static readonly IReadOnlyList<string> NeverTypes = new[] { "Sandbox.CameraComponent" };

    // Property names are read from the [Property] members of the decompiled types (Sandbox.ModelRenderer, SkinnedModelRenderer, Light, AmbientLight,
    // DirectionalLight, PointLight, SpotLight, SkyBox2D, MeshComponent, Terrain), and the shapes from the engine's own sample scenes.
    private static readonly string[] ModelRendererProperties =
    {
        // CreateAttachments is left out: it makes the renderer create child objects, which a mirror must only get from the stream.
        "Model", "Tint", "BodyGroups", "MaterialGroup", "RenderType", "LodOverride", "MaterialOverride", "Materials", "RenderOptions",
    };

    private static readonly string[] LightProperties =
    {
        "LightColor", "FogMode", "FogStrength", "Shadows", "ShadowBias", "ShadowHardness", "Contribution",
    };

    private static readonly Dictionary<string, string[]> Types = new Dictionary<string, string[]>( StringComparer.Ordinal )
    {
        // Draws a model. Its only effect is a scene object in the renderer; no game callback is reachable from the properties listed.
        ["Sandbox.ModelRenderer"] = ModelRendererProperties,

        // A ModelRenderer that also plays animation. Animation events (footsteps and the like) fire engine events with no listener on a
        // spectator; the animation graph is data. CreateBoneObjects is left out for the same reason as CreateAttachments.
        ["Sandbox.SkinnedModelRenderer"] = ModelRendererProperties.Concat( new[]
        {
            "DeformationMode", "BoneMergeTarget", "UseAnimGraph", "AnimationGraph", "Sequence", "PlaybackRate", "Morphs", "Parameters",
        } ).ToArray(),

        // A light is a scene object that only illuminates.
        ["Sandbox.AmbientLight"] = new[] { "Color" },
        ["Sandbox.DirectionalLight"] = LightProperties.Concat( new[] { "SkyColor", "ShadowCascadeCount", "ShadowCascadeSplitRatio", "ContactShadows" } ).ToArray(),
        ["Sandbox.PointLight"] = LightProperties.Concat( new[] { "Radius", "Attenuation" } ).ToArray(),
        ["Sandbox.SpotLight"] = LightProperties.Concat( new[] { "Radius", "ConeOuter", "ConeInner", "Attenuation", "Cookie" } ).ToArray(),

        // The sky: a tint and a material.
        ["Sandbox.SkyBox2D"] = new[] { "Tint", "SkyIndirectLighting", "SkyMaterial" },

        // Editor-built geometry. It derives from Collider, so its collision is forced to None below and its trigger actions are not listed; a
        // Mesh whose data lives in a blob sidecar is not mirrored (the stream carries state, not files), so that component stays invisible.
        ["Sandbox.MeshComponent"] = new[] { "Mesh", "Color", "SmoothingAngle", "HideInGame", "RenderType", "Collision" },

        // Visual terrain. It derives from Collider as well, so EnableCollision is forced off below. Storage names a .terrain asset (data).
        ["Sandbox.Terrain"] = new[] { "EnableCollision", "Storage", "MaterialOverride", "TerrainSize", "TerrainHeight", "ClipMapLodLevels", "ClipMapLodExtentTexels", "SubdivisionFactor", "RenderType" },

        // Deliberately NOT listed: Sandbox.ParticleEffect and everything that needs it. Its properties include CollisionPrefab and
        // FollowerPrefab (it spawns GameObjects from prefab references, with their scripts) and OnParticleCreated/Destroyed callbacks.
    };

    /// <summary>Values written over whatever the sender sent, for the types whose base class is a collider.</summary>
    private static readonly Dictionary<string, (string Property, JsonNode Value)> Forced = new Dictionary<string, (string, JsonNode)>( StringComparer.Ordinal )
    {
        ["Sandbox.MeshComponent"] = ("Collision", "None"),
        ["Sandbox.Terrain"] = ("EnableCollision", false),
    };

    public static IEnumerable<string> AllowedTypes => Types.Keys;

    public static IReadOnlySet<string> AllowedProperties( string type ) =>
        type != null && Types.TryGetValue( type, out string[] names ) ? new HashSet<string>( names, StringComparer.Ordinal ) : new HashSet<string>();

    /// <summary>Exact, ordinal match against the list; null, empty, padded, differently cased or oversized names never match.</summary>
    public static bool IsAllowedType( string type ) => type != null && type.Length <= 64 && Types.ContainsKey( type );

    /// <summary>
    /// Builds the component a spectator may create from <paramref name="component"/>, or says why it may not. <paramref name="clean"/> is a new object
    /// that shares nothing with the input.
    /// </summary>
    public static bool TrySanitize( JsonNode component, out JsonObject clean, out string refusal )
    {
        clean = null;
        refusal = null;
        if ( component is not JsonObject source )
        {
            refusal = "A component entry is not an object.";
            return false;
        }
        string type = source["__type"] is JsonValue typeValue && typeValue.TryGetValue( out string typeName ) ? typeName : null;
        if ( !IsAllowedType( type ) )
        {
            refusal = "The component type " + Describe( type ) + " is not on the spectator whitelist.";
            return false;
        }
        if ( !( source["__guid"] is JsonValue guidValue && guidValue.TryGetValue( out string guidText ) && guidText.Length == 36 && Guid.TryParse( guidText, out Guid guid ) && guid != Guid.Empty ) )
        {
            refusal = "The component of type " + type + " has no usable __guid.";
            return false;
        }
        bool enabled = !( source["__enabled"] is JsonValue enabledValue && enabledValue.TryGetValue( out bool flag ) && !flag );
        var result = new JsonObject
        {
            ["__type"] = type,
            ["__guid"] = guid.ToString(),
            ["__enabled"] = enabled,
        };
        foreach ( string name in Types[type] )
        {
            if ( source.TryGetPropertyValue( name, out JsonNode value ) && IsPlainData( value ) )
            {
                result[name] = value?.DeepClone();
            }
        }
        if ( Forced.TryGetValue( type, out var forced ) )
        {
            result[forced.Property] = forced.Value.DeepClone();
        }
        if ( result.ToJsonString().Length > MaxComponentBytes )
        {
            refusal = "The component of type " + type + " is larger than " + MaxComponentBytes + " characters.";
            return false;
        }
        clean = result;
        return true;
    }

    /// <summary>
    /// Sanitises a Components array: refused entries are removed and counted, and the number kept is bounded. Anything that is not an array is
    /// treated as no components, never as an error, because a spectator must keep mirroring the rest of the frame.
    /// </summary>
    public static JsonArray SanitizeComponents( JsonNode components, out int stripped )
    {
        stripped = 0;
        var kept = new JsonArray();
        if ( components is not JsonArray list )
        {
            return kept;
        }
        foreach ( JsonNode entry in list )
        {
            if ( kept.Count >= MaxComponentsPerObject )
            {
                stripped++;
                continue;
            }
            if ( TrySanitize( entry, out JsonObject clean, out _ ) )
            {
                kept.Add( clean );
            }
            else
            {
                stripped++;
            }
        }
        return kept;
    }

    private static string Describe( string type ) =>
        type == null ? "(missing)" : type.Length > 48 ? "\"" + type.Substring( 0, 48 ).Replace( "\0", "" ) + "...\"" : "\"" + string.Concat( type.Where( c => !char.IsControl( c ) ) ) + "\"";

    /// <summary>
    /// Plain data only: no key that starts with <c>__</c> (a nested type discriminator or prefab patch is how the engine builds an object of another
    /// type) or <c>$</c> (a directive), bounded depth, node count and string length.
    /// </summary>
    private static bool IsPlainData( JsonNode value )
    {
        int nodes = 0;
        return IsPlainData( value, 0, ref nodes );
    }

    private static bool IsPlainData( JsonNode node, int depth, ref int nodes )
    {
        if ( node == null )
        {
            return true;
        }
        if ( ++nodes > MaxValueNodes || depth > MaxValueDepth )
        {
            return false;
        }
        switch ( node )
        {
            case JsonValue leaf:
                return !( leaf.TryGetValue( out string text ) && text.Length > MaxStringLength );
            case JsonObject obj:
                foreach ( var pair in obj )
                {
                    if ( pair.Key.StartsWith( "__", StringComparison.Ordinal ) || pair.Key.StartsWith( "$", StringComparison.Ordinal ) || !IsPlainData( pair.Value, depth + 1, ref nodes ) )
                    {
                        return false;
                    }
                }
                return true;
            case JsonArray array:
                foreach ( JsonNode item in array )
                {
                    if ( !IsPlainData( item, depth + 1, ref nodes ) )
                    {
                        return false;
                    }
                }
                return true;
            default:
                return false;
        }
    }
}
