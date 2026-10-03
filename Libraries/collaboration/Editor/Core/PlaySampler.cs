using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// The player's side of shared play: compares the scene of the run with what it last sent and turns the difference into frames. It holds no engine
/// type; the editor glue reads the runtime scene into <see cref="PlayObject"/> values and hands them over twenty times a second.
/// </summary>
/// <remarks>
/// What is bounded, and why: at most <see cref="MaxMovedPerFrame"/> moved entries per sample, chosen oldest-sent first so a scene with more moving
/// objects than that still reaches every one of them in turn; at most <see cref="MaxChangedPerFrame"/> component changes; and no frame larger than
/// <see cref="MaxFrameBytes"/>, with created and destroyed first because a frame that moves an object the receiver has not been told about is wasted.
/// A full keyframe goes out every <see cref="KeyframeIntervalMs"/> (and on request), so a spectator that joined late or lost a frame recovers on its own.
/// Whatever the sender cannot send this time stays different from "last sent" and is therefore sent next time: nothing is ever skipped permanently.
/// </remarks>
public sealed class PlaySampler
{
    public const int MaxMovedPerFrame = 200;

    public const int MaxChangedPerFrame = 100;

    public const int MaxFrameBytes = 256 * 1024;

    public const double KeyframeIntervalMs = 5000;

    /// <summary>One created entry may take at most this much of a frame; a larger one is sent without its components rather than not at all.</summary>
    public const int MaxEntryBytes = MaxFrameBytes / 2;

    /// <summary>Objects tracked at once; a scene beyond this is cut (the spectator view is a mirror, not the whole of a huge map).</summary>
    public const int MaxObjects = 20000;

    private const double PositionStep = 0.001;

    private const double RotationStep = 0.0001;

    private const double Epsilon = 1e-9;

    private sealed class TrackedComponent
    {
        public string Type;

        public bool Enabled;

        public Dictionary<string, string> Props = new Dictionary<string, string>( StringComparer.Ordinal );
    }

    private sealed class Tracked
    {
        public int Index;

        public string Id;

        public string Parent;

        public string Name;

        public double[] Position = new double[3];

        public double[] Rotation = new double[4];

        public double[] Scale = new double[3];

        public bool Enabled = true;

        public bool Sent;

        public long LastMovedSeq;

        public Dictionary<string, TrackedComponent> Components = new Dictionary<string, TrackedComponent>( StringComparer.Ordinal );
    }

    private readonly Dictionary<string, Tracked> tracked = new Dictionary<string, Tracked>( StringComparer.Ordinal );

    private long seq;

    private int nextIndex;

    private double lastKeyMs;

    private bool started;

    private bool keyRequested;

    public int TrackedCount => tracked.Count;

    /// <summary>The sequence number of the last frame produced (0 before any).</summary>
    public long LastSeq => seq;

    /// <summary>Asks for a full keyframe on the next sample: a late joiner or a spectator that noticed a gap needs one.</summary>
    public void RequestKeyframe() => keyRequested = true;

    public IReadOnlyList<PlayFrame> Sample( IReadOnlyList<PlayObject> scene, double nowMs )
    {
        var frames = new List<PlayFrame>();
        var current = new Dictionary<string, PlayObject>( StringComparer.Ordinal );
        foreach ( PlayObject o in scene ?? Array.Empty<PlayObject>() )
        {
            if ( o == null || string.IsNullOrEmpty( o.Id ) || o.Id.Length > 64 || current.ContainsKey( o.Id ) || current.Count >= MaxObjects ) continue;
            current[o.Id] = o;
        }

        bool key = !started || keyRequested || nowMs - lastKeyMs >= KeyframeIntervalMs;
        if ( key )
        {
            // The receiver forgets everything on a keyframe, so everything is described again under new indices.
            tracked.Clear();
            started = true;
            keyRequested = false;
            lastKeyMs = nowMs;
        }

        var builder = new Builder( this, frames, key );
        var destroyed = tracked.Where( t => !current.ContainsKey( t.Key ) ).Select( t => t.Value ).ToList();
        foreach ( Tracked gone in destroyed )
        {
            tracked.Remove( gone.Id );
            if ( gone.Sent ) builder.Destroyed( gone.Index );
        }

        var created = new List<PlayObject>();
        foreach ( PlayObject o in current.Values )
        {
            if ( !tracked.ContainsKey( o.Id ) ) created.Add( o );
        }
        foreach ( PlayObject o in Ordered( created, current ) )
        {
            var t = new Tracked { Index = nextIndex++, Id = o.Id, Parent = o.Parent, Name = o.Name ?? "" };
            Quantise( o, t );
            t.Enabled = o.Enabled;
            JsonArray components = SpectateWhitelist.SanitizeComponents( o.Components, out _ );
            foreach ( JsonNode c in components ) Remember( t, c.AsObject() );
            tracked[o.Id] = t;
            t.Sent = true;
            builder.Created( Describe( t, components, current ) );
        }
        int createdNow = created.Count;

        var enabled = new List<(Tracked, bool)>();
        var changes = new List<(Tracked, JsonObject)>();
        var removals = new List<(Tracked, string)>();
        var movers = new List<(Tracked T, PlayObject O)>();
        var createdIds = new HashSet<string>( created.Select( c => c.Id ), StringComparer.Ordinal );
        foreach ( var pair in tracked )
        {
            Tracked t = pair.Value;
            if ( createdIds.Contains( t.Id ) ) continue;
            PlayObject o = current[t.Id];
            if ( t.Enabled != o.Enabled ) enabled.Add( (t, o.Enabled) );
            JsonArray components = SpectateWhitelist.SanitizeComponents( o.Components, out _ );
            var present = new HashSet<string>( StringComparer.Ordinal );
            foreach ( JsonNode c in components )
            {
                JsonObject co = c.AsObject();
                string guid = (string)co["__guid"];
                present.Add( guid );
                JsonObject delta = Delta( t, co );
                if ( delta != null ) changes.Add( (t, delta) );
            }
            foreach ( string guid in t.Components.Keys.Where( g => !present.Contains( g ) ) ) removals.Add( (t, guid) );
            if ( Differs( t, o ) ) movers.Add( (t, o) );
        }

        foreach ( var (t, value) in enabled )
        {
            t.Enabled = value;
            builder.Enabled( t.Index, value );
        }
        foreach ( var (t, guid) in removals )
        {
            t.Components.Remove( guid );
            builder.Removed( t.Index, guid );
        }
        int changedCount = 0;
        foreach ( var (t, delta) in changes )
        {
            if ( changedCount >= MaxChangedPerFrame ) break;
            if ( builder.Changed( t.Index, delta ) )
            {
                Apply( t, delta );
                changedCount++;
            }
        }

        // Oldest-sent first: a scene with more movers than fit still reaches all of them, each exactly once per pass.
        int movedCount = 0;
        foreach ( var (t, o) in movers.OrderBy( m => m.T.LastMovedSeq ).ThenBy( m => m.T.Index ) )
        {
            if ( movedCount >= MaxMovedPerFrame ) break;
            JsonArray entry = MoveEntry( t, o );
            if ( entry == null ) continue;
            if ( !builder.Moved( entry ) ) break;
            t.LastMovedSeq = builder.CurrentSeq;
            Commit( t, o, (int)entry[1] );
            movedCount++;
        }

        builder.Finish();
        return frames;
    }

    private static IEnumerable<PlayObject> Ordered( List<PlayObject> created, Dictionary<string, PlayObject> all )
    {
        // Parents before children, so a split keyframe never describes a child before its parent.
        int Depth( PlayObject o )
        {
            int depth = 0;
            for ( string p = o.Parent; p != null && depth < 64 && all.TryGetValue( p, out PlayObject parent ); p = parent.Parent ) depth++;
            return depth;
        }
        return created.Select( ( o, i ) => (o, i, d: Depth( o )) ).OrderBy( x => x.d ).ThenBy( x => x.i ).Select( x => x.o );
    }

    private JsonObject Describe( Tracked t, JsonArray components, Dictionary<string, PlayObject> current )
    {
        int parent = t.Parent != null && tracked.TryGetValue( t.Parent, out Tracked p ) ? p.Index : -1;
        var entry = new JsonObject
        {
            ["i"] = t.Index,
            ["u"] = t.Id,
            ["p"] = parent,
            ["n"] = t.Name,
            ["t"] = new JsonArray( t.Position.Concat( t.Rotation ).Concat( t.Scale ).Select( v => (JsonNode)JsonValue.Create( v ) ).ToArray() ),
        };
        if ( !t.Enabled ) entry["e"] = false;
        if ( components.Count > 0 ) entry["k"] = components.DeepClone();
        return entry;
    }

    private static double Round( float v, int decimals ) => float.IsFinite( v ) ? Math.Round( (double)v, decimals, MidpointRounding.AwayFromZero ) : 0;

    private static void Quantise( PlayObject o, Tracked t )
    {
        t.Position = new[] { Round( o.Position.X, 3 ), Round( o.Position.Y, 3 ), Round( o.Position.Z, 3 ) };
        t.Rotation = new[] { Round( o.Rotation.X, 4 ), Round( o.Rotation.Y, 4 ), Round( o.Rotation.Z, 4 ), Round( o.Rotation.W, 4 ) };
        t.Scale = new[] { Round( o.Scale.X, 3 ), Round( o.Scale.Y, 3 ), Round( o.Scale.Z, 3 ) };
    }

    private static bool Beyond( double[] a, double[] b, double step ) => a.Where( ( v, i ) => Math.Abs( v - b[i] ) >= step - Epsilon ).Any();

    private static bool Differs( Tracked t, PlayObject o )
    {
        var probe = new Tracked();
        Quantise( o, probe );
        return Beyond( probe.Position, t.Position, PositionStep ) || Beyond( probe.Rotation, t.Rotation, RotationStep ) || Beyond( probe.Scale, t.Scale, PositionStep );
    }

    private static JsonArray MoveEntry( Tracked t, PlayObject o )
    {
        var probe = new Tracked();
        Quantise( o, probe );
        int mask = ( Beyond( probe.Position, t.Position, PositionStep ) ? 1 : 0 ) | ( Beyond( probe.Rotation, t.Rotation, RotationStep ) ? 2 : 0 ) | ( Beyond( probe.Scale, t.Scale, PositionStep ) ? 4 : 0 );
        if ( mask == 0 ) return null;
        var entry = new JsonArray { t.Index, mask };
        if ( ( mask & 1 ) != 0 ) foreach ( double v in probe.Position ) entry.Add( v );
        if ( ( mask & 2 ) != 0 ) foreach ( double v in probe.Rotation ) entry.Add( v );
        if ( ( mask & 4 ) != 0 ) foreach ( double v in probe.Scale ) entry.Add( v );
        return entry;
    }

    private static void Commit( Tracked t, PlayObject o, int mask )
    {
        var probe = new Tracked();
        Quantise( o, probe );
        if ( ( mask & 1 ) != 0 ) t.Position = probe.Position;
        if ( ( mask & 2 ) != 0 ) t.Rotation = probe.Rotation;
        if ( ( mask & 4 ) != 0 ) t.Scale = probe.Scale;
    }

    private static void Remember( Tracked t, JsonObject component )
    {
        var c = new TrackedComponent { Type = (string)component["__type"], Enabled = (bool)component["__enabled"] };
        foreach ( var p in component )
        {
            if ( !p.Key.StartsWith( "__", StringComparison.Ordinal ) ) c.Props[p.Key] = p.Value?.ToJsonString() ?? "null";
        }
        t.Components[(string)component["__guid"]] = c;
    }

    /// <summary>What differs between the component as last sent and as it is now, or null when nothing does. A new component carries its type and everything.</summary>
    private static JsonObject Delta( Tracked t, JsonObject component )
    {
        string guid = (string)component["__guid"];
        bool enabled = (bool)component["__enabled"];
        var props = new JsonObject();
        if ( !t.Components.TryGetValue( guid, out TrackedComponent known ) )
        {
            foreach ( var p in component ) if ( !p.Key.StartsWith( "__", StringComparison.Ordinal ) ) props[p.Key] = p.Value?.DeepClone();
            return new JsonObject { ["g"] = guid, ["y"] = (string)component["__type"], ["a"] = enabled, ["p"] = props };
        }
        foreach ( var p in component )
        {
            if ( p.Key.StartsWith( "__", StringComparison.Ordinal ) ) continue;
            string text = p.Value?.ToJsonString() ?? "null";
            if ( !known.Props.TryGetValue( p.Key, out string before ) || before != text ) props[p.Key] = p.Value?.DeepClone();
        }
        if ( props.Count == 0 && known.Enabled == enabled ) return null;
        var delta = new JsonObject { ["g"] = guid };
        if ( known.Enabled != enabled ) delta["a"] = enabled;
        if ( props.Count > 0 ) delta["p"] = props;
        return delta;
    }

    private static void Apply( Tracked t, JsonObject delta )
    {
        string guid = (string)delta["g"];
        if ( !t.Components.TryGetValue( guid, out TrackedComponent c ) )
        {
            c = new TrackedComponent { Type = (string)delta["y"] };
            t.Components[guid] = c;
        }
        if ( delta["a"] is JsonValue a ) c.Enabled = (bool)a;
        else if ( delta["y"] != null ) c.Enabled = true;
        if ( delta["p"] is JsonObject props ) foreach ( var p in props ) c.Props[p.Key] = p.Value?.ToJsonString() ?? "null";
    }

    /// <summary>Collects entries into frames of at most <see cref="MaxFrameBytes"/> characters, opening a new frame (with the next sequence number) when one is full.</summary>
    private sealed class Builder
    {
        private readonly PlaySampler owner;

        private readonly List<PlayFrame> frames;

        private readonly bool key;

        private PlayFrame frame;

        private int size;

        public Builder( PlaySampler owner, List<PlayFrame> frames, bool key )
        {
            this.owner = owner;
            this.frames = frames;
            this.key = key;
        }

        public long CurrentSeq => frame?.Seq ?? owner.seq;

        private PlayFrame Open()
        {
            if ( frame == null )
            {
                frame = new PlayFrame { Seq = ++owner.seq, Key = key && frames.Count == 0 };
                size = 64;
            }
            return frame;
        }

        private void Seal()
        {
            if ( frame != null && ( !frame.IsEmpty || frame.Key ) ) frames.Add( frame );
            frame = null;
        }

        private bool Fits( int bytes ) => frame == null || size + bytes <= MaxFrameBytes;

        private PlayFrame Room( int bytes )
        {
            if ( !Fits( bytes ) ) Seal();
            return Open();
        }

        public void Destroyed( int index )
        {
            Room( 12 ).Destroyed.Add( index );
            size += 12;
        }

        public void Created( JsonObject entry )
        {
            int bytes = entry.ToJsonString().Length;
            if ( bytes > MaxEntryBytes )
            {
                entry.Remove( "k" );
                bytes = entry.ToJsonString().Length;
            }
            Room( bytes ).Created.Add( entry );
            size += bytes + 1;
        }

        public void Enabled( int index, bool value )
        {
            Room( 16 ).Enabled.Add( new JsonArray { index, value } );
            size += 16;
        }

        public void Removed( int index, string guid )
        {
            Room( 56 ).RemovedComponents.Add( new JsonArray { index, guid } );
            size += 56;
        }

        public bool Changed( int index, JsonObject delta )
        {
            var entry = new JsonObject { ["i"] = index };
            foreach ( var p in delta.ToList() ) entry[p.Key] = p.Value?.DeepClone();
            int bytes = entry.ToJsonString().Length;
            if ( bytes > MaxEntryBytes ) return false;
            Room( bytes ).Changed.Add( entry );
            size += bytes + 1;
            return true;
        }

        public bool Moved( JsonArray entry )
        {
            int bytes = entry.ToJsonString().Length + 1;
            Room( bytes ).Moved.Add( entry );
            size += bytes;
            return true;
        }

        public void Finish()
        {
            // A keyframe of an empty scene still has to reach the receiver: it is what makes the spectator forget the previous run's objects.
            if ( key && frames.Count == 0 && frame == null ) Open();
            Seal();
        }
    }
}
