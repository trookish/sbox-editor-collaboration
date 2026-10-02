using TeamCreate;

/// <summary>
/// ClockOffset, AdaptiveDelay, and the three interpolation pieces composed (ClockOffset + AdaptiveDelay + SnapshotBuffer) in a seeded
/// network simulation. Test names start with clock_ so they group in the output.
/// </summary>
static class InterpolationClockTests
{
    private static bool Near( double a, double b, double eps = 1e-9 ) => Math.Abs( a - b ) <= eps;

    public static void Run( Action<bool, string> assert )
    {
        RunClockOffset( assert );
        RunAdaptiveDelay( assert );
        RunSimulation( assert );
    }

    // ---------------------------------------------------------------- ClockOffset

    private static void RunClockOffset( Action<bool, string> assert )
    {
        {
            var clock = new ClockOffset();
            assert( !clock.HasEstimate && clock.OffsetMs == 0 && clock.ToLocalMs( 1234.5 ) == 1234.5, "clock_offset_has_no_estimate_before_the_first_observation" );
        }
        {
            // The sender's clock is 1000.5 ms behind ours and every packet takes 25 ms: local receive = sender + 1025.5, whatever the packet.
            var clock = new ClockOffset();
            for ( int i = 0; i < 20; i++ )
            {
                double sender = i * 33.0;
                clock.Observe( sender, sender + 1025.5 );
            }
            assert( clock.HasEstimate && clock.OffsetMs == 1025.5, "clock_offset_constant_one_way_delay_gives_that_offset" );
        }
        {
            // The offset is the smallest (received - sent): the packet that was delayed least. Extra delay on the others must not move it.
            double[] extraDelay = { 40, 12, 55, 8, 33, 70, 9, 21, 64, 15 };
            var clock = new ClockOffset();
            double previous = double.MaxValue;
            bool neverRises = true, neverBelowTruth = true;
            for ( int i = 0; i < extraDelay.Length; i++ )
            {
                double sender = 5000 + i * 33.3;
                clock.Observe( sender, sender + 500 + extraDelay[i] );
                neverRises &= clock.OffsetMs <= previous;
                neverBelowTruth &= clock.OffsetMs >= 500 + 8;
                previous = clock.OffsetMs;
            }
            assert( Near( clock.OffsetMs, 508, 1e-6 ), "clock_offset_jittery_series_converges_to_the_minimum" );
            assert( neverRises && neverBelowTruth, "clock_offset_only_improves_while_the_best_packet_is_in_the_window" );
        }
        {
            // The lowest sample is observation 0. It is still one of the last 64 while 63 newer ones exist, and gone once 64 newer ones exist.
            var clock = new ClockOffset();
            clock.Observe( 0, 100 );
            for ( int i = 1; i <= 63; i++ )
            {
                clock.Observe( i * 10.0, i * 10.0 + 500 );
            }
            bool stillRemembered = clock.OffsetMs == 100;
            clock.Observe( 640, 640 + 500 );
            bool forgotten = clock.OffsetMs == 500;
            assert( stillRemembered, "clock_offset_window_still_counts_the_oldest_of_64_observations" );
            assert( forgotten, "clock_offset_window_forgets_an_observation_after_64_newer_ones" );
        }
        {
            var clock = new ClockOffset();
            clock.Observe( 1000, 1030 );
            assert( clock.ToLocalMs( 2000 ) == 2030 && clock.ToLocalMs( 0 ) == 30, "clock_offset_to_local_adds_the_offset" );
        }
        {
            // A sender whose monotonic clock is ahead of ours gives a negative offset; that is legitimate.
            var clock = new ClockOffset();
            clock.Observe( 5000, 100 );
            assert( clock.HasEstimate && clock.OffsetMs == -4900 && clock.ToLocalMs( 5000 ) == 100, "clock_offset_may_be_negative" );
        }
        {
            var clock = new ClockOffset();
            double[] bad = { double.NaN, double.PositiveInfinity, double.NegativeInfinity };
            foreach ( double v in bad )
            {
                clock.Observe( v, 100 );
                clock.Observe( 100, v );
            }
            clock.Observe( double.MaxValue, -double.MaxValue ); // both finite, but the difference overflows
            bool ignoredAll = !clock.HasEstimate && clock.OffsetMs == 0;
            clock.Observe( 100, 150 );
            foreach ( double v in bad )
            {
                clock.Observe( v, 1 );
                clock.Observe( 1, v );
            }
            assert( ignoredAll && clock.HasEstimate && clock.OffsetMs == 50, "clock_offset_ignores_non_finite_inputs" );
        }
    }

    // ---------------------------------------------------------------- AdaptiveDelay

    private static void RunAdaptiveDelay( Action<bool, string> assert )
    {
        {
            var d = new AdaptiveDelay();
            assert( AdaptiveDelay.FloorMs == 50 && AdaptiveDelay.CeilingMs == 150 && AdaptiveDelay.MaxFallMsPerSecond == 5, "clock_delay_constants_are_floor_50_ceiling_150_fall_5" );
            assert( AdaptiveDelay.StartMs == 80 && d.DelayMs( 0 ) == 80 && d.DelayMs( 60_000 ) == 80, "clock_delay_starts_at_80_and_holds_until_a_gap_is_observed" );
        }
        {
            // 30 Hz with no jitter: the target is 33 + 16.5 = 49.5, which the floor lifts to 50. The delay walks down from the start value at 5 ms a second.
            var d = new AdaptiveDelay();
            double now = 0, delay = 0;
            bool inRange = true, atMostSeventyFiveAfterTwoSeconds = true;
            for ( int i = 0; i < 300; i++ )
            {
                now += 33;
                d.Observe( 33, now );
                delay = d.DelayMs( now );
                inRange &= delay >= 50 && delay <= 80;
                if ( now >= 2000 )
                {
                    atMostSeventyFiveAfterTwoSeconds &= delay <= 75;
                }
            }
            assert( inRange && atMostSeventyFiveAfterTwoSeconds, "clock_delay_steady_33ms_gaps_stay_between_50_and_75" );
            assert( delay == 50, "clock_delay_steady_33ms_gaps_settle_on_the_floor" );
        }
        {
            // Five 33 ms gaps and one 120 ms gap: nearest-rank p95 of six values is the sixth (120), mean is 47.5, target 120 + 23.75 = 143.75.
            var d = new AdaptiveDelay();
            double now = 0;
            for ( int i = 0; i < 5; i++ )
            {
                now += 33;
                d.Observe( 33, now );
            }
            double before = d.DelayMs( now );
            now += 120;
            d.Observe( 120, now );
            double after = d.DelayMs( now );
            assert( before < 80 && before > 79 && Near( after, 143.75 ), "clock_delay_a_120ms_gap_raises_the_delay_at_once" );
        }
        {
            // After a long calm the window holds 32 gaps. One 120 ms gap among 31 of 33 ms is not above the nearest-rank p95 (the 31st of 32 values, 33),
            // so only the mean moves: 33 + 0.5 * (31 * 33 + 120) / 32 = 50.859375. It still rises at once.
            var d = new AdaptiveDelay();
            double now = 0;
            for ( int i = 0; i < 400; i++ )
            {
                now += 33;
                d.Observe( 33, now );
            }
            double calm = d.DelayMs( now );
            now += 120;
            d.Observe( 120, now );
            double after = d.DelayMs( now );
            assert( calm == 50 && Near( after, 50.859375 ), "clock_delay_a_single_spike_in_a_full_window_raises_it_to_the_formula_value_at_once" );
        }
        {
            // Spike, then calm again. The delay may never fall faster than 5 ms per elapsed second, queried at every half-gap.
            var d = new AdaptiveDelay();
            double now = 0;
            for ( int i = 0; i < 5; i++ )
            {
                now += 33;
                d.Observe( 33, now );
            }
            now += 120;
            d.Observe( 120, now );
            double spikeTime = now;
            double previousDelay = d.DelayMs( now ), previousNow = now;
            double peak = previousDelay;
            bool fallLimited = true, neverNearFloorEarly = true;
            double atTenSeconds = 0, timeAtTen = 0, fallStartDelay = 0, fallStartTime = 0;
            for ( int i = 0; i < 320; i++ ) // about 10.5 seconds of steady 33 ms gaps, read at a render frame between arrivals and again at each arrival
            {
                double half = now + 16.5;
                now += 33;
                for ( int step = 0; step < 2; step++ )
                {
                    double t = step == 0 ? half : now;
                    if ( step == 1 )
                    {
                        d.Observe( 33, now );
                    }
                    double delay = d.DelayMs( t );
                    if ( i == 0 && step == 1 )
                    {
                        // The target equals the delay until the first calm gap arrives, so the fall starts here.
                        fallStartDelay = delay;
                        fallStartTime = t;
                    }
                    fallLimited &= previousDelay - delay <= AdaptiveDelay.MaxFallMsPerSecond * (t - previousNow) / 1000.0 + 1e-9;
                    previousDelay = delay;
                    previousNow = t;
                    if ( t - spikeTime <= 1000 )
                    {
                        neverNearFloorEarly &= delay >= peak - AdaptiveDelay.MaxFallMsPerSecond * 1.0 - 1e-9;
                    }
                    atTenSeconds = delay;
                    timeAtTen = t;
                }
            }
            assert( fallLimited, "clock_delay_falls_by_at_most_5ms_per_elapsed_second" );
            assert( neverNearFloorEarly, "clock_delay_one_second_after_a_spike_is_still_within_5ms_of_the_peak" );
            // From the first calm gap on, the target is below the delay for the whole time, so the delay should be falling at exactly the maximum rate, not slower.
            assert( Near( atTenSeconds, fallStartDelay - AdaptiveDelay.MaxFallMsPerSecond * (timeAtTen - fallStartTime) / 1000.0, 1e-6 ), "clock_delay_falls_at_the_maximum_rate_toward_a_lower_target" );

            // 30 more seconds of calm: the spike has long left the 32-gap window, so the target is 49.5 and the delay settles on the 50 ms floor.
            for ( int i = 0; i < 900; i++ )
            {
                now += 33;
                d.Observe( 33, now );
            }
            assert( d.DelayMs( now ) == 50, "clock_delay_forgets_a_spike_after_32_further_gaps_and_settles_on_the_floor" );
        }
        {
            // The result depends on elapsed time, not on how often DelayMs is called.
            var everyFrame = new AdaptiveDelay();
            var onlyAtEnd = new AdaptiveDelay();
            double now = 0;
            for ( int i = 0; i < 6; i++ )
            {
                now += i == 5 ? 120 : 33;
                double gap = i == 5 ? 120 : 33;
                everyFrame.Observe( gap, now );
                onlyAtEnd.Observe( gap, now );
            }
            for ( int i = 0; i < 100; i++ )
            {
                now += 33;
                everyFrame.Observe( 33, now );
                everyFrame.DelayMs( now - 5 );
                everyFrame.DelayMs( now );
                onlyAtEnd.Observe( 33, now );
            }
            assert( Near( everyFrame.DelayMs( now ), onlyAtEnd.DelayMs( now ), 1e-6 ), "clock_delay_does_not_depend_on_how_often_it_is_read" );
        }
        {
            var huge = new AdaptiveDelay();
            huge.Observe( 10_000, 0 );
            double hugeDelay = huge.DelayMs( 0 );

            var zero = new AdaptiveDelay();
            double now = 0;
            bool zeroStaysInRange = true;
            for ( int i = 0; i < 400; i++ )
            {
                now += 33;
                zero.Observe( 0, now );
                double v = zero.DelayMs( now );
                zeroStaysInRange &= v >= 50 && v <= 80;
            }

            var maxValue = new AdaptiveDelay();
            for ( int i = 0; i < 40; i++ )
            {
                maxValue.Observe( double.MaxValue, i * 10.0 ); // the sum of the window overflows to infinity
            }
            double maxDelay = maxValue.DelayMs( 400 );

            assert( hugeDelay == 150 && Near( maxDelay, 150 ), "clock_delay_never_exceeds_150_for_a_10000ms_or_overflowing_gap" );
            assert( zeroStaysInRange && zero.DelayMs( now ) == 50, "clock_delay_never_goes_below_50_for_zero_gaps" );
        }
        {
            var d = new AdaptiveDelay();
            d.Observe( -5, 100 );
            d.Observe( double.NaN, 200 );
            d.Observe( double.PositiveInfinity, 300 );
            d.Observe( double.NegativeInfinity, 400 );
            double stillStart = d.DelayMs( 10_000 ); // had any of them been recorded, the delay would have fallen to the floor by now
            d.Observe( 33, 10_000 );
            double afterTwoSeconds = d.DelayMs( 12_000 );
            assert( stillStart == 80 && Near( afterTwoSeconds, 70 ), "clock_delay_ignores_negative_and_non_finite_gaps" );
        }
        {
            var d = new AdaptiveDelay();
            d.Observe( 33, 0 );
            d.Observe( 33, double.NaN );
            double a = d.DelayMs( double.NaN );
            double b = d.DelayMs( double.PositiveInfinity );
            double c = d.DelayMs( 1000 );
            assert( a >= 50 && a <= 150 && b >= 50 && b <= 150 && Near( c, 75 ), "clock_delay_ignores_a_non_finite_clock_reading" );
        }
        {
            // Readings need not arrive in strict time order (an arrival stamped after the frame's own clock reading, say). Time that was already
            // counted must not be counted twice, or the delay would fall faster than its limit.
            var d = new AdaptiveDelay();
            double now = 0;
            for ( int i = 0; i < 5; i++ )
            {
                now += 33;
                d.Observe( 33, now );
            }
            now += 120;
            d.Observe( 120, now );
            double peak = d.DelayMs( now );
            double worstFall = 0;
            for ( int i = 1; i <= 100; i++ )
            {
                d.Observe( 33, now + i * 33 ); // an arrival stamped 33 ms ahead...
                double behind = d.DelayMs( now + i * 33 - 16.5 ); // ...then a frame read from slightly earlier
                double current = d.DelayMs( now + i * 33 );
                worstFall = Math.Max( worstFall, peak - current - AdaptiveDelay.MaxFallMsPerSecond * (i * 33) / 1000.0 );
                worstFall = Math.Max( worstFall, peak - behind - AdaptiveDelay.MaxFallMsPerSecond * (i * 33) / 1000.0 );
            }
            assert( worstFall <= 1e-9, "clock_delay_a_clock_reading_that_goes_backwards_never_lets_it_fall_faster" );
        }
    }

    // ---------------------------------------------------------------- Composition

    private const double TrueOffsetMs = 123_456.789; // local clock minus the sender's clock; neither is known to the receiver
    private const double BaseDelayMs = 20;
    private const double SendIntervalMs = 33.3;
    private const double JitterMs = 15;
    private const double FrameMs = 1000.0 / 60.0;

    /// <summary>Where the sender really was at a sender-clock time: a steady run along +X with a slow weave in Y and Z, turning 90 degrees a second.</summary>
    private static Pose PathAt( double senderMs )
    {
        double s = senderMs / 1000.0;
        double half = Math.PI / 4 * s;
        return new Pose( (float)(300.0 * s), (float)(40.0 * Math.Sin( Math.PI * s )), (float)(10.0 * Math.Cos( Math.PI * s )), 0f, 0f, (float)Math.Sin( half ), (float)Math.Cos( half ) );
    }

    private static double Distance( Pose a, Pose b )
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt( dx * dx + dy * dy + dz * dz );
    }

    private sealed record SimStats( int Frames, int Missed, double MaxStep, double MedianStep, double MaxDeviation, double MaxRotationError, double MinDelay, double MaxDelay );

    /// <summary>
    /// One peer sends every 33.3 ms with its own clock; packets arrive with +-15 ms of jitter on top of a 20 ms base delay; the receiver
    /// renders at 60 Hz at (local now - clock offset - adaptive delay). The first second is warm-up and is not measured, the next five seconds are.
    /// Warm-up matters: with the first packet's single, possibly 15 ms wrong, clock estimate and one or two samples in the buffer, the first few
    /// hundred milliseconds do not meet the 2x bound (measured over 200 seeds: worst step ratio 2.3 with no warm-up, 1.5 after 300 ms, 1.2 after
    /// 1 s), so the code that draws a peer should wait for a clock estimate and a few samples rather than draw its very first packet.
    /// </summary>
    private static SimStats Simulate( int seed )
    {
        var rng = new Random( seed );
        var packets = new List<(double SentMs, double ArrivalMs)>();
        for ( double sent = 5000; sent <= 5000 + 8000; sent += SendIntervalMs )
        {
            double jitter = (rng.NextDouble() * 2 - 1) * JitterMs;
            packets.Add( (sent, sent + TrueOffsetMs + BaseDelayMs + jitter) );
        }
        packets.Sort( ( a, b ) => a.ArrivalMs.CompareTo( b.ArrivalMs ) );

        var clock = new ClockOffset();
        var delay = new AdaptiveDelay();
        var buffer = new SnapshotBuffer();

        double start = 5000 + TrueOffsetMs;
        double measureFrom = start + 1000, measureTo = measureFrom + 5000;
        int next = 0, frames = 0, missed = 0;
        double lastArrival = double.NaN, maxDeviation = 0, maxRotationError = 0, minDelay = double.MaxValue, maxDelay = 0;
        Pose? previous = null;
        var steps = new List<double>();
        for ( double now = start; now < measureTo; now += FrameMs )
        {
            while ( next < packets.Count && packets[next].ArrivalMs <= now )
            {
                var (sent, arrival) = packets[next++];
                clock.Observe( sent, arrival );
                if ( !double.IsNaN( lastArrival ) )
                {
                    delay.Observe( arrival - lastArrival, arrival );
                }
                lastArrival = arrival;
                buffer.Add( sent, PathAt( sent ) );
            }
            if ( !clock.HasEstimate )
            {
                continue;
            }
            double currentDelay = delay.DelayMs( now );
            double renderTime = now - clock.OffsetMs - currentDelay;
            bool got = buffer.TrySample( renderTime, out var pose );
            if ( now < measureFrom )
            {
                continue;
            }
            frames++;
            minDelay = Math.Min( minDelay, currentDelay );
            maxDelay = Math.Max( maxDelay, currentDelay );
            if ( !got )
            {
                missed++;
                continue;
            }
            var truth = PathAt( renderTime );
            maxDeviation = Math.Max( maxDeviation, Distance( pose, truth ) );
            maxRotationError = Math.Max( maxRotationError, Math.Abs( pose.Qz - truth.Qz ) + Math.Abs( pose.Qw - truth.Qw ) );
            if ( previous is Pose p )
            {
                steps.Add( Distance( pose, p ) );
            }
            previous = pose;
        }
        steps.Sort();
        double median = steps.Count == 0 ? 0 : steps[steps.Count / 2];
        return new SimStats( frames, missed, steps.Count == 0 ? 0 : steps[^1], median, maxDeviation, maxRotationError, minDelay, maxDelay );
    }

    private static void RunSimulation( Action<bool, string> assert )
    {
        var main = Simulate( 20261002 );
        assert( main.Frames >= 299 && main.Missed == 0, "clock_sim_every_rendered_frame_found_a_pose" );
        assert( main.MedianStep > 0 && main.MaxStep <= 2.0 * main.MedianStep, "clock_sim_largest_frame_step_is_at_most_twice_the_median" );
        // Linear interpolation of a curved path cuts corners by at most (largest acceleration) * (sample spacing)^2 / 8 = 394.8 * 0.0333^2 / 8 = 0.055 units here,
        // so 0.1 means "on the path" with room for the occasional few milliseconds of extrapolation, and a rendering that is late or early by even 1 ms is well outside it.
        assert( main.MaxDeviation <= 0.1, "clock_sim_sampled_position_stays_on_the_senders_path" );
        assert( main.MaxRotationError <= 0.01, "clock_sim_sampled_rotation_stays_on_the_senders_path" );
        assert( main.MinDelay >= AdaptiveDelay.FloorMs && main.MaxDelay <= AdaptiveDelay.CeilingMs, "clock_sim_delay_stays_inside_the_floor_and_ceiling" );

        // The bound should be a property of the design, not of one lucky seed.
        bool allSmooth = true, allOnPath = true, allFound = true;
        string worst = "";
        for ( int seed = 1; seed <= 20; seed++ )
        {
            var s = Simulate( seed );
            bool smooth = s.MaxStep <= 2.0 * s.MedianStep;
            allSmooth &= smooth;
            allOnPath &= s.MaxDeviation <= 0.1;
            allFound &= s.Missed == 0;
            if ( !smooth )
            {
                worst += " seed " + seed + " ratio " + (s.MaxStep / s.MedianStep).ToString( "F2" );
            }
        }
        assert( allFound, "clock_sim_twenty_seeds_every_frame_found_a_pose" );
        assert( allSmooth, "clock_sim_twenty_seeds_largest_step_is_at_most_twice_the_median" + worst );
        assert( allOnPath, "clock_sim_twenty_seeds_position_stays_on_the_senders_path" );
    }
}
