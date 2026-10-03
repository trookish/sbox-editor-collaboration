using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TeamCreate;

public readonly record struct Vec3( float X, float Y, float Z );

public readonly record struct Quat( float X, float Y, float Z, float W )
{
    public static Quat Identity => new Quat( 0, 0, 0, 1 );
}

/// <summary>
/// One object of a played run, as the engine glue reads it. Position, rotation and scale are WORLD values, so a spectator can place every object
/// without resolving a hierarchy (and a re-parenting needs no message of its own); <see cref="Parent"/> is kept only so the mirror can show the
/// tree. <see cref="Components"/> are the raw component states; the sampler strips them to the visual whitelist before anything is sent.
/// </summary>
public sealed record PlayObject( string Id, string Parent, string Name, Vec3 Position, Quat Rotation, Vec3 Scale, bool Enabled, JsonArray Components );

/// <summary>
/// One frame of a played run, as it goes on the wire inside <c>Packet.Play</c>. Objects are addressed by an index the sampler hands out per run (a
/// 36-character guid in every moved entry would triple the bandwidth). Entry shapes:
/// <list type="bullet">
/// <item><c>c</c> created: <c>{ i, u (guid), p (parent index or -1), n (name), t [px,py,pz, rx,ry,rz,rw, sx,sy,sz], e (false when disabled), k [components] }</c></item>
/// <item><c>d</c> destroyed: indices</item>
/// <item><c>m</c> moved: <c>[i, mask, values...]</c>, mask 1 = position (3 numbers), 2 = rotation (4), 4 = scale (3), values in that order</item>
/// <item><c>e</c> enabled: <c>[i, bool]</c></item>
/// <item><c>x</c> component changed or added: <c>{ i, g (guid), y (type, only for a new component), a (enabled), p (changed properties) }</c></item>
/// <item><c>r</c> component removed: <c>[i, guid]</c></item>
/// </list>
/// <c>s</c> is the sequence number and <c>k</c> true marks a keyframe: the receiver discards everything it had and rebuilds from this frame (and the
/// frames that follow it, when a large keyframe was split).
/// </summary>
public sealed class PlayFrame
{
    public const int MaxEntries = 4096;

    public const int MaxIndex = 10_000_000;

    public long Seq { get; set; }

    public bool Key { get; set; }

    public JsonArray Created { get; } = new JsonArray();

    public JsonArray Destroyed { get; } = new JsonArray();

    public JsonArray Moved { get; } = new JsonArray();

    public JsonArray Enabled { get; } = new JsonArray();

    public JsonArray Changed { get; } = new JsonArray();

    public JsonArray RemovedComponents { get; } = new JsonArray();

    /// <summary>On a frame parsed from the wire: how many component entries the whitelist refused. Zero on a frame the sampler built.</summary>
    public int Stripped { get; set; }

    public bool IsEmpty => Created.Count + Destroyed.Count + Moved.Count + Enabled.Count + Changed.Count + RemovedComponents.Count == 0;

    public JsonObject ToJson()
    {
        var o = new JsonObject { ["s"] = Seq };
        if ( Key ) o["k"] = true;
        Put( o, "c", Created );
        Put( o, "d", Destroyed );
        Put( o, "m", Moved );
        Put( o, "e", Enabled );
        Put( o, "x", Changed );
        Put( o, "r", RemovedComponents );
        return o;
    }

    private static void Put( JsonObject o, string key, JsonArray list )
    {
        if ( list.Count > 0 ) o[key] = list.DeepClone();
    }

    /// <summary>
    /// Reads a frame from the wire. The sender is another person's editor, so everything is checked again here: bounded entry counts, sane indices,
    /// and every component of a created entry passes the whitelist (refused ones are dropped and counted). Anything malformed refuses the whole frame.
    /// </summary>
    public static bool TryParse( JsonNode node, out PlayFrame frame, out string error )
    {
        frame = null;
        error = null;
        if ( node is not JsonObject o )
        {
            error = "A play frame is not an object.";
            return false;
        }
        var result = new PlayFrame();
        try
        {
            result.Seq = o["s"] is JsonValue s && s.TryGetValue( out long seq ) && seq >= 0 ? seq : throw new FormatException( "The frame has no valid sequence number." );
            result.Key = o["k"] is JsonValue k && k.TryGetValue( out bool key ) && key;
            foreach ( JsonNode c in List( o, "c" ) )
            {
                if ( c is not JsonObject co ) throw new FormatException( "A created entry is not an object." );
                int index = Index( co["i"] );
                string guid = co["u"] is JsonValue u && u.TryGetValue( out string g ) && g.Length == 36 && Guid.TryParse( g, out _ ) ? g : throw new FormatException( "A created entry has no valid guid." );
                int parent = co["p"] is JsonValue p && p.TryGetValue( out int pv ) && pv >= -1 && pv <= MaxIndex ? pv : -1;
                string name = co["n"] is JsonValue n && n.TryGetValue( out string nv ) ? Cap( nv, 256 ) : "";
                var t = Numbers( co["t"], 10, "transform" );
                var clean = new JsonObject { ["i"] = index, ["u"] = guid, ["p"] = parent, ["n"] = name, ["t"] = t };
                if ( co["e"] is JsonValue e && e.TryGetValue( out bool enabled ) && !enabled ) clean["e"] = false;
                var components = SpectateWhitelist.SanitizeComponents( co["k"], out int stripped );
                result.Stripped += stripped;
                if ( components.Count > 0 ) clean["k"] = components;
                result.Created.Add( clean );
            }
            foreach ( JsonNode d in List( o, "d" ) ) result.Destroyed.Add( Index( d ) );
            foreach ( JsonNode m in List( o, "m" ) )
            {
                if ( m is not JsonArray ma || ma.Count < 3 ) throw new FormatException( "A moved entry is malformed." );
                int mask = ma[1] is JsonValue mv && mv.TryGetValue( out int bits ) && bits >= 1 && bits <= 7 ? bits : throw new FormatException( "A moved entry has no valid mask." );
                int expected = 2 + ( ( mask & 1 ) != 0 ? 3 : 0 ) + ( ( mask & 2 ) != 0 ? 4 : 0 ) + ( ( mask & 4 ) != 0 ? 3 : 0 );
                if ( ma.Count != expected ) throw new FormatException( "A moved entry does not match its mask." );
                var entry = new JsonArray { Index( ma[0] ), mask };
                for ( int i = 2; i < expected; i++ ) entry.Add( Finite( ma[i] ) );
                result.Moved.Add( entry );
            }
            foreach ( JsonNode e in List( o, "e" ) )
            {
                if ( e is not JsonArray ea || ea.Count != 2 || !( ea[1] is JsonValue ev && ev.TryGetValue( out bool value ) ) ) throw new FormatException( "An enabled entry is malformed." );
                result.Enabled.Add( new JsonArray { Index( ea[0] ), value } );
            }
            foreach ( JsonNode x in List( o, "x" ) )
            {
                if ( x is not JsonObject xo ) throw new FormatException( "A changed entry is not an object." );
                var clean = new JsonObject { ["i"] = Index( xo["i"] ) };
                clean["g"] = xo["g"] is JsonValue g && g.TryGetValue( out string gv ) && gv.Length == 36 && Guid.TryParse( gv, out _ ) ? gv : throw new FormatException( "A changed entry has no valid guid." );
                if ( xo["y"] is JsonValue y )
                {
                    if ( !( y.TryGetValue( out string type ) && SpectateWhitelist.IsAllowedType( type ) ) )
                    {
                        // A component of a type that is not on the list is not an error of the frame: it is one more thing the spectator never creates.
                        result.Stripped++;
                        continue;
                    }
                    clean["y"] = type;
                }
                if ( xo["a"] is JsonValue a && a.TryGetValue( out bool en ) ) clean["a"] = en;
                if ( xo["p"] is JsonObject props ) clean["p"] = props.DeepClone();
                result.Changed.Add( clean );
            }
            foreach ( JsonNode r in List( o, "r" ) )
            {
                if ( r is not JsonArray ra || ra.Count != 2 || !( ra[1] is JsonValue rv && rv.TryGetValue( out string rg ) && Guid.TryParse( rg, out _ ) ) ) throw new FormatException( "A removed-component entry is malformed." );
                result.RemovedComponents.Add( new JsonArray { Index( ra[0] ), rg } );
            }
        }
        catch ( FormatException ex )
        {
            error = ex.Message;
            return false;
        }
        frame = result;
        return true;
    }

    private static JsonArray List( JsonObject o, string key )
    {
        if ( !o.TryGetPropertyValue( key, out JsonNode node ) || node == null ) return new JsonArray();
        if ( node is not JsonArray list || list.Count > MaxEntries ) throw new FormatException( "The frame list '" + key + "' is not a list of at most " + MaxEntries + " entries." );
        return list;
    }

    private static int Index( JsonNode node ) =>
        node is JsonValue v && v.TryGetValue( out int i ) && i >= 0 && i <= MaxIndex ? i : throw new FormatException( "An object index is out of range." );

    private static double Finite( JsonNode node ) =>
        node is JsonValue v && v.TryGetValue( out double d ) && double.IsFinite( d ) && Math.Abs( d ) <= 1e9 ? d : throw new FormatException( "A number in a frame is not finite." );

    private static JsonArray Numbers( JsonNode node, int count, string what )
    {
        if ( node is not JsonArray a || a.Count != count ) throw new FormatException( "The " + what + " does not hold " + count + " numbers." );
        var result = new JsonArray();
        foreach ( JsonNode n in a ) result.Add( Finite( n ) );
        return result;
    }

    private static string Cap( string text, int max ) => text.Length <= max ? text : text.Substring( 0, max );
}
