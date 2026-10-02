using TeamCreate;

// PeerSmoothing: one remote collaborator's camera, turned from 30 raw packets a second into a path that is smooth at 60 frames a second. The
// maths (SnapshotBuffer, ClockOffset, AdaptiveDelay) has its own tests; these are about the composition and the hazards that appear only once the
// three are wired together: a gate before the first frame is drawn, a restarted sender, samples that arrive almost together, and bad numbers.
static class PeerSmoothingTests
{
    // The sender's clock is unrelated to ours: its zero is whenever its editor started. 12345 ms is an arbitrary, awkward difference.
    private const double SenderToLocalMs = 12345.0;

    private static Pose At( double x ) => new Pose( (float)x, 0f, 0f, 0f, 0f, 0f, 1f );

    private static Pose Moving( double senderMs )
    {
        // A camera flown along a gentle curve: about 100 units a second forward with a sideways sway. Smooth, so any roughness in the drawn
        // path is the smoother's doing and not the input's.
        double y = 20.0 * Math.Sin( senderMs / 700.0 );
        double yaw = 0.4 * Math.Sin( senderMs / 900.0 );
        return new Pose( (float)(senderMs * 0.1), (float)y, 0f, 0f, 0f, (float)Math.Sin( yaw / 2 ), (float)Math.Cos( yaw / 2 ) );
    }

    private static double Distance( Pose a, Pose b )
    {
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z;
        return Math.Sqrt( dx * dx + dy * dy + dz * dz );
    }

    /// <summary>
    /// Runs a sender stamping every <paramref name="sendIntervalMs"/> against a receiver drawing at 60 Hz, with each packet taking
    /// <c>baseLatencyMs + jitter</c> to arrive. Returns every drawn frame's step length after <paramref name="warmupMs"/> and the delay
    /// readings, so that the caller can judge both smoothness and the delay bounds.
    /// </summary>
    private static (List<double> Steps, List<double> Delays, int Unready) Simulate( PeerSmoothing peer, double durationMs, double warmupMs, double jitterMs, int seed, double sendIntervalMs = 33.0, double senderStartMs = 0 )
    {
        var random = new Random( seed );
        var arrivals = new List<(double Local, double Sender)>();
        for ( double sent = senderStartMs; sent < senderStartMs + durationMs; sent += sendIntervalMs )
        {
            double latency = 20.0 + (jitterMs > 0 ? (random.NextDouble() * 2 - 1) * jitterMs : 0);
            arrivals.Add( (sent + SenderToLocalMs + latency, sent) );
        }
        arrivals.Sort( ( a, b ) => a.Local.CompareTo( b.Local ) );

        var steps = new List<double>();
        var delays = new List<double>();
        int unready = 0;
        int next = 0;
        Pose? previous = null;
        double frame = 1000.0 / 60.0;
        for ( double now = senderStartMs + SenderToLocalMs; now < senderStartMs + SenderToLocalMs + durationMs; now += frame )
        {
            while ( next < arrivals.Count && arrivals[next].Local <= now )
            {
                peer.OnPresence( arrivals[next].Sender, arrivals[next].Local, Moving( arrivals[next].Sender ) );
                next++;
            }
            delays.Add( peer.CurrentDelayMs( now ) );
            if ( !peer.TryGetPose( now, out var pose ) )
            {
                unready++;
                previous = null;
                continue;
            }
            if ( previous.HasValue && now - senderStartMs - SenderToLocalMs >= warmupMs )
            {
                steps.Add( Distance( previous.Value, pose ) );
            }
            previous = pose;
        }
        return (steps, delays, unready);
    }

    private static double Median( List<double> values )
    {
        var sorted = values.OrderBy( v => v ).ToList();
        return sorted[sorted.Count / 2];
    }

    public static void Run( Action<bool, string> assert )
    {
        // ---- Readiness: nothing is drawn from a half-filled buffer --------------------------------------------------------------------
        // Before a clock estimate and three samples the first ~300 ms stutter (one sample is held still, two blend with nothing around them),
        // so the marker must not use the smoothed path until it is ready.
        var peer = new PeerSmoothing();
        assert( !peer.TryGetPose( 1000, out var none ) && none == default, "peer_not_ready_when_nothing_received" );
        assert( peer.CurrentDelayMs( 1000 ) == AdaptiveDelay.StartMs, "peer_delay_before_any_packet_is_the_start_delay" );
        peer.OnPresence( 100, 100 + SenderToLocalMs, At( 1 ) );
        assert( !peer.TryGetPose( 200 + SenderToLocalMs, out _ ), "peer_not_ready_after_one_sample" );
        peer.OnPresence( 133, 133 + SenderToLocalMs, At( 2 ) );
        assert( !peer.TryGetPose( 200 + SenderToLocalMs, out _ ), "peer_not_ready_after_two_samples" );
        peer.OnPresence( 166, 166 + SenderToLocalMs, At( 3 ) );
        assert( peer.TryGetPose( 200 + SenderToLocalMs, out var third ) && float.IsFinite( third.X ), "peer_ready_after_three_samples" );

        // ---- Smoothness: the drawn path has no step much bigger than a typical one ------------------------------------------------------
        foreach ( (string label, double jitter) in new[] { ("steady", 0.0), ("jittery", 15.0) } )
        {
            var smooth = new PeerSmoothing();
            var run = Simulate( smooth, durationMs: 8000, warmupMs: 1500, jitterMs: jitter, seed: 7 );
            double median = Median( run.Steps );
            double largest = run.Steps.Max();
            assert( run.Steps.Count > 300, $"peer_{label}_stream_draws_most_frames" );
            assert( median > 0.5, $"peer_{label}_stream_actually_moves" );
            assert( largest <= 2 * median, $"peer_{label}_stream_largest_step_is_at_most_twice_the_median: largest {largest:F2} median {median:F2}" );
            assert( run.Unready < 30, $"peer_{label}_stream_is_ready_within_half_a_second: {run.Unready} unready frames" );
        }

        // ---- The delay stays in its band --------------------------------------------------------------------------------------------------
        var banded = new PeerSmoothing();
        var bandRun = Simulate( banded, durationMs: 8000, warmupMs: 0, jitterMs: 15, seed: 11 );
        assert( bandRun.Delays.All( d => d >= AdaptiveDelay.FloorMs && d <= AdaptiveDelay.CeilingMs ), "peer_delay_stays_within_50_to_150_ms_when_jittery" );
        var sparse = new PeerSmoothing();
        var sparseRun = Simulate( sparse, durationMs: 8000, warmupMs: 0, jitterMs: 0, seed: 3, sendIntervalMs: 900 );
        assert( sparseRun.Delays.All( d => d >= AdaptiveDelay.FloorMs && d <= AdaptiveDelay.CeilingMs ), "peer_delay_stays_within_50_to_150_ms_when_packets_are_rare" );

        // ---- A restarted sender: its clock begins again, so its stamps go backwards -------------------------------------------------------
        // ClockOffset has no Reset. Without a fresh start the old offset (the minimum, i.e. the smallest difference seen) is kept, the new stamps
        // convert to a time far in the future, and the marker would stay where the previous run left it.
        var restarted = new PeerSmoothing();
        double local = 200000;
        for ( int i = 0; i < 20; i++ )
        {
            restarted.OnPresence( 100000 + i * 33.0, local + i * 33.0, At( 5000 + i ) );
        }
        assert( restarted.TryGetPose( local + 20 * 33.0, out _ ), "peer_restart_precondition_old_stream_was_ready" );
        local += 20 * 33.0;
        restarted.OnPresence( 50, local, At( 1 ) );
        assert( !restarted.TryGetPose( local + 1, out _ ), "peer_restart_drops_the_old_samples_and_waits_for_three_new_ones" );
        restarted.OnPresence( 83, local + 33, At( 2 ) );
        restarted.OnPresence( 116, local + 66, At( 3 ) );
        restarted.OnPresence( 149, local + 99, At( 4 ) );
        restarted.OnPresence( 182, local + 132, At( 5 ) );
        assert( restarted.TryGetPose( local + 160, out var afterRestart ) && afterRestart.X < 100 && afterRestart.X >= 1, $"peer_restart_follows_the_new_stream: X {afterRestart.X}" );

        // A modest step back (a late packet, a clock that stepped a little) is not a restart: 4 s is inside the 5 s limit, 6 s is outside it.
        var nudged = new PeerSmoothing();
        for ( int i = 0; i < 6; i++ )
        {
            nudged.OnPresence( 100000 + i * 33.0, 300000 + i * 33.0, At( i ) );
        }
        nudged.OnPresence( 100000 + 5 * 33.0 - 4000 + 5, 300000 + 6 * 33.0, At( 999 ) );
        assert( nudged.TryGetPose( 300000 + 6 * 33.0 + 10, out var kept ) && kept.X < 100, "peer_stamp_four_seconds_back_is_not_a_restart_and_is_ignored_as_late" );
        var restartedEarly = new PeerSmoothing();
        for ( int i = 0; i < 6; i++ )
        {
            restartedEarly.OnPresence( 100000 + i * 33.0, 300000 + i * 33.0, At( i ) );
        }
        restartedEarly.OnPresence( 100000 + 5 * 33.0 - 6000, 300000 + 6 * 33.0, At( 999 ) );
        assert( !restartedEarly.TryGetPose( 300000 + 6 * 33.0 + 10, out _ ), "peer_stamp_six_seconds_back_is_a_restart" );

        // ---- Samples closer than 5 ms collapse: the newer pose replaces the older, at the older one's time -------------------------------
        // Two samples 2 ms apart give an extrapolation velocity 16 times too large (or, here, zero). The stream moves one unit per millisecond;
        // a straggler 2 ms after the sample at 99 says the camera had not moved. Kept as its own sample the velocity of the last two would be
        // zero and the marker would stop; collapsed, the last two are 66 and 99 and the marker keeps going at one unit per millisecond.
        var collapsed = new PeerSmoothing();
        collapsed.OnPresence( 0, SenderToLocalMs, At( 0 ) );
        collapsed.OnPresence( 33, 33 + SenderToLocalMs, At( 33 ) );
        collapsed.OnPresence( 66, 66 + SenderToLocalMs, At( 66 ) );
        collapsed.OnPresence( 99, 99 + SenderToLocalMs, At( 99 ) );
        collapsed.OnPresence( 101, 101 + SenderToLocalMs, At( 99 ) );
        double renderAt = 140.0 + SenderToLocalMs;
        double guess = renderAt + collapsed.CurrentDelayMs( renderAt );
        guess = renderAt + collapsed.CurrentDelayMs( guess );
        assert( collapsed.TryGetPose( guess, out var extrapolated ) && Math.Abs( extrapolated.X - 140.0 ) < 3.0, $"peer_samples_closer_than_5_ms_are_collapsed: X {extrapolated.X} expected about 140" );

        // ---- A late, older sample never rewinds the path ---------------------------------------------------------------------------------
        var late = new PeerSmoothing();
        for ( int i = 0; i < 5; i++ )
        {
            late.OnPresence( i * 33.0, i * 33.0 + SenderToLocalMs, At( i * 33.0 ) );
        }
        late.OnPresence( 40, 5 * 33.0 + SenderToLocalMs, At( 9999 ) );
        // Read just before the newest sample, where an inserted or substituted pose would show.
        double lateRender = 125.0 + SenderToLocalMs;
        double lateNow = lateRender + late.CurrentDelayMs( lateRender );
        lateNow = lateRender + late.CurrentDelayMs( lateNow );
        assert( late.TryGetPose( lateNow, out var lateBlend ) && lateBlend.X > 90 && lateBlend.X < 135, $"peer_older_sample_arriving_late_is_ignored: X {lateBlend.X}" );

        // ---- Non-finite or hostile numbers never reach the drawn pose -----------------------------------------------------------------------
        var hostile = new PeerSmoothing();
        hostile.OnPresence( double.NaN, 1000, At( 1 ) );
        hostile.OnPresence( 10, double.NaN, At( 1 ) );
        hostile.OnPresence( double.PositiveInfinity, 1000, At( 1 ) );
        hostile.OnPresence( 10, 1000, new Pose( float.NaN, 0, 0, 0, 0, 0, 1 ) );
        hostile.OnPresence( 10, 1000, new Pose( 0, float.PositiveInfinity, 0, 0, 0, 0, 1 ) );
        hostile.OnPresence( 10, 1000, new Pose( 0, 0, 0, 0, 0, float.NaN, 1 ) );
        assert( !hostile.TryGetPose( 2000, out _ ), "peer_non_finite_input_never_counts_toward_readiness" );
        hostile.OnPresence( 100, 1100, At( 1 ) );
        hostile.OnPresence( 133, 1133, new Pose( float.NaN, 0, 0, 0, 0, 0, 1 ) );
        hostile.OnPresence( 166, 1166, At( 2 ) );
        hostile.OnPresence( 199, 1199, At( 3 ) );
        assert( hostile.TryGetPose( 1300, out var clean ) && float.IsFinite( clean.X ) && float.IsFinite( clean.Qw ), "peer_good_samples_still_work_between_bad_ones" );

        // A rejected sample must leave no trace, including its sender stamp: if a bad pose stamped far in the future were remembered as the newest,
        // every good packet after it would look like a restart (or a late packet) and the peer would never be drawn smoothly again.
        var poisoned = new PeerSmoothing();
        for ( int i = 0; i < 4; i++ )
        {
            poisoned.OnPresence( 1000 + i * 33.0, 1000 + i * 33.0 + SenderToLocalMs, At( i ) );
        }
        poisoned.OnPresence( 9e9, 1132 + SenderToLocalMs, new Pose( float.NaN, 0, 0, 0, 0, 0, 1 ) );
        poisoned.OnPresence( 1132, 1132 + SenderToLocalMs, At( 4 ) );
        poisoned.OnPresence( 1165, 1165 + SenderToLocalMs, At( 5 ) );
        assert( poisoned.TryGetPose( 1200 + SenderToLocalMs, out var afterPoison ) && afterPoison.X > 2 && afterPoison.X < 6, $"peer_a_rejected_sample_leaves_no_trace_in_the_clock_or_ordering: X {afterPoison.X}" );

        // Finite but enormous values: the blend and the extrapolation must not overflow into infinity on the way out.
        var huge = new PeerSmoothing();
        double hugeStart = 5000;
        for ( int i = 0; i < 40; i++ )
        {
            float x = i % 2 == 0 ? float.MaxValue : -float.MaxValue;
            huge.OnPresence( hugeStart + i * 33.0, hugeStart + i * 33.0 + SenderToLocalMs, new Pose( x, x, x, 0, 0, 0, 1, float.MaxValue, float.MaxValue, float.MaxValue ) );
        }
        bool allFinite = true;
        for ( double t = hugeStart + SenderToLocalMs; t < hugeStart + SenderToLocalMs + 2500; t += 7 )
        {
            if ( huge.TryGetPose( t, out var h ) )
            {
                allFinite &= float.IsFinite( h.X ) && float.IsFinite( h.Y ) && float.IsFinite( h.Z ) && float.IsFinite( h.Sx ) && float.IsFinite( h.Qw );
            }
        }
        assert( allFinite, "peer_enormous_finite_poses_never_render_as_infinity" );

        // ---- A presence without a usable sender stamp (an older peer) is not smoothed -------------------------------------------------------
        assert( !PeerSmoothing.CanSmooth( 0 ) && !PeerSmoothing.CanSmooth( -1 ) && !PeerSmoothing.CanSmooth( double.NaN ) && !PeerSmoothing.CanSmooth( double.PositiveInfinity ), "peer_unstamped_or_bad_stamp_cannot_be_smoothed" );
        assert( PeerSmoothing.CanSmooth( 0.001 ) && PeerSmoothing.CanSmooth( 123456.7 ), "peer_a_positive_finite_stamp_can_be_smoothed" );

        // ---- A pause the sender chose is not network irregularity ------------------------------------------------------------------------
        // An idle camera is refreshed every two seconds on purpose. That gap says nothing about the link and must not push the delay up: with
        // one such gap in the window the mean alone would hold the delay near 80 ms instead of letting it settle toward the floor.
        var withPause = new PeerSmoothing();
        var withoutPause = new PeerSmoothing();
        // The pause comes late, so that it is still inside the 32-gap window when the delay is read.
        for ( int i = 0; i < 160; i++ )
        {
            double sentA = i * 33.0 + (i >= 150 ? 2000.0 : 0.0);
            withPause.OnPresence( sentA, sentA + SenderToLocalMs, At( i ) );
            withoutPause.OnPresence( i * 33.0, i * 33.0 + SenderToLocalMs, At( i ) );
        }
        double endPause = 159 * 33.0 + 2000.0 + SenderToLocalMs;
        double endSteady = 159 * 33.0 + SenderToLocalMs;
        assert( withPause.CurrentDelayMs( endPause ) <= withoutPause.CurrentDelayMs( endSteady ) + 6.0, $"peer_a_two_second_refresh_gap_does_not_raise_the_delay: {withPause.CurrentDelayMs( endPause ):F1} vs {withoutPause.CurrentDelayMs( endSteady ):F1}" );
        assert( withoutPause.CurrentDelayMs( endSteady ) < AdaptiveDelay.StartMs, "peer_a_steady_stream_settles_below_the_start_delay" );
    }
}
