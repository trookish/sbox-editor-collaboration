using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// The live-drag channel: while somebody drags or rotates an object, the other editors receive its pose about thirty times a second in a
/// <c>transient</c> packet and draw the object from interpolated samples (<see cref="TransientReceiver"/>), instead of seeing it jump when the edit
/// is finally committed. A transient is a picture of motion in progress and nothing else: the host never journals it, never revises a scene for it
/// and never keeps it, and the committed edit still travels the ordinary path, so the scene a transient shows is always replaced by the real state.
/// </summary>
public static class TransientLimits
{
    public const string Kind = "transient";

    /// <summary>
    /// Advertised in the host's welcome. An older host answers a kind it does not know with a fatal error, so a client sends transients only to a host
    /// that names this feature; an older client simply never sends or reads one.
    /// </summary>
    public const string Feature = "transient-drag-1";

    /// <summary>Objects in one packet. A sender with more objects in motion sends the newest-moved first and the rest on the next tick.</summary>
    public const int MaxItems = 16;

    /// <summary>Largest payload of one packet in bytes of JSON: sixteen entries are about 2 KB, so this is a hostile-input bound, not a working limit.</summary>
    public const int MaxPacketBytes = 4096;

    /// <summary>One object's entry on the wire (id and ten numbers). A typical entry is about 110 bytes.</summary>
    public const int MaxEntryBytes = 160;

    /// <summary>Packets a member may send in any one second. A drag sends 30; the headroom absorbs a burst after a stall.</summary>
    public const int MaxPacketsPerSecond = 40;

    /// <summary>The largest magnitude any number may have. Scene coordinates beyond this are not a place anybody is dragging an object to.</summary>
    public const double MaxCoordinate = 1e7;

    /// <summary>A stream with no packet for this long has stopped: its last pose is held and the accepted edit is expected.</summary>
    public const double SettleMs = 500;

    /// <summary>
    /// A held pose that no accepted edit has confirmed for this long is given back (the object returns to the host's state): a drag whose edit was
    /// refused or lost must not leave an object where nobody put it.
    /// </summary>
    public const double HoldLimitMs = 5000;
}

/// <summary>One object's pose in a transient. The id is the object's GUID in the same text form the scene uses.</summary>
public readonly record struct TransientItem( string Id, Pose Pose );

/// <summary>Writes and checks the wire form of a transient: <c>Packet.Transient</c> is an array of <c>[id, px, py, pz, qx, qy, qz, qw, sx, sy, sz]</c> entries.</summary>
public static class TransientCodec
{
    private const int EntryLength = 11;

    /// <summary>
    /// Builds the packet. The first <see cref="TransientLimits.MaxItems"/> items are kept (the caller orders them newest-moved first) and further items
    /// are dropped if the payload would still exceed the byte bound: an oversized batch is truncated, never refused, because the objects left out go on
    /// the next tick. The stamp is rounded to a tenth of a millisecond, which is far finer than a frame.
    /// </summary>
    public static Packet Build( string scene, string peer, double sentMs, IReadOnlyList<TransientItem> items, bool final )
    {
        var array = new JsonArray();
        for ( int i = 0; i < items.Count && i < TransientLimits.MaxItems; i++ )
        {
            array.Add( Encode( items[i] ) );
        }
        while ( array.Count > 1 && array.ToJsonString().Length > TransientLimits.MaxPacketBytes )
        {
            array.RemoveAt( array.Count - 1 );
        }
        return new Packet
        {
            Kind = TransientLimits.Kind,
            // Not a packet identity anyone looks up (nothing answers a transient), so it is empty rather than thirty-six characters of GUID per packet.
            Id = string.Empty,
            Peer = peer,
            Scene = scene,
            SentMs = Math.Round( sentMs, 1 ),
            Final = final,
            Transient = array
        };
    }

    /// <summary>One entry. Position and scale to three decimals, rotation to four: finer than the 32-bit values the engine itself keeps.</summary>
    public static JsonArray Encode( TransientItem item )
    {
        var p = item.Pose;
        string id = Guid.TryParse( item.Id, out Guid guid ) ? guid.ToString( "N" ) : item.Id;
        return new JsonArray(
            JsonValue.Create( id ),
            Number( p.X, 3 ), Number( p.Y, 3 ), Number( p.Z, 3 ),
            Number( p.Qx, 4 ), Number( p.Qy, 4 ), Number( p.Qz, 4 ), Number( p.Qw, 4 ),
            Number( p.Sx, 3 ), Number( p.Sy, 3 ), Number( p.Sz, 3 ) );
    }

    /// <summary>The size of one object's entry on the wire.</summary>
    public static int EntryBytes( TransientItem item ) => Encode( item ).ToJsonString().Length;

    private static JsonNode Number( float value, int decimals )
    {
        double rounded = Math.Round( (double)value, decimals );
        // Negative zero would be written as "-0".
        return JsonValue.Create( rounded == 0 ? 0.0 : rounded );
    }

    /// <summary>
    /// Checks a transient and reads its items. Returns null when it is acceptable, otherwise the reason. Nothing here throws on hostile input: a wrong
    /// shape, a string where a number belongs, a non-finite or enormous number, an id that is not a GUID, too many entries or too many bytes is a
    /// refusal. The host applies exactly this check and nothing about the scene's content, because a transient never becomes scene state.
    /// </summary>
    public static string Validate( Packet packet, out List<TransientItem> items )
    {
        items = null;
        if ( packet == null || packet.Kind != TransientLimits.Kind )
        {
            return "Not a transient packet.";
        }
        if ( string.IsNullOrEmpty( packet.Scene ) || packet.Scene.Length > 260 )
        {
            return "A transient names no valid scene.";
        }
        if ( !double.IsFinite( packet.SentMs ) )
        {
            return "A transient carries no valid time stamp.";
        }
        JsonArray array = packet.Transient;
        if ( array == null || array.Count == 0 )
        {
            return "A transient carries no objects.";
        }
        if ( array.Count > TransientLimits.MaxItems )
        {
            return "A transient carries more than " + TransientLimits.MaxItems + " objects.";
        }
        string json;
        try
        {
            json = array.ToJsonString();
        }
        catch ( ArgumentException )
        {
            // A number that cannot be written as JSON (NaN, infinity) can only come from in-process code, never from the wire.
            return "A transient holds a value that is not finite.";
        }
        if ( json.Length > TransientLimits.MaxPacketBytes )
        {
            return "A transient is larger than " + TransientLimits.MaxPacketBytes + " bytes.";
        }
        var result = new List<TransientItem>( array.Count );
        foreach ( JsonNode node in array )
        {
            if ( node is not JsonArray entry || entry.Count != EntryLength )
            {
                return "A transient entry has the wrong shape.";
            }
            if ( entry[0] is not JsonValue idValue || !idValue.TryGetValue( out string text ) || !Guid.TryParse( text, out Guid guid ) )
            {
                return "A transient entry names no valid object.";
            }
            var numbers = new float[EntryLength - 1];
            for ( int i = 1; i < EntryLength; i++ )
            {
                if ( entry[i] is not JsonValue value || !value.TryGetValue( out double number ) )
                {
                    return "A transient entry holds a value that is not a number.";
                }
                if ( !double.IsFinite( number ) || Math.Abs( number ) > TransientLimits.MaxCoordinate )
                {
                    return "A transient entry holds a value that is not finite or is beyond " + TransientLimits.MaxCoordinate + ".";
                }
                numbers[i - 1] = (float)number;
            }
            result.Add( new TransientItem( guid.ToString(), new Pose( numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5], numbers[6], numbers[7], numbers[8], numbers[9] ) ) );
        }
        items = result;
        return null;
    }
}

/// <summary>
/// The host's flood guard for transients: at most <see cref="TransientLimits.MaxPacketsPerSecond"/> packets from one member in any sliding second. The
/// excess is dropped without a word, because a transient is motion in progress and the next one replaces it; nothing is queued.
/// </summary>
public sealed class TransientRateLimit
{
    private const double WindowMs = 1000;

    private readonly Dictionary<string, Queue<double>> recent = new Dictionary<string, Queue<double>>( StringComparer.Ordinal );

    /// <summary>True when a packet from <paramref name="member"/> arriving at <paramref name="nowMs"/> is within the limit (and counts it).</summary>
    public bool Allow( string member, double nowMs )
    {
        member ??= string.Empty;
        if ( !recent.TryGetValue( member, out Queue<double> times ) )
        {
            if ( recent.Count >= 64 )
            {
                Prune( nowMs );
            }
            times = new Queue<double>();
            recent[member] = times;
        }
        while ( times.Count > 0 && nowMs - times.Peek() >= WindowMs )
        {
            times.Dequeue();
        }
        if ( times.Count >= TransientLimits.MaxPacketsPerSecond )
        {
            return false;
        }
        times.Enqueue( nowMs );
        return true;
    }

    // Members come and go over a long session; a member that has been silent for a window holds no state worth keeping.
    private void Prune( double nowMs )
    {
        var gone = new List<string>();
        foreach ( var pair in recent )
        {
            if ( pair.Value.Count == 0 || nowMs - pair.Value.ToArray()[^1] >= WindowMs )
            {
                gone.Add( pair.Key );
            }
        }
        foreach ( string key in gone )
        {
            recent.Remove( key );
        }
    }
}

/// <summary>
/// Keeps the scan from reading a transient write as the person's own edit. A remote pose written into the live object makes the object differ
/// from <c>Known</c> (what the host last confirmed) in exactly its transform; the next scan would send that difference back as a local edit, and the
/// two editors would feed each other. The scan therefore reads such an object with its three transform fields taken from <c>Known</c> again: the
/// transform of an object that is being drawn from a remote drag is not this person's to send, and every other field still is.
/// </summary>
public static class TransientEcho
{
    private static readonly string[] Fields = { "Position", "Rotation", "Scale" };

    /// <summary>
    /// <paramref name="onto"/> with its Position, Rotation and Scale replaced by those of <paramref name="from"/>. Neither argument is modified.
    /// With nothing to copy from (an object the host does not know yet) <paramref name="onto"/> is returned as it is.
    /// </summary>
    public static ObjectState CopyTransform( ObjectState onto, ObjectState from )
    {
        if ( onto?.Data == null || from?.Data == null )
        {
            return onto;
        }
        var data = (JsonObject)onto.Data.DeepClone();
        foreach ( string field in Fields )
        {
            if ( from.Data.TryGetPropertyValue( field, out JsonNode value ) && value != null )
            {
                data[field] = value.DeepClone();
            }
        }
        return onto with { Data = data };
    }
}
