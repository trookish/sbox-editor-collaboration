using System;

namespace TeamCreate;

/// <summary>
/// One remote collaborator's camera, smoothed: owns the three pieces of maths (<see cref="ClockOffset"/>, <see cref="SnapshotBuffer"/>,
/// <see cref="AdaptiveDelay"/>) and the rules for feeding them that only matter once they are used together. The editor keeps one per peer, feeds it
/// every presence it receives and asks it each frame where to draw the marker.
/// </summary>
/// <remarks>
/// All times are milliseconds. <c>senderMs</c> is the sender's own monotonic clock; <c>localNowMs</c> is a monotonic clock of this editor. Neither is
/// wall-clock time and the two have no known relation, which is what <see cref="ClockOffset"/> estimates. Samples are stored at the LOCAL time they
/// correspond to (sender stamp plus the offset at that moment) and the picture is drawn at <c>localNow - delay</c>.
///
/// The hazards the three parts leave to their caller, and what is done about each:
/// <list type="bullet">
/// <item>The first samples stutter: one sample is held still and two blend with nothing around them. <see cref="TryGetPose"/> is false until the
/// clock has an estimate and <see cref="MinSamples"/> samples are held; the caller draws the raw packet meanwhile.</item>
/// <item><see cref="ClockOffset"/> cannot forget. A sender whose stamps go back by more than <see cref="RestartThresholdMs"/> has restarted its editor
/// (its clock began again), so all three objects are replaced; keeping the old minimum would put every new sample far in the future.</item>
/// <item>Two samples a few milliseconds apart give the extrapolation an enormous velocity. A sample less than <see cref="MinSampleSpacingMs"/> after the
/// newest stored one replaces that sample's pose (the newer pose wins, the older time is kept) instead of being added beside it.</item>
/// <item>A stamp older than the newest one seen (within the restart limit) is a late or repeated packet: it is ignored, never inserted behind newer
/// motion.</item>
/// <item>NaN or infinite stamps, times or pose components change nothing at all: not the clock, not the delay, not the readiness count.</item>
/// <item>An idle camera is refreshed every couple of seconds by design. Such a gap says nothing about the network, so gaps over
/// <see cref="MaxNetworkGapMs"/> are not shown to <see cref="AdaptiveDelay"/>; otherwise one refresh in the window would hold the delay near 80 ms.</item>
/// </list>
/// </remarks>
public sealed class PeerSmoothing
{
    /// <summary>Samples held before the smoothed path is used. Three give an interval to blend in and one before it to extrapolate from.</summary>
    public const int MinSamples = 3;

    /// <summary>A sender stamp this far behind the newest means the sender's clock began again (a restarted editor).</summary>
    public const double RestartThresholdMs = 5000;

    /// <summary>A sample closer than this to the newest stored one is merged into it rather than stored beside it.</summary>
    public const double MinSampleSpacingMs = 5;

    /// <summary>An arrival gap longer than this is the sender pausing (an idle refresh), not the network being slow, and is not used for the delay.</summary>
    public const double MaxNetworkGapMs = 1000;

    private ClockOffset clock = new ClockOffset();
    private SnapshotBuffer buffer = new SnapshotBuffer();
    private AdaptiveDelay delay = new AdaptiveDelay();
    private bool hasNewestSender;
    private double newestSenderMs;
    private bool hasArrival;
    private double lastArrivalMs;
    private double newestStoredLocalMs;

    /// <summary>
    /// True when a presence carries a sender stamp this class can use: finite and positive. A stamp of zero is how an older peer (one that predates
    /// the field) looks on the wire, and such a peer is drawn from its raw packets.
    /// </summary>
    public static bool CanSmooth( double senderMs ) => double.IsFinite( senderMs ) && senderMs > 0;

    /// <summary>Feeds one received presence. Call it for every presence of this peer, in the order received.</summary>
    public void OnPresence( double senderMs, double localNowMs, Pose pose )
    {
        if ( !double.IsFinite( senderMs ) || !double.IsFinite( localNowMs ) || !IsFinite( pose ) )
        {
            return;
        }

        if ( hasNewestSender && newestSenderMs - senderMs > RestartThresholdMs )
        {
            Restart();
        }
        else if ( hasNewestSender && senderMs < newestSenderMs )
        {
            return;
        }
        hasNewestSender = true;
        newestSenderMs = senderMs;

        clock.Observe( senderMs, localNowMs );
        if ( hasArrival )
        {
            double gap = localNowMs - lastArrivalMs;
            if ( gap >= 0 && gap <= MaxNetworkGapMs )
            {
                delay.Observe( gap, localNowMs );
            }
        }
        hasArrival = true;
        lastArrivalMs = localNowMs;

        double localMs = clock.ToLocalMs( senderMs );
        if ( buffer.Count > 0 && localMs - newestStoredLocalMs < MinSampleSpacingMs )
        {
            // Stored at the time of the sample it replaces: the buffer swaps the pose of an equal timestamp, so no sample is ever added within
            // 5 ms of another. This also covers the first packets after the clock estimate improved, when the converted time can land a little
            // behind the previous one.
            localMs = newestStoredLocalMs;
        }
        buffer.Add( localMs, pose );
        newestStoredLocalMs = localMs;
    }

    /// <summary>
    /// The pose to draw at local time <paramref name="localNowMs"/>. False until the peer is ready (a clock estimate and
    /// <see cref="MinSamples"/> samples); <paramref name="pose"/> is then the default value and the caller draws the raw packet.
    /// </summary>
    public bool TryGetPose( double localNowMs, out Pose pose )
    {
        pose = default;
        if ( !clock.HasEstimate || buffer.Count < MinSamples )
        {
            return false;
        }
        return buffer.TrySample( localNowMs - delay.DelayMs( localNowMs ), out pose );
    }

    /// <summary>How far behind live this peer is being drawn at <paramref name="localNowMs"/>, between 50 and 150 ms (80 before any packet).</summary>
    public double CurrentDelayMs( double localNowMs ) => delay.DelayMs( localNowMs );

    private void Restart()
    {
        clock = new ClockOffset();
        buffer = new SnapshotBuffer();
        delay = new AdaptiveDelay();
        hasNewestSender = false;
        hasArrival = false;
        newestStoredLocalMs = 0;
    }

    private static bool IsFinite( Pose p )
    {
        return float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z )
            && float.IsFinite( p.Qx ) && float.IsFinite( p.Qy ) && float.IsFinite( p.Qz ) && float.IsFinite( p.Qw )
            && float.IsFinite( p.Sx ) && float.IsFinite( p.Sy ) && float.IsFinite( p.Sz );
    }
}
