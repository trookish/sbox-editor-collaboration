using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

public enum PlayApplyResult
{
    /// <summary>The frame was applied to the mirror.</summary>
    Applied,

    /// <summary>The frame is a repeat or older than what the mirror already holds; nothing changed.</summary>
    Ignored,

    /// <summary>The mirror has a gap (or never had a keyframe) and applied nothing; the caller asks the player for a keyframe.</summary>
    NeedsKeyframe
}

public sealed class MirrorComponent
{
    public string Guid;

    public string Type;

    public bool Enabled = true;

    public JsonObject Props = new JsonObject();

    /// <summary>Incremented whenever the type, enabled flag or properties change, so a view re-applies a component only when it must.</summary>
    public int Revision = 1;
}

public sealed class MirrorObject
{
    public int Index;

    public string Guid;

    public int Parent = -1;

    public string Name = "";

    public double[] Position = new double[3];

    public double[] Rotation = { 0, 0, 0, 1 };

    public double[] Scale = { 1, 1, 1 };

    public bool Enabled = true;

    public Dictionary<string, MirrorComponent> Components = new Dictionary<string, MirrorComponent>( StringComparer.Ordinal );
}

/// <summary>
/// The spectator's copy of a played run: plain data, no engine objects and nothing that executes. The view reads it and draws; it never asks the
/// game to run. Frames are applied only in order: a gap (a frame that never arrived, or one dropped because this spectator was behind) leaves the
/// mirror exactly as it was and asks for a keyframe, because applying a delta on top of a state it does not extend would draw something that never
/// existed.
/// </summary>
public sealed class PlayMirror
{
    /// <summary>The most objects a mirror holds. A hostile sender cannot make a spectator allocate without limit.</summary>
    public const int MaxObjects = 20000;

    public const int MaxComponentsPerObject = SpectateWhitelist.MaxComponentsPerObject;

    private readonly Dictionary<int, MirrorObject> objects = new Dictionary<int, MirrorObject>();

    public IReadOnlyDictionary<int, MirrorObject> Objects => objects;

    public long LastSeq { get; private set; }

    /// <summary>True until a keyframe has been applied, and again after a gap.</summary>
    public bool AwaitingKeyframe { get; private set; } = true;

    /// <summary>Incremented by every applied frame, so a view can tell cheaply that it must refresh.</summary>
    public long Version { get; private set; }

    /// <summary>References to objects the mirror does not hold (a delta for something never created). Normally zero.</summary>
    public int UnknownReferences { get; private set; }

    public void Reset()
    {
        objects.Clear();
        LastSeq = 0;
        AwaitingKeyframe = true;
        Version++;
    }

    public PlayApplyResult Apply( PlayFrame frame )
    {
        if ( frame == null )
        {
            return PlayApplyResult.Ignored;
        }
        if ( frame.Key )
        {
            if ( !AwaitingKeyframe && frame.Seq <= LastSeq )
            {
                return PlayApplyResult.Ignored;
            }
            objects.Clear();
            AwaitingKeyframe = false;
        }
        else
        {
            if ( AwaitingKeyframe )
            {
                return PlayApplyResult.NeedsKeyframe;
            }
            if ( frame.Seq <= LastSeq )
            {
                return PlayApplyResult.Ignored;
            }
            if ( frame.Seq != LastSeq + 1 )
            {
                AwaitingKeyframe = true;
                return PlayApplyResult.NeedsKeyframe;
            }
        }
        LastSeq = frame.Seq;
        Version++;
        int unknown = 0;
        foreach ( JsonNode node in frame.Created ) Create( node as JsonObject );
        foreach ( JsonNode node in frame.Destroyed ) objects.Remove( (int)node );
        foreach ( JsonNode node in frame.Moved ) unknown += Move( node.AsArray() ) ? 0 : 1;
        foreach ( JsonNode node in frame.Enabled )
        {
            if ( objects.TryGetValue( (int)node[0], out MirrorObject o ) ) o.Enabled = (bool)node[1];
            else unknown++;
        }
        foreach ( JsonNode node in frame.Changed ) unknown += Change( node as JsonObject ) ? 0 : 1;
        foreach ( JsonNode node in frame.RemovedComponents )
        {
            if ( objects.TryGetValue( (int)node[0], out MirrorObject o ) ) o.Components.Remove( (string)node[1] );
            else unknown++;
        }
        UnknownReferences += unknown;
        if ( unknown > 0 )
        {
            // The sender referred to something this mirror never saw: it is out of step in a way the sequence check cannot see.
            AwaitingKeyframe = true;
            return PlayApplyResult.NeedsKeyframe;
        }
        return PlayApplyResult.Applied;
    }

    private void Create( JsonObject entry )
    {
        if ( entry == null || objects.Count >= MaxObjects && !objects.ContainsKey( (int)entry["i"] ) )
        {
            return;
        }
        var o = new MirrorObject { Index = (int)entry["i"], Guid = (string)entry["u"], Parent = entry["p"] is JsonValue p && p.TryGetValue( out int pv ) ? pv : -1, Name = entry["n"] is JsonValue n && n.TryGetValue( out string name ) ? name : "" };
        if ( entry["t"] is JsonArray t && t.Count == 10 )
        {
            for ( int i = 0; i < 3; i++ ) o.Position[i] = (double)t[i];
            for ( int i = 0; i < 4; i++ ) o.Rotation[i] = (double)t[3 + i];
            for ( int i = 0; i < 3; i++ ) o.Scale[i] = (double)t[7 + i];
        }
        o.Enabled = !( entry["e"] is JsonValue e && e.TryGetValue( out bool enabled ) && !enabled );
        // The receiver sanitises again whatever the frame says: a frame built in process, or by a future code path, must not be able to bypass the list.
        foreach ( JsonNode c in SpectateWhitelist.SanitizeComponents( entry["k"], out _ ) )
        {
            JsonObject co = c.AsObject();
            var component = new MirrorComponent { Guid = (string)co["__guid"], Type = (string)co["__type"], Enabled = (bool)co["__enabled"] };
            foreach ( var prop in co )
            {
                if ( !prop.Key.StartsWith( "__", StringComparison.Ordinal ) ) component.Props[prop.Key] = prop.Value?.DeepClone();
            }
            o.Components[component.Guid] = component;
        }
        objects[o.Index] = o;
    }

    private bool Move( JsonArray entry )
    {
        if ( !objects.TryGetValue( (int)entry[0], out MirrorObject o ) )
        {
            return false;
        }
        int mask = (int)entry[1];
        int at = 2;
        if ( ( mask & 1 ) != 0 ) for ( int i = 0; i < 3; i++ ) o.Position[i] = (double)entry[at++];
        if ( ( mask & 2 ) != 0 ) for ( int i = 0; i < 4; i++ ) o.Rotation[i] = (double)entry[at++];
        if ( ( mask & 4 ) != 0 ) for ( int i = 0; i < 3; i++ ) o.Scale[i] = (double)entry[at++];
        return true;
    }

    private bool Change( JsonObject entry )
    {
        if ( entry == null || !objects.TryGetValue( (int)entry["i"], out MirrorObject o ) )
        {
            return false;
        }
        string guid = (string)entry["g"];
        if ( !o.Components.TryGetValue( guid, out MirrorComponent component ) )
        {
            if ( entry["y"] is not JsonValue y || !y.TryGetValue( out string type ) || !SpectateWhitelist.IsAllowedType( type ) )
            {
                return false;
            }
            if ( o.Components.Count >= MaxComponentsPerObject )
            {
                return true;
            }
            component = new MirrorComponent { Guid = guid, Type = type };
            o.Components[guid] = component;
        }
        component.Revision++;
        if ( entry["a"] is JsonValue a && a.TryGetValue( out bool enabled ) ) component.Enabled = enabled;
        if ( entry["p"] is JsonObject props )
        {
            foreach ( var prop in SpectateWhitelist.FilterProperties( component.Type, props ).ToList() ) component.Props[prop.Key] = prop.Value?.DeepClone();
        }
        return true;
    }
}
