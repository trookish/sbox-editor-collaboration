using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// Reads and writes a scene's own settings (<see cref="SettingsMerge"/>) on the live editor scene.
///
/// Reading is the engine's public <c>Scene.SerializeProperties()</c> (Sandbox.Engine.dll, Scene/Scene.LoadSave.cs). Its counterpart,
/// <c>Scene.DeserializeProperties(JsonObject, bool)</c>, is PRIVATE (as are the two helpers it calls), and the only public path that reaches it, <c>Scene.Deserialize</c>,
/// first clears every object in the scene, so it cannot be used on a scene people are editing. <see cref="Apply"/> therefore repeats what
/// <c>DeserializeProperties</c> does, step for step, through public API only (decompiled from the installed Sandbox.Engine.dll with ilspycmd):
/// <list type="number">
/// <item>every <c>[Property]</c> of <c>Scene</c> in <c>Game.TypeLibrary</c> except <c>Enabled</c>, <c>Name</c> and <c>Lerp</c> is set from the key of
/// the same name with <c>Json.FromNode( node, property.PropertyType )</c> and <c>PropertyDescription.SetValue</c>;</item>
/// <item><c>GameObjectSystems</c> goes through what the internal <c>Scene.ApplyGameObjectSystemOverrides(JsonNode)</c> does: each entry is named by
/// a system type's <c>FullName</c> and sets that system's writable <c>[Property]</c> values. The engine walks the internal <c>Scene.GetSystems()</c>;
/// here the entry's type is resolved in <c>Game.TypeLibrary</c> and its instance fetched with the public <c>Scene.GetSystem&lt;T&gt;()</c>;</item>
/// <item><c>NavMesh</c> goes through what the internal <c>NavMesh.Deserialize(JsonObject)</c> does, with the same public setters and the same
/// fallbacks (a missing key keeps the current value, a missing <c>Enabled</c> means disabled). <c>BakedDataPath</c> has no public setter and is
/// left as the scene has it.</item>
/// </list>
/// <c>Metadata</c> is not written: the engine computes it from components (<c>SerializeMetadata</c>) and never reads it back either. Like the
/// engine, a value that cannot be converted is skipped rather than aborting the rest; unlike the engine, which only logs, each skip is
/// returned so the caller can tell the user, and the editor's own re-read (the echo, see <see cref="SettingsMerge.LocalChange"/>) keeps a
/// skipped value from being sent back as a change. A key absent from the settings is left as the scene has it, as the engine does.
/// </summary>
public static class SceneSettingsAdapter
{
    private static readonly HashSet<string> NotSettings = new HashSet<string>( StringComparer.Ordinal ) { "Enabled", "Name", "Lerp" };

    /// <summary>The scene's settings exactly as the engine writes them into a scene file.</summary>
    public static JsonObject Capture( Scene scene )
    {
        return scene.SerializeProperties();
    }

    /// <summary>Writes <paramref name="settings"/> into <paramref name="scene"/>; returns one formal line per value the engine could not take (empty when all were taken).</summary>
    public static List<string> Apply( Scene scene, JsonObject settings )
    {
        var skipped = new List<string>();
        if ( scene == null || settings == null )
        {
            return skipped;
        }
        TypeDescription sceneType = Game.TypeLibrary.GetType<Scene>();
        if ( sceneType == null )
        {
            skipped.Add( "The engine's type library does not describe Scene, so no scene setting could be applied." );
            return skipped;
        }
        foreach ( PropertyDescription property in sceneType.Properties.Where( x => x.HasAttribute<Sandbox.PropertyAttribute>() ) )
        {
            // The engine also tries read-only properties and logs the failure; skipping them up front only spares that noise.
            if ( NotSettings.Contains( property.Name ) || !property.CanWrite || !settings.TryGetPropertyValue( property.Name, out JsonNode node ) )
            {
                continue;
            }
            SetOne( scene, property, node, property.Name, skipped );
        }
        if ( settings.TryGetPropertyValue( "GameObjectSystems", out JsonNode systemsNode ) && systemsNode is JsonObject systems )
        {
            foreach ( KeyValuePair<string, JsonNode> entry in systems )
            {
                if ( !( entry.Value is JsonObject values ) )
                {
                    continue;
                }
                GameObjectSystem system = FindSystem( scene, entry.Key );
                if ( system == null )
                {
                    // The engine skips an entry for a system this scene does not run (a type the project no longer has) without a word.
                    continue;
                }
                TypeDescription systemType = Game.TypeLibrary.GetType( system.GetType() );
                foreach ( PropertyDescription property in systemType.Properties.Where( x => x.HasAttribute<Sandbox.PropertyAttribute>() ) )
                {
                    if ( property.CanWrite && values.TryGetPropertyValue( property.Name, out JsonNode node ) )
                    {
                        SetOne( system, property, node, entry.Key + "." + property.Name, skipped );
                    }
                }
            }
        }
        if ( settings["NavMesh"] is JsonObject navMesh )
        {
            try
            {
                ApplyNavMesh( scene.NavMesh, navMesh );
            }
            catch ( Exception ex )
            {
                skipped.Add( "NavMesh: " + ex.Message );
            }
        }
        return skipped;
    }

    private static readonly MethodInfo GetSystemDefinition = typeof( Scene ).GetMethod( nameof( Scene.GetSystem ), Type.EmptyTypes );

    private static GameObjectSystem FindSystem( Scene scene, string fullName )
    {
        TypeDescription type = Game.TypeLibrary.GetType( fullName );
        if ( type?.TargetType == null || !typeof( GameObjectSystem ).IsAssignableFrom( type.TargetType ) || GetSystemDefinition == null )
        {
            return null;
        }
        return GetSystemDefinition.MakeGenericMethod( type.TargetType ).Invoke( scene, null ) as GameObjectSystem;
    }

    // Line for line the internal NavMesh.Deserialize(JsonObject) (Game/Navigation/NavMesh.Baking.cs), minus the baked-data path it alone can set.
    private static void ApplyNavMesh( Sandbox.Navigation.NavMesh navMesh, JsonObject json )
    {
        navMesh.IncludeStaticBodies = (bool)( json["IncludeStaticBodies"] ?? (JsonNode)navMesh.IncludeStaticBodies );
        navMesh.IncludeKeyframedBodies = (bool)( json["IncludeKeyframedBodies"] ?? (JsonNode)navMesh.IncludeKeyframedBodies );
        navMesh.EditorAutoUpdate = (bool)( json["EditorAutoUpdate"] ?? (JsonNode)navMesh.EditorAutoUpdate );
        navMesh.AgentHeight = (float)( json["AgentHeight"] ?? (JsonNode)navMesh.AgentHeight );
        navMesh.AgentRadius = (float)( json["AgentRadius"] ?? (JsonNode)navMesh.AgentRadius );
        navMesh.AgentStepSize = (float)( json["AgentStepSize"] ?? (JsonNode)navMesh.AgentStepSize );
        navMesh.AgentMaxSlope = (float)( json["AgentMaxSlope"] ?? (JsonNode)navMesh.AgentMaxSlope );
        navMesh.ExcludedBodies = Json.FromNode<TagSet>( json["ExcludedBodies"] ) ?? navMesh.ExcludedBodies;
        navMesh.IncludedBodies = Json.FromNode<TagSet>( json["IncludedBodies"] ) ?? navMesh.IncludedBodies;
        navMesh.DeferGeneration = (bool)( json["DeferGeneration"] ?? (JsonNode)navMesh.DeferGeneration );
        navMesh.CustomBounds = (bool)( json["CustomBounds"] ?? (JsonNode)navMesh.CustomBounds );
        navMesh.Bounds = ( navMesh.CustomBounds && json["Bounds"] != null ) ? Json.FromNode<BBox>( json["Bounds"] ) : default;
        navMesh.IsEnabled = (bool)( json["Enabled"] ?? (JsonNode)false );
    }

    private static void SetOne( object target, PropertyDescription property, JsonNode node, string label, List<string> skipped )
    {
        try
        {
            property.SetValue( target, Json.FromNode( node, property.PropertyType ) );
        }
        catch ( Exception ex )
        {
            skipped.Add( label + ": " + ex.Message );
        }
    }
}
