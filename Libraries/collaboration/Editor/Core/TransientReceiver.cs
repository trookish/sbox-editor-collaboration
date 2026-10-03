using System;
using System.Collections.Generic;

namespace TeamCreate;

public enum TransientVerdict
{
    /// <summary>The pose joined its object's stream and will be drawn.</summary>
    Applied,

    /// <summary>This editor is moving the same object itself; local wins during the drag.</summary>
    DroppedLocalDrag,

    /// <summary>It was sent before the object's accepted state arrived: drawing it would pull the object back from the authoritative pose (rubber-banding).</summary>
    DroppedStale,

    /// <summary>It is older than a packet already received from the same sender (a late or repeated packet).</summary>
    DroppedOld,

    /// <summary>Not a usable pose.</summary>
    Invalid
}

/// <summary>
/// Turns the transients received from other editors into poses to draw: one snapshot buffer per (scene, object) and one clock estimate and adaptive
/// delay per sending peer, the same three pieces that smooth presence (<see cref="ClockOffset"/>, <see cref="AdaptiveDelay"/>,
/// <see cref="SnapshotBuffer"/>). Each frame <see cref="Sample"/> yields the pose every active stream has at <c>now - delay</c>, which the editor writes
/// to the live object without an undo entry.
/// </summary>
/// <remarks>
/// All times are milliseconds: <c>senderMs</c> is the sender's own monotonic clock, <c>nowMs</c> a monotonic clock of this editor. Nothing here
/// reads a clock or the engine.
///
/// How a stream ends. The sender's last act is the real edit (which reaches everybody as an accepted packet) and one transient marked final.
/// <list type="bullet">
/// <item>A final transient makes the stream hold its exact last pose, with no extrapolation past it.</item>
/// <item>The accepted edit that carries <c>Final</c> ends the stream (<see cref="OnAccepted"/>): the authoritative state replaces whatever was drawn.</item>
/// <item>A stream silent for <see cref="TransientLimits.SettleMs"/> stops being written and its last pose is held; if no accepted edit confirms it
/// within <see cref="TransientLimits.HoldLimitMs"/> it is given back (<see cref="TakeExpired"/>).</item>
/// <item>A transient that arrives after the accepted edit but was sent before it (a late packet) is dropped, so the object never swings back.</item>
/// </list>
/// The stale test compares the transient's send time, converted to this editor's clock, with the local time the accepted edit arrived. The edit is sent
/// after the transients of the same drag and takes at least the minimum network delay to arrive, which is also what the clock estimate adds, so a
/// transient of the finished drag is never later than its accepted edit, and a new drag started afterwards always is.
/// </remarks>
public sealed class TransientReceiver
{
    /// <summary>A sender stamp this far behind the newest means the sender's clock began again (a restarted editor).</summary>
    public const double RestartThresholdMs = 5000;

    /// <summary>A sample closer than this to the newest stored one in the same stream replaces it instead of being stored beside it.</summary>
    public const double MinSampleSpacingMs = 5;

    /// <summary>An arrival gap longer than this is a pause between drags, not network irregularity, and is not shown to the delay.</summary>
    public const double MaxNetworkGapMs = 1000;

    /// <summary>Samples a stream holds before it is drawn: one is a point, two are a segment.</summary>
    public const int MinSamples = 2;

    private const double SettledMemoryMs = 10_000;

    private sealed class PeerState
    {
        public readonly ClockOffset Clock = new ClockOffset();
        public readonly AdaptiveDelay Delay = new AdaptiveDelay();
        public bool HasNewest;
        public double NewestSenderMs;
        public bool HasArrival;
        public double LastArrivalMs;
    }

    private sealed class StreamState
    {
        public string Peer;
        public readonly SnapshotBuffer Buffer = new SnapshotBuffer();
        public double LastArrivalMs;
        public double NewestStoredLocalMs;
        public bool Final;
        public bool HasWritten;
        public Pose LastWritten;
    }

    private readonly Dictionary<string, PeerState> peers = new Dictionary<string, PeerState>( StringComparer.Ordinal );

    private readonly Dictionary<(string Scene, string Id), StreamState> streams = new Dictionary<(string Scene, string Id), StreamState>();

    private readonly Dictionary<(string Scene, string Id), double> settled = new Dictionary<(string Scene, string Id), double>();

    /// <summary>Number of objects currently being drawn from (or held at) a remote drag.</summary>
    public int ActiveCount => streams.Count;

    /// <summary>
    /// Takes a received packet's items. The sender's clock and the arrival gap are observed once for the packet, whatever its item count. Returns how
    /// many items joined a stream.
    /// </summary>
    public int OnPacket( string peer, string scene, double senderMs, IReadOnlyList<TransientItem> items, bool final, double nowMs, Func<string, bool> localDragging )
    {
        int applied = 0;
        if ( !ObservePeer( peer, senderMs, nowMs, out PeerState state, out TransientVerdict early ) )
        {
            return 0;
        }
        foreach ( TransientItem item in items )
        {
            if ( Take( peer, state, scene, senderMs, item, final, nowMs, localDragging != null && localDragging( item.Id ) ) == TransientVerdict.Applied )
            {
                applied++;
            }
        }
        return applied;
    }

    /// <summary>One item as a packet of its own. The same rules as <see cref="OnPacket"/>; returns what became of it.</summary>
    public TransientVerdict OnTransient( string peer, string scene, double senderMs, TransientItem item, bool final, double nowMs, bool localDragging )
    {
        if ( !ObservePeer( peer, senderMs, nowMs, out PeerState state, out TransientVerdict early ) )
        {
            return early;
        }
        return Take( peer, state, scene, senderMs, item, final, nowMs, localDragging );
    }

    private bool ObservePeer( string peer, double senderMs, double nowMs, out PeerState state, out TransientVerdict verdict )
    {
        state = null;
        verdict = TransientVerdict.Invalid;
        if ( string.IsNullOrEmpty( peer ) || !double.IsFinite( senderMs ) || !double.IsFinite( nowMs ) )
        {
            return false;
        }
        if ( !peers.TryGetValue( peer, out state ) )
        {
            state = new PeerState();
            peers[peer] = state;
        }
        if ( state.HasNewest && state.NewestSenderMs - senderMs > RestartThresholdMs )
        {
            // The sender's editor restarted and its clock began again: ClockOffset cannot forget, so the peer's estimate is replaced.
            state = new PeerState();
            peers[peer] = state;
        }
        else if ( state.HasNewest && senderMs < state.NewestSenderMs )
        {
            verdict = TransientVerdict.DroppedOld;
            return false;
        }
        state.HasNewest = true;
        state.NewestSenderMs = senderMs;
        state.Clock.Observe( senderMs, nowMs );
        if ( state.HasArrival )
        {
            double gap = nowMs - state.LastArrivalMs;
            if ( gap >= 0 && gap <= MaxNetworkGapMs )
            {
                state.Delay.Observe( gap, nowMs );
            }
        }
        state.HasArrival = true;
        state.LastArrivalMs = nowMs;
        return true;
    }

    private TransientVerdict Take( string peer, PeerState state, string scene, double senderMs, TransientItem item, bool final, double nowMs, bool localDragging )
    {
        if ( string.IsNullOrEmpty( scene ) || string.IsNullOrEmpty( item.Id ) || !IsFinite( item.Pose ) )
        {
            return TransientVerdict.Invalid;
        }
        if ( localDragging )
        {
            return TransientVerdict.DroppedLocalDrag;
        }
        var key = (scene, item.Id);
        double localMs = state.Clock.ToLocalMs( senderMs );
        if ( settled.TryGetValue( key, out double acceptedAtMs ) )
        {
            if ( localMs < acceptedAtMs )
            {
                return TransientVerdict.DroppedStale;
            }
            // Sent after the accepted state: a new drag of the object.
            settled.Remove( key );
        }
        if ( !streams.TryGetValue( key, out StreamState stream ) || stream.Peer != peer )
        {
            stream = new StreamState { Peer = peer };
            streams[key] = stream;
        }
        if ( stream.Buffer.Count > 0 && localMs - stream.NewestStoredLocalMs < MinSampleSpacingMs )
        {
            // Stored at the time of the sample it replaces, so no two samples are ever within 5 ms of each other (extrapolation would then see a huge velocity).
            localMs = stream.NewestStoredLocalMs;
        }
        stream.Buffer.Add( localMs, item.Pose );
        stream.NewestStoredLocalMs = localMs;
        stream.LastArrivalMs = nowMs;
        if ( final )
        {
            // A second identical sample just after the first gives the extrapolation a velocity of zero, so the object stops exactly on its final pose
            // instead of running on for up to 120 ms and then snapping back.
            stream.Buffer.Add( localMs + 1, item.Pose );
            stream.NewestStoredLocalMs = localMs + 1;
            stream.Final = true;
        }
        else
        {
            stream.Final = false;
        }
        return TransientVerdict.Applied;
    }

    /// <summary>
    /// The accepted state of an object arrived. When it is the final one of an edit (<paramref name="final"/>) the stream ends and the moment is
    /// remembered, so that a transient of the finished drag arriving later is dropped; a mid-drag accepted edit leaves the stream running.
    /// </summary>
    public void OnAccepted( string scene, string id, bool final, double nowMs )
    {
        if ( !final || string.IsNullOrEmpty( scene ) || string.IsNullOrEmpty( id ) || !double.IsFinite( nowMs ) )
        {
            return;
        }
        var key = (scene, id);
        streams.Remove( key );
        settled[key] = nowMs;
        if ( settled.Count > 32 )
        {
            foreach ( var old in new List<(string Scene, string Id)>( settled.Keys ) )
            {
                if ( nowMs - settled[old] > SettledMemoryMs )
                {
                    settled.Remove( old );
                }
            }
        }
    }

    /// <summary>
    /// True while the object is drawn from, or held at, a remote drag. The editor then keeps the object's transform out of the scan's diff and keeps
    /// the pose it shows when a mid-drag accepted edit is applied.
    /// </summary>
    public bool IsActive( string scene, string id, double nowMs ) => streams.ContainsKey( (scene, id) );

    /// <summary>Appends the pose every active stream has at <c>nowMs - delay</c>, for those whose pose changed since the last call.</summary>
    public void Sample( double nowMs, List<(string Scene, string Id, Pose Pose)> output )
    {
        if ( !double.IsFinite( nowMs ) )
        {
            return;
        }
        foreach ( var pair in streams )
        {
            StreamState stream = pair.Value;
            if ( !stream.Final && nowMs - stream.LastArrivalMs > TransientLimits.SettleMs )
            {
                // The sender went quiet: the last pose stays where it is until the accepted edit arrives.
                continue;
            }
            if ( stream.Buffer.Count < MinSamples || !peers.TryGetValue( stream.Peer, out PeerState peer ) )
            {
                continue;
            }
            if ( !stream.Buffer.TrySample( nowMs - peer.Delay.DelayMs( nowMs ), out Pose pose ) )
            {
                continue;
            }
            if ( stream.HasWritten && stream.LastWritten == pose )
            {
                continue;
            }
            stream.HasWritten = true;
            stream.LastWritten = pose;
            output.Add( (pair.Key.Scene, pair.Key.Id, pose) );
        }
    }

    /// <summary>Streams that no accepted edit confirmed within <see cref="TransientLimits.HoldLimitMs"/> of their last packet. They are removed; the editor gives those objects back to the host's state.</summary>
    public List<(string Scene, string Id)> TakeExpired( double nowMs )
    {
        var expired = new List<(string Scene, string Id)>();
        foreach ( var pair in streams )
        {
            if ( nowMs - pair.Value.LastArrivalMs > TransientLimits.HoldLimitMs )
            {
                expired.Add( pair.Key );
            }
        }
        foreach ( var key in expired )
        {
            streams.Remove( key );
        }
        return expired;
    }

    /// <summary>Forgets everything: a new session, a reconnect, a scene resynchronised from the host.</summary>
    public void Clear()
    {
        peers.Clear();
        streams.Clear();
        settled.Clear();
    }

    /// <summary>How far behind live this sender's objects are drawn at <paramref name="nowMs"/>: 50 to 150 ms, 80 before anything was received.</summary>
    public double DelayMs( string peer, double nowMs )
    {
        return peer != null && peers.TryGetValue( peer, out PeerState state ) ? state.Delay.DelayMs( nowMs ) : AdaptiveDelay.StartMs;
    }

    private static bool IsFinite( Pose p )
    {
        return float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z )
            && float.IsFinite( p.Qx ) && float.IsFinite( p.Qy ) && float.IsFinite( p.Qz ) && float.IsFinite( p.Qw )
            && float.IsFinite( p.Sx ) && float.IsFinite( p.Sy ) && float.IsFinite( p.Sz );
    }
}
