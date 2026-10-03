using System;

namespace TeamCreate;

/// <summary>
/// The caps that protect a relay the host runs on their own computer. Exposed through a tunnel, that relay is reachable by anyone who learns the random
/// address, and it runs on the host's machine and the host's connection, so each limit here bounds what a stranger can make it do before they have
/// proved they hold the invite: how many may join, how large one frame may be, how fast a room may talk, how long an unproven socket may linger,
/// and which URLs the listener answers. The functions are pure; the relay supplies the counts, sizes and clock readings.
/// </summary>
public static class RelayLimits
{
    /// <summary>
    /// People in one session through this relay, the host included: the same number as <see cref="SessionLimits.MaxParticipants"/>, so the relay
    /// never accepts a guest the host's server would turn away. SessionServer's own 16-socket bound on connections that have not yet
    /// said hello (SessionLimits.MaxConnections) still applies on top of this.
    /// </summary>
    public const int MaxMembersPerRoom = SessionLimits.MaxParticipants;

    /// <summary>
    /// The largest single frame the relay forwards. The library itself never sends more than <see cref="RelayProtocol.MaxMessageBytes"/> (about 16 KiB),
    /// so this leaves room for protocol growth while keeping one message from a stranger from costing the host megabytes of memory.
    /// </summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>Frames a room may send in any one second, all members together. A runaway or hostile peer is cut off here instead of saturating the host's uplink.</summary>
    public const int MaxMessagesPerSecond = 200;

    /// <summary>How long a new socket has to present a valid proof. Matches the hosted relay worker, which closes a socket that has said nothing in 10 s.</summary>
    public const double ProofDeadlineSeconds = 10;

    private const string HealthPath = "/health";

    private const string FilesPrefix = "/files/";

    // A file is addressed by the SHA-256 of its content, written as lower-case hexadecimal.
    private const int FileHashChars = 64;

    /// <summary>Null when one more member may join a room that has <paramref name="currentMembers"/>; otherwise the reason it may not.</summary>
    public static string AdmitMember( int currentMembers )
    {
        // A negative count means the caller's bookkeeping is wrong. Admitting on it would let the room grow past the cap by exactly that error.
        if ( currentMembers < 0 )
            return "The member count was not valid, so no further member was admitted.";
        if ( currentMembers >= MaxMembersPerRoom )
            return "The session already has the maximum of " + MaxMembersPerRoom + " members.";
        return null;
    }

    /// <summary>Null when a frame of this size may be forwarded. An empty or negative size is refused as malformed; anything over <see cref="MaxFrameBytes"/> is refused as too large.</summary>
    public static string AdmitFrame( int frameBytes )
    {
        if ( frameBytes <= 0 )
            return "The frame was empty or its size was not valid.";
        if ( frameBytes > MaxFrameBytes )
            return "The frame of " + frameBytes + " bytes is larger than the maximum of " + MaxFrameBytes + " bytes.";
        return null;
    }

    /// <summary>
    /// True when a socket that connected at <paramref name="connectedAtSeconds"/> has gone unproven for <see cref="ProofDeadlineSeconds"/> by
    /// <paramref name="nowSeconds"/> and should be closed. The deadline is reached, not exceeded: at exactly 10 s the socket is expired. A socket that has
    /// presented a valid proof never expires here. A clock reading that is not finite cannot show an unproven socket is still in time, so it expires.
    /// </summary>
    public static bool ProofExpired( double connectedAtSeconds, double nowSeconds, bool proven )
    {
        if ( proven )
            return false;
        if ( !double.IsFinite( connectedAtSeconds ) || !double.IsFinite( nowSeconds ) )
            return true;
        return nowSeconds - connectedAtSeconds >= ProofDeadlineSeconds;
    }

    /// <summary>
    /// The allow-list for the relay's HTTP listener: exactly <c>/relay</c>, <c>/health</c>, and <c>/files/</c> followed by 64 lower-case hexadecimal characters.
    /// Matching is exact and case-sensitive, with no query, fragment, trailing slash, dot segment, or encoding. Nothing in the URL is ever used as a file
    /// path (a file is looked up by its hash among the files the host chose to share), so every other string, including null, is refused.
    /// </summary>
    public static bool IsServedPath( string path )
    {
        if ( path == null )
            return false;
        if ( string.Equals( path, RelayProtocol.DefaultPath, StringComparison.Ordinal ) || string.Equals( path, HealthPath, StringComparison.Ordinal ) )
            return true;
        if ( path.Length != FilesPrefix.Length + FileHashChars || !path.StartsWith( FilesPrefix, StringComparison.Ordinal ) )
            return false;
        for ( int i = FilesPrefix.Length; i < path.Length; i++ )
        {
            // Ranges rather than Uri.IsHexDigit or char.IsDigit: those accept upper case, and char.IsDigit accepts digits from other scripts.
            char c = path[i];
            if ( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) )
                return false;
        }
        return true;
    }
}

/// <summary>
/// A rolling one-second rate limit: <c>limit</c> events are allowed in any 1.0-second span. It remembers the times of the last <c>limit</c> allowed
/// events in a ring that is allocated once, so a call never allocates. A new event is allowed when fewer than <c>limit</c> have happened in the
/// past second, which is exactly when the <c>limit</c>-th most recent allowed event is at least a second old (the span is half open: an event
/// exactly one second after another is outside its span). Unlike fixed one-second buckets, a burst straddling a bucket boundary cannot double the rate.
/// Not thread-safe: one owner calls it, as a relay does for each room.
/// </summary>
public sealed class RateWindow
{
    /// <summary>The largest limit accepted. The ring holds one reading per allowed event, so an absurd limit would be an absurd allocation.</summary>
    public const int MaxLimitPerSecond = 100_000;

    private readonly double[] stamps;

    private int head;

    private int count;

    private double latest = double.NegativeInfinity;

    public RateWindow( int limitPerSecond )
    {
        if ( limitPerSecond <= 0 || limitPerSecond > MaxLimitPerSecond )
            throw new ArgumentOutOfRangeException( nameof( limitPerSecond ), limitPerSecond, "The rate limit must be between 1 and " + MaxLimitPerSecond + " events per second." );
        stamps = new double[limitPerSecond];
    }

    /// <summary>
    /// Counts one event at <paramref name="nowSeconds"/> and returns true, or returns false (counting nothing) when the limit for the last second is already
    /// used. A reading earlier than one already seen is treated as the latest time seen, so a clock that steps back neither frees slots nor
    /// disorders the ring; a reading that is not finite is refused and changes nothing, so it can neither fill the window nor leave it open.
    /// </summary>
    public bool TryConsume( double nowSeconds )
    {
        if ( !double.IsFinite( nowSeconds ) )
            return false;
        if ( nowSeconds < latest )
            nowSeconds = latest;
        else
            latest = nowSeconds;

        if ( count == stamps.Length )
        {
            if ( nowSeconds - stamps[head] < 1.0 )
                return false;
            stamps[head] = nowSeconds;
            head = ( head + 1 ) % stamps.Length;
            return true;
        }
        stamps[( head + count ) % stamps.Length] = nowSeconds;
        count++;
        return true;
    }
}
