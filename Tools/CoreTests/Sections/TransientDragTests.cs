using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// Live drag of objects on the other editor: the transient channel (never journaled, never echoed), its cadence, its host-side guards, the
/// receiver that blends the samples, and the synthetic smoothness proof. Every assertion name starts with <c>drag_</c>.
/// </summary>
static class TransientDragTests
{
    private const string Scene = "scenes/drag.scene";

    private static string Id( int n ) => new Guid( n, 0, 0, new byte[8] ).ToString();

    private static Pose At( float x, float y = 0f, float z = 0f ) => new Pose( x, y, z, 0f, 0f, 0f, 1f );

    private static TransientItem Item( int n, float x ) => new TransientItem( Id( n ), At( x ) );

    public static void Run( Action<bool, string> assert )
    {
        Codec( assert );
        Cadence( assert );
        RateLimit( assert );
        Echo( assert );
        ReceiverRules( assert );
        Interpolation( assert );
        ServerBehaviour( assert );
        Bandwidth( assert );
    }

    // ---- The packet --------------------------------------------------------------------------------------------------------------------

    private static void Codec( Action<bool, string> assert )
    {
        assert( TransientLimits.Kind == "transient", "drag_kind_is_transient" );

        var pose = new Pose( 1234.5678f, -2.25f, 0.0004f, 0.1234567f, -0.2f, 0.3f, 0.9f, 1.5f, 2f, 0.25f );
        var packet = TransientCodec.Build( Scene, "peer-a", 98765.4321, new[] { new TransientItem( Id( 1 ), pose ) }, final: false );
        string wire = JsonSerializer.Serialize( packet );
        var back = JsonSerializer.Deserialize<Packet>( wire );
        string error = TransientCodec.Validate( back, out var items );
        assert( error == null && items.Count == 1 && items[0].Id == Id( 1 ), $"drag_packet_round_trips_through_the_real_serializer: {error}" );
        var got = items[0].Pose;
        assert( Math.Abs( got.X - 1234.568f ) < 1e-3 && Math.Abs( got.Y + 2.25f ) < 1e-6 && Math.Abs( got.Z - 0f ) < 1e-3, "drag_position_is_rounded_to_three_decimals" );
        assert( Math.Abs( got.Qx - 0.1235f ) < 1e-6 && Math.Abs( got.Qw - 0.9f ) < 1e-6, "drag_rotation_is_rounded_to_four_decimals" );
        assert( got.Sx == 1.5f && got.Sy == 2f && got.Sz == 0.25f, "drag_scale_round_trips" );
        assert( Math.Abs( back.SentMs - 98765.4 ) < 0.06, "drag_sent_ms_is_stamped_to_a_tenth_of_a_millisecond" );
        assert( !wire.Contains( "Presence" ) && !wire.Contains( "Changes" ), "drag_packet_carries_no_scene_state" );

        int entry = TransientCodec.EntryBytes( new TransientItem( Id( 1 ), pose ) );
        assert( entry <= TransientLimits.MaxEntryBytes, $"drag_one_object_entry_is_at_most_160_bytes: {entry}" );

        var many = Enumerable.Range( 1, 40 ).Select( n => Item( n, n * 1.25f ) ).ToList();
        var capped = TransientCodec.Build( Scene, "peer-a", 1000, many, final: false );
        string cappedError = TransientCodec.Validate( capped, out var cappedItems );
        assert( cappedError == null && cappedItems.Count == TransientLimits.MaxItems, $"drag_build_truncates_a_big_batch_to_16_instead_of_refusing: {cappedItems?.Count} {cappedError}" );
        assert( cappedItems != null && cappedItems[0].Id == Id( 1 ) && cappedItems[15].Id == Id( 16 ), "drag_truncation_keeps_the_first_items_which_are_the_newest_moved" );
        assert( JsonSerializer.SerializeToUtf8Bytes( capped.Transient ).Length <= TransientLimits.MaxPacketBytes, "drag_built_batch_is_within_4_kb" );

        assert( TransientCodec.Validate( TransientCodec.Build( Scene, "p", 1, new List<TransientItem>(), final: false ), out _ ) != null, "drag_empty_batch_is_refused" );

        // Hostile content: each is refused with a reason, never an exception.
        string Bad( Action<Packet> hurt )
        {
            var p = TransientCodec.Build( Scene, "p", 1, new[] { Item( 1, 1f ) }, final: false );
            hurt( p );
            return TransientCodec.Validate( p, out _ );
        }
        assert( Bad( p => p.Transient[0][1] = double.NaN ) != null, "drag_nan_is_refused" );
        assert( Bad( p => p.Transient[0][2] = 1e9 ) != null, "drag_value_beyond_1e7_is_refused" );
        assert( Bad( p => p.Transient[0][3] = -1e7 - 1 ) != null, "drag_negative_value_beyond_1e7_is_refused" );
        assert( Bad( p => p.Transient[0][0] = "not-a-guid" ) != null, "drag_bad_object_id_is_refused" );
        assert( Bad( p => p.Transient[0][0] = 5 ) != null, "drag_non_string_object_id_is_refused" );
        assert( Bad( p => p.Transient[0][4] = "text" ) != null, "drag_non_numeric_value_is_refused" );
        assert( Bad( p => p.Transient[0].AsArray().RemoveAt( 10 ) ) != null, "drag_short_item_is_refused" );
        assert( Bad( p => p.Transient = null ) != null, "drag_missing_payload_is_refused" );
        assert( Bad( p => p.Transient[0] = "scalar" ) != null, "drag_non_array_item_is_refused" );
        assert( Bad( p => p.Scene = null ) != null, "drag_missing_scene_is_refused" );
        assert( Bad( p => p.SentMs = double.NaN ) != null, "drag_non_finite_stamp_is_refused" );
        assert( Bad( p => p.Kind = "edit" ) != null, "drag_wrong_kind_is_refused" );
        assert( Bad( p => { for ( int i = 2; i <= 17; i++ ) p.Transient.Add( TransientCodec.Encode( Item( i, 1f ) ) ); } ) != null, "drag_17_items_are_refused_by_the_host_side_check" );
        var padded = TransientCodec.Build( Scene, "p", 1, new[] { Item( 1, 1f ) }, final: false );
        padded.Transient[0][0] = new string( 'a', 5000 );
        assert( TransientCodec.Validate( padded, out _ ) != null, "drag_payload_over_4_kb_is_refused" );
    }

    // ---- The sender's cadence ------------------------------------------------------------------------------------------------------------

    private static void Cadence( Action<bool, string> assert )
    {
        assert( TransientCadence.MinIntervalMs == 33 && SendCadence.PresenceMinimum.TotalMilliseconds == 33, "drag_cadence_floor_is_33_ms_like_presence" );

        var c = new TransientCadence();
        assert( c.TakeDue( 0 ).Count == 0, "drag_idle_sends_nothing" );
        c.Update( Id( 1 ), At( 0f ), 0 );
        assert( c.TakeDue( 0 ).Count == 0 && c.TakeDue( 500 ).Count == 0, "drag_selecting_an_object_without_moving_it_sends_nothing" );
        c.Update( Id( 1 ), At( 0.5f ), 0 );
        var first = c.TakeDue( 0 );
        assert( first.Count == 1, "drag_first_moved_sample_of_an_object_is_sent_at_once" );
        c.Update( Id( 1 ), At( 5f ), 10 );
        assert( c.TakeDue( 10 ).Count == 0 && c.TakeDue( 32 ).Count == 0, "drag_object_is_not_sent_again_within_33_ms" );
        var second = c.TakeDue( 33 );
        assert( second.Count == 1 && second[0].Pose.X == 5f, "drag_object_is_sent_again_after_33_ms_with_the_latest_pose" );

        c.Update( Id( 1 ), At( 5.0005f ), 100 );
        assert( c.TakeDue( 200 ).Count == 0, "drag_position_change_below_0_001_is_not_sent" );
        c.Update( Id( 1 ), At( 5.0015f ), 100 );
        assert( c.TakeDue( 200 ).Count == 1, "drag_position_change_of_0_0015_is_sent" );

        var r = new TransientCadence();
        r.Update( Id( 2 ), At( 0f ), 0 );
        r.Update( Id( 2 ), At( 1f ), 0 );
        r.Update( Id( 2 ), At( 0f ), 0 );
        r.TakeDue( 0 );
        r.TakeDue( 40 );
        r.Update( Id( 2 ), new Pose( 0, 0, 0, 0.0004f, 0, 0, 1f ), 50 );
        assert( r.TakeDue( 100 ).Count == 0, "drag_rotation_change_below_0_0005_is_not_sent" );
        r.Update( Id( 2 ), new Pose( 0, 0, 0, 0.0006f, 0, 0, 1f ), 50 );
        assert( r.TakeDue( 100 ).Count == 1, "drag_rotation_change_of_0_0006_is_sent" );
        r.Update( Id( 2 ), new Pose( 0, 0, 0, -0.0006f, 0, 0, -1f ), 150 );
        assert( r.TakeDue( 300 ).Count == 0, "drag_negated_quaternion_is_the_same_rotation_and_is_not_sent" );

        var s = new TransientCadence();
        s.Update( Id( 3 ), At( 0f ), 0 );
        s.Update( Id( 3 ), At( 1f ), 0 );
        s.TakeDue( 0 );
        s.Update( Id( 3 ), At( 0f ), 0 );
        s.TakeDue( 40 );
        s.Update( Id( 3 ), new Pose( 0, 0, 0, 0, 0, 0, 1f, 2f, 1f, 1f ), 40 );
        assert( s.TakeDue( 100 ).Count == 1, "drag_scale_only_change_is_sent_so_a_pure_scale_drag_is_not_lost" );

        // More objects than one packet carries: the newest-moved go first, the rest follow on the next tick, nothing is refused.
        var m = new TransientCadence();
        for ( int n = 1; n <= 20; n++ ) m.Update( Id( n ), At( n ), 0 );
        for ( int n = 1; n <= 20; n++ ) m.Update( Id( n ), At( n + 0.5f ), n * 10 );
        var batch = m.TakeDue( 500 );
        assert( batch.Count == TransientLimits.MaxItems, $"drag_a_tick_returns_at_most_16_objects: {batch.Count}" );
        assert( batch[0].Id == Id( 20 ) && batch.All( b => b.Id != Id( 1 ) && b.Id != Id( 4 ) ), "drag_overflow_keeps_the_newest_moved_objects" );
        var rest = m.TakeDue( 534 );
        assert( rest.Count == 4, $"drag_the_objects_that_did_not_fit_go_out_on_the_next_tick: {rest.Count}" );

        // Ending a drag: the exact last pose goes out once when it was not already sent, then the state is forgotten.
        var f = new TransientCadence();
        f.Update( Id( 4 ), At( 0f ), 0 );
        f.Update( Id( 4 ), At( 1f ), 0 );
        f.TakeDue( 0 );
        f.Update( Id( 4 ), At( 9f ), 10 );
        var last = f.TakeFinal();
        assert( last.Count == 1 && last[0].Pose.X == 9f, "drag_final_carries_the_exact_last_pose" );
        assert( f.TakeFinal().Count == 0 && f.TakeDue( 1000 ).Count == 0, "drag_nothing_is_sent_after_the_final" );
        var g = new TransientCadence();
        g.Update( Id( 5 ), At( 0f ), 0 );
        g.Update( Id( 5 ), At( 1f ), 0 );
        g.TakeDue( 0 );
        assert( g.TakeFinal().Count == 1, "drag_final_of_an_object_whose_last_pose_was_already_sent_still_closes_the_stream" );

        c.Update( Id( 1 ), At( 99f ), 1000 );
        assert( c.IsMoving( Id( 1 ), 1100 ) && !c.IsMoving( Id( 1 ), 1400 ) && !c.IsMoving( Id( 9 ), 1100 ), "drag_an_object_counts_as_dragged_locally_for_250_ms_after_it_last_moved" );
    }

    // ---- The host's rate limit ----------------------------------------------------------------------------------------------------------

    private static void RateLimit( Action<bool, string> assert )
    {
        var limit = new TransientRateLimit();
        int allowed = 0;
        for ( int i = 0; i < 100; i++ ) if ( limit.Allow( "a", i * 5.0 ) ) allowed++;
        assert( allowed == 40, $"drag_rate_limit_allows_40_in_the_first_second_of_a_burst: {allowed}" );
        assert( limit.Allow( "b", 100 ), "drag_rate_limit_is_per_member" );
        assert( limit.Allow( "a", 1001 ), "drag_rate_limit_recovers_as_the_window_moves_on" );
        var steady = new TransientRateLimit();
        int ok = 0;
        for ( int i = 0; i < 300; i++ ) if ( steady.Allow( "a", i * 33.0 ) ) ok++;
        assert( ok == 300, $"drag_a_steady_30_hz_stream_is_never_limited: {ok}" );
    }

    // ---- Echo suppression ---------------------------------------------------------------------------------------------------------------

    private static ObjectState Cube( int n, string position, string name = "Cube" )
    {
        return new ObjectState( Id( n ), "", new JsonObject
        {
            ["__guid"] = Id( n ), ["Name"] = name, ["Position"] = position, ["Rotation"] = "0,0,0,1", ["Scale"] = "1,1,1", ["Enabled"] = true,
            ["Tags"] = "", ["Components"] = new JsonArray()
        } );
    }

    private static void Echo( Action<bool, string> assert )
    {
        var known = Cube( 1, "0,0,0" );
        var shown = Cube( 1, "12.5,3,0" );
        var masked = TransientEcho.CopyTransform( shown, known );
        var diff = StateRules.Diff( new Dictionary<string, ObjectState> { [Id( 1 )] = known }, new Dictionary<string, ObjectState> { [Id( 1 )] = masked } );
        assert( diff.Count == 0, "drag_a_transient_write_is_not_seen_by_the_scan_as_a_local_edit" );
        var unmasked = StateRules.Diff( new Dictionary<string, ObjectState> { [Id( 1 )] = known }, new Dictionary<string, ObjectState> { [Id( 1 )] = shown } );
        assert( unmasked.Count == 1, "drag_without_the_mask_the_same_write_would_be_sent_back" );

        var renamed = Cube( 1, "12.5,3,0", "Renamed" );
        var keepsName = TransientEcho.CopyTransform( renamed, known );
        assert( keepsName.Data["Name"].GetValue<string>() == "Renamed" && keepsName.Data["Position"].GetValue<string>() == "0,0,0", "drag_the_mask_covers_only_the_three_transform_fields" );
        assert( renamed.Data["Position"].GetValue<string>() == "12.5,3,0", "drag_the_mask_does_not_modify_its_input" );
        assert( ReferenceEquals( TransientEcho.CopyTransform( shown, null ), shown ), "drag_nothing_to_copy_from_leaves_the_state_alone" );
    }

    // ---- The receiver -------------------------------------------------------------------------------------------------------------------

    /// <summary>Feeds a steady stream: one sample every 33 ms whose sender stamp is 100 ms behind the receiver's clock.</summary>
    private static void Stream( TransientReceiver r, string peer, int id, int from, int count, bool finalLast = false )
    {
        for ( int i = 0; i < count; i++ )
        {
            double sent = 1000 + ( from + i ) * 33.0;
            r.OnTransient( peer, Scene, sent, Item( id, ( from + i ) * 3f ), final: finalLast && i == count - 1, nowMs: sent + 100, localDragging: false );
        }
    }

    private static void ReceiverRules( Action<bool, string> assert )
    {
        // Applied, and written at a render time behind live.
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 10 );
            var writes = new List<(string Scene, string Id, Pose Pose)>();
            r.Sample( 1000 + 9 * 33.0 + 100 + 20, writes );
            assert( writes.Count == 1 && writes[0].Scene == Scene && writes[0].Id == Id( 1 ), "drag_receiver_writes_a_pose_for_an_active_stream" );
            assert( writes[0].Pose.X > 0f && writes[0].Pose.X < 9 * 3f, $"drag_receiver_renders_behind_the_newest_sample: {writes[0].Pose.X}" );
            assert( r.IsActive( Scene, Id( 1 ), 1500 ), "drag_a_stream_in_progress_is_active" );
        }

        // Local wins during a drag of the same object.
        {
            var r = new TransientReceiver();
            var verdict = TransientVerdict.Applied;
            for ( int i = 0; i < 6; i++ ) verdict = r.OnTransient( "A", Scene, 1000 + i * 33.0, Item( 1, i ), false, 1100 + i * 33.0, localDragging: true );
            var writes = new List<(string, string, Pose)>();
            r.Sample( 1500, writes );
            assert( verdict == TransientVerdict.DroppedLocalDrag && writes.Count == 0 && !r.IsActive( Scene, Id( 1 ), 1500 ), "drag_a_local_drag_of_the_same_object_drops_the_transient" );
        }

        // Invalid pose is ignored.
        {
            var r = new TransientReceiver();
            var verdict = r.OnTransient( "A", Scene, 1000, new TransientItem( Id( 1 ), At( float.NaN ) ), false, 1100, false );
            assert( verdict == TransientVerdict.Invalid && !r.IsActive( Scene, Id( 1 ), 1100 ), "drag_receiver_ignores_a_non_finite_pose" );
        }

        // Final, then the accepted edit: the stream ends exactly at the final pose and the authoritative state takes over.
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 8 );
            r.OnTransient( "A", Scene, 1000 + 8 * 33.0, Item( 1, 100f ), final: true, nowMs: 1000 + 8 * 33.0 + 100, localDragging: false );
            var writes = new List<(string Scene, string Id, Pose Pose)>();
            r.Sample( 1000 + 8 * 33.0 + 100 + 400, writes );
            assert( writes.Count == 1 && writes[0].Pose.X == 100f, $"drag_stream_holds_the_final_pose_without_overshoot: {writes.FirstOrDefault().Pose.X}" );
            assert( r.IsActive( Scene, Id( 1 ), 1000 + 8 * 33.0 + 100 + 400 ), "drag_final_pose_stays_masked_until_the_accepted_edit_lands" );
            r.OnAccepted( Scene, Id( 1 ), final: true, nowMs: 2000 );
            writes.Clear();
            r.Sample( 2010, writes );
            assert( writes.Count == 0 && !r.IsActive( Scene, Id( 1 ), 2010 ), "drag_final_accepted_ends_the_stream_and_nothing_is_written_over_it" );
        }

        // A mid-drag accepted edit does not end the stream (the shown pose is kept by the session).
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 8 );
            r.OnAccepted( Scene, Id( 1 ), final: false, nowMs: 1400 );
            assert( r.IsActive( Scene, Id( 1 ), 1400 ), "drag_a_mid_drag_accepted_edit_keeps_the_stream_going" );
        }

        // Stale after accepted: a transient older than the accepted state is discarded, a later drag is not.
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 10 );
            r.OnAccepted( Scene, Id( 1 ), final: true, nowMs: 1500 );
            var late = r.OnTransient( "A", Scene, 1000 + 9 * 33.0 + 5, Item( 1, 27.5f ), false, 1510, false );
            assert( late == TransientVerdict.DroppedStale, $"drag_stale_transient_after_the_accepted_state_is_discarded: {late}" );
            assert( !r.IsActive( Scene, Id( 1 ), 1510 ), "drag_a_stale_transient_does_not_restart_the_stream_no_rubber_banding" );
            var writes = new List<(string, string, Pose)>();
            r.Sample( 1520, writes );
            assert( writes.Count == 0, "drag_nothing_is_written_for_a_stale_transient" );
            var fresh = r.OnTransient( "A", Scene, 1000 + 20 * 33.0, Item( 1, 60f ), false, 1000 + 20 * 33.0 + 100, false );
            assert( fresh == TransientVerdict.Applied && r.IsActive( Scene, Id( 1 ), 1760 ), "drag_a_new_drag_after_the_accepted_state_is_applied" );
        }

        // 500 ms of silence: the last pose is held (no more writes), still masked, and given back after the hold limit.
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 8 );
            double lastArrival = 1000 + 7 * 33.0 + 100;
            var writes = new List<(string, string, Pose)>();
            r.Sample( lastArrival + 200, writes );
            int whileLive = writes.Count;
            writes.Clear();
            r.Sample( lastArrival + 700, writes );
            r.Sample( lastArrival + 800, writes );
            assert( whileLive == 1 && writes.Count <= 1, $"drag_after_500_ms_without_packets_writing_stops: {writes.Count}" );
            writes.Clear();
            r.Sample( lastArrival + 900, writes );
            assert( writes.Count == 0 && r.IsActive( Scene, Id( 1 ), lastArrival + 900 ), "drag_a_silent_stream_holds_its_last_pose_and_stays_masked" );
            assert( r.TakeExpired( lastArrival + 1000 ).Count == 0, "drag_hold_is_not_given_up_early" );
            var expired = r.TakeExpired( lastArrival + TransientLimits.HoldLimitMs + 1 );
            assert( expired.Count == 1 && expired[0] == (Scene, Id( 1 )) && !r.IsActive( Scene, Id( 1 ), lastArrival + TransientLimits.HoldLimitMs + 1 ), "drag_a_pose_never_confirmed_by_an_accepted_edit_is_given_back_after_the_hold_limit" );
        }

        // One clock and one delay per sending peer, and the stamps of one peer are never compared with another's.
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 8 );
            for ( int i = 0; i < 8; i++ ) r.OnTransient( "B", Scene, 50 + i * 33.0, Item( 2, i ), false, 5000 + i * 33.0, false );
            var writes = new List<(string Scene, string Id, Pose Pose)>();
            r.Sample( 5000 + 7 * 33.0 + 20, writes );
            assert( writes.Any( w => w.Id == Id( 2 ) ), "drag_two_senders_with_unrelated_clocks_are_both_rendered" );
        }

        // A late or repeated packet is never inserted behind newer motion.
        {
            var r = new TransientReceiver();
            Stream( r, "A", 1, 0, 6 );
            var old = r.OnTransient( "A", Scene, 1000 + 1 * 33.0 + 7, Item( 1, 999f ), false, 1000 + 6 * 33.0 + 100, false );
            assert( old == TransientVerdict.DroppedOld, "drag_a_packet_older_than_the_newest_from_that_peer_is_ignored" );
        }
    }

    // ---- The smoothness proof (T8.3, pure form) --------------------------------------------------------------------------------------------

    /// <summary>The path being dragged: a drift with a wobble, so the velocity is never constant.</summary>
    private static double PathX( double seconds ) => 100.0 * seconds + 20.0 * Math.Sin( 2 * Math.PI * 0.7 * seconds );

    private static (double Median, double Largest, double MinDelay, double MaxDelay, double AverageDelay, bool Finite, double LargestInBurst) Simulate( int seed, bool jitterEvery, double lossRate, double burstFromMs, double burstMs )
    {
        var random = new Random( seed );
        const double sendMs = 1000.0 / 30.0;
        const double latencyMs = 40.0;
        const double durationMs = 10_000.0;
        var arrivals = new List<(double Arrival, double Sent, int Index)>();
        for ( int i = 0; i * sendMs <= durationMs; i++ )
        {
            double sent = 5000.0 + i * sendMs;
            double offered = i * sendMs;
            if ( random.NextDouble() < lossRate ) continue;
            if ( burstMs > 0 && offered >= burstFromMs && offered < burstFromMs + burstMs ) continue;
            bool jittered = jitterEvery || random.NextDouble() < 0.20;
            double jitter = jittered ? ( random.NextDouble() * 2 - 1 ) * 15.0 : 0.0;
            arrivals.Add( (offered + latencyMs + jitter, sent, i) );
        }
        arrivals.Sort( ( a, b ) => a.Arrival.CompareTo( b.Arrival ) );

        var receiver = new TransientReceiver();
        var writes = new List<(string Scene, string Id, Pose Pose)>();
        int next = 0;
        double previousX = double.NaN;
        var steps = new List<double>();
        bool finite = true;
        double delaySum = 0, minDelay = double.MaxValue, maxDelay = 0;
        int delayCount = 0;
        double largestInBurst = 0;
        double tick = 1000.0 / 120.0;
        for ( double now = 0; now <= durationMs; now += tick )
        {
            while ( next < arrivals.Count && arrivals[next].Arrival <= now )
            {
                var a = arrivals[next++];
                double x = PathX( a.Index * sendMs / 1000.0 );
                receiver.OnTransient( "A", Scene, a.Sent, new TransientItem( Id( 1 ), At( (float)x ) ), false, a.Arrival, false );
            }
            writes.Clear();
            receiver.Sample( now, writes );
            if ( writes.Count == 1 )
            {
                double x = writes[0].Pose.X;
                finite &= double.IsFinite( x ) && double.IsFinite( writes[0].Pose.Y ) && double.IsFinite( writes[0].Pose.Qw );
                if ( !double.IsNaN( previousX ) )
                {
                    double step = Math.Abs( x - previousX );
                    steps.Add( step );
                    if ( burstMs > 0 && now >= burstFromMs && now <= burstFromMs + burstMs + 300 ) largestInBurst = Math.Max( largestInBurst, step );
                }
                previousX = x;
            }
            if ( now >= 2000 )
            {
                double d = receiver.DelayMs( "A", now );
                delaySum += d;
                delayCount++;
                minDelay = Math.Min( minDelay, d );
                maxDelay = Math.Max( maxDelay, d );
            }
        }
        // The first second only warms the clock and the delay up: the step statistics start once the picture is moving continuously.
        var moving = steps.Skip( steps.Count / 10 ).Where( s => s > 0 ).OrderBy( s => s ).ToList();
        double median = moving[moving.Count / 2];
        return (median, moving.Max(), minDelay, maxDelay, delaySum / delayCount, finite, largestInBurst);
    }

    private static void Interpolation( Action<bool, string> assert )
    {
        foreach ( var (name, everyPacket) in new[] { ("jitter_on_every_packet", true), ("jitter_on_20_percent_of_packets", false) } )
        {
            var m = Simulate( seed: 7, jitterEvery: everyPacket, lossRate: 0.02, burstFromMs: 0, burstMs: 0 );
            Console.WriteLine( $"INFO: drag smoothness {name}: 30 Hz drag, +-15 ms jitter, 2% loss, 120 Hz render for 10 s: median step {m.Median:F3}, largest step {m.Largest:F3} ({m.Largest / m.Median:F2}x median), render delay {m.MinDelay:F1}-{m.MaxDelay:F1} ms (average {m.AverageDelay:F1} ms)" );
            assert( m.Finite, $"drag_{name}_never_renders_a_non_finite_value" );
            assert( m.Largest <= 2 * m.Median, $"drag_{name}_largest_step_is_at_most_twice_the_median: {m.Largest:F3} vs {m.Median:F3}" );
            assert( m.AverageDelay >= 50 && m.AverageDelay <= 100, $"drag_{name}_average_render_delay_is_50_to_100_ms: {m.AverageDelay:F1}" );
        }

        // A loss burst of 150 ms: no step beyond what the extrapolation limit allows.
        var burst = Simulate( seed: 11, jitterEvery: true, lossRate: 0, burstFromMs: 5000, burstMs: 150 );
        double fastest = 100.0 + 20.0 * 2 * Math.PI * 0.7;
        double limit = fastest * SnapshotBuffer.MaxExtrapolationMs / 1000.0;
        Console.WriteLine( $"INFO: drag smoothness through a 150 ms loss burst: largest step {burst.LargestInBurst:F3} (extrapolation limit {limit:F3}, median step {burst.Median:F3})" );
        assert( burst.Finite && burst.LargestInBurst <= limit, $"drag_a_150_ms_loss_burst_never_steps_beyond_the_extrapolation_limit: {burst.LargestInBurst:F3} vs {limit:F3}" );
    }

    // ---- The host --------------------------------------------------------------------------------------------------------------------

    private sealed class Rig : IDisposable
    {
        public SessionServer Server;
        public string Directory;
        public string JournalPath;
        public readonly List<Wire> Clients = new();
        public readonly List<List<Packet>> Seen = new();
        public readonly List<string> Peers = new();

        public void Pump( int milliseconds )
        {
            var until = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < until )
            {
                Server.Pump();
                for ( int i = 0; i < Clients.Count; i++ ) while ( Clients[i].Incoming.TryDequeue( out var packet ) ) Seen[i].Add( packet );
                Thread.Sleep( 3 );
            }
        }

        public void PumpUntil( Func<bool> done, string what )
        {
            var deadline = DateTime.UtcNow.AddSeconds( 6 );
            while ( DateTime.UtcNow < deadline )
            {
                Pump( 10 );
                if ( done() ) return;
            }
            throw new TimeoutException( "Timed out waiting for " + what );
        }

        /// <summary>A ping and its pong: everything the host did because of what the client sent before it has happened once the pong is back.</summary>
        public void Settle( int client )
        {
            int pongs = Seen[client].Count( p => p.Kind == "pong" );
            Clients[client].Send( new Packet { Kind = "ping" } );
            PumpUntil( () => Seen[client].Count( p => p.Kind == "pong" ) > pongs, "a pong" );
            Pump( 60 );
        }

        public int Revision()
        {
            Clients[0].Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState>() } );
            int before = Seen[0].Count( p => p.Kind == "snapshot" );
            PumpUntil( () => Seen[0].Count( p => p.Kind == "snapshot" ) > before, "a snapshot" );
            return (int)Seen[0].Last( p => p.Kind == "snapshot" ).Revision;
        }

        public long JournalBytes() => new FileInfo( JournalPath ).Length;

        public int Count( int client, string kind ) => Seen[client].Count( p => p.Kind == kind );

        public void Dispose()
        {
            foreach ( var c in Clients ) c.Dispose();
            Server?.Dispose();
            try { if ( System.IO.Directory.Exists( Directory ) ) System.IO.Directory.Delete( Directory, true ); }
            catch ( IOException ) { }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        int port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        listener.Stop();
        return port;
    }

    private static Rig Start( int clients )
    {
        var rig = new Rig();
        var root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        rig.Directory = Path.Combine( root, "teamcreate-coretests", Guid.NewGuid().ToString( "N" ) );
        int port = 0;
        for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
        {
            try
            {
                port = FreePort();
                rig.JournalPath = Path.Combine( rig.Directory, $"journal-{attempt}.jsonl" );
                rig.Server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", rig.JournalPath );
            }
            catch ( SocketException ) { }
        }
        if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the drag tests." );
        rig.Server.AllowUnverifiedPeers = true;
        for ( int i = 0; i < clients; i++ )
        {
            var wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult();
            string peer = Guid.NewGuid().ToString();
            wire.Send( new Packet
            {
                Kind = "hello", Peer = peer, ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source, Name = "Dragger " + i,
                Token = "test-token", Project = "test-project", Baseline = "test-baseline", SteamId = "7656119800000000" + ( 1 + i ), SteamToken = "a-realistic-session-token",
                OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
            } );
            rig.Clients.Add( wire );
            rig.Seen.Add( new List<Packet>() );
            rig.Peers.Add( peer );
            int index = i;
            rig.PumpUntil( () => rig.Seen[index].Any( p => p.Kind == "welcome" ), "welcome" );
        }
        rig.Clients[0].Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState> { Cube( 1, "0,0,0" ) } } );
        rig.PumpUntil( () => rig.Seen[0].Any( p => p.Kind == "snapshot" ), "snapshot" );
        return rig;
    }

    private static Packet Valid( Rig rig, int from, double sentMs, int id = 1 )
        => TransientCodec.Build( Scene, rig.Peers[from], sentMs, new[] { Item( id, (float)( sentMs / 10 ) ) }, final: false );

    private static void ServerBehaviour( Action<bool, string> assert )
    {
        // ---- fan out to the others, never back to the sender; nothing journaled or revised ----
        using ( var rig = Start( 3 ) )
        {
            long journal = rig.JournalBytes();
            int revision = rig.Revision();
            rig.Clients[0].Send( Valid( rig, 0, 1000 ) );
            rig.Clients[0].Send( Valid( rig, 0, 1033 ) );
            rig.Settle( 0 );
            rig.Pump( 100 );
            assert( rig.Count( 1, "transient" ) == 2 && rig.Count( 2, "transient" ) == 2, $"drag_server_fans_a_transient_out_to_every_other_member: {rig.Count( 1, "transient" )}/{rig.Count( 2, "transient" )}" );
            assert( rig.Count( 0, "transient" ) == 0, "drag_server_never_echoes_a_transient_to_its_sender" );
            var relayed = rig.Seen[1].First( p => p.Kind == "transient" );
            assert( relayed.Peer == rig.Peers[0] && relayed.Scene == Scene && relayed.SentMs == 1000, "drag_server_relays_with_the_admitted_peer_id_scene_and_stamp_untouched" );
            assert( TransientCodec.Validate( relayed, out var items ) == null && items[0].Id == Id( 1 ), "drag_relayed_packet_is_still_a_valid_transient" );
            assert( rig.JournalBytes() == journal, "drag_server_never_journals_a_transient" );
            assert( rig.Revision() == revision, "drag_server_never_revises_the_scene_for_a_transient" );
            assert( !rig.Seen.Any( s => s.Any( p => p.Kind == "fatal" || p.Kind == "reject" ) ), "drag_server_transient_costs_nobody_their_session" );
        }

        // ---- the sender cannot spoof another member ----
        using ( var rig = Start( 2 ) )
        {
            var spoof = Valid( rig, 0, 1000 );
            spoof.Peer = rig.Peers[1];
            rig.Clients[0].Send( spoof );
            rig.Settle( 0 );
            assert( rig.Count( 1, "transient" ) == 1 && rig.Seen[1].First( p => p.Kind == "transient" ).Peer == rig.Peers[0] && rig.Count( 0, "transient" ) == 0, "drag_server_stamps_the_real_sender_not_the_claimed_one" );
        }

        // ---- hostile content is dropped silently ----
        using ( var rig = Start( 2 ) )
        {
            long journal = rig.JournalBytes();
            var nan = Valid( rig, 0, 1000 ); nan.Transient[0][1] = "NaN";
            var huge = Valid( rig, 0, 1001 ); huge.Transient[0][2] = 1e9;
            var badId = Valid( rig, 0, 1002 ); badId.Transient[0][0] = "zzz";
            var unknownScene = Valid( rig, 0, 1003 ); unknownScene.Scene = "scenes/never-subscribed.scene";
            var notScenes = Valid( rig, 0, 1004 ); notScenes.Scene = "../../etc/passwd";
            var overBatch = Valid( rig, 0, 1005 ); for ( int i = 2; i <= 40; i++ ) overBatch.Transient.Add( TransientCodec.Encode( Item( i, 1f ) ) );
            var noPayload = Valid( rig, 0, 1006 ); noPayload.Transient = null;
            foreach ( var p in new[] { nan, huge, badId, unknownScene, notScenes, overBatch, noPayload } ) rig.Clients[0].Send( p );
            rig.Settle( 0 );
            assert( rig.Count( 1, "transient" ) == 0, "drag_server_drops_nan_huge_bad_id_unknown_scene_oversize_batch_and_empty_payload" );
            assert( !rig.Seen.Any( s => s.Any( p => p.Kind == "fatal" || p.Kind == "reject" ) ), "drag_server_hostile_transients_are_dropped_without_a_fatal" );
            assert( rig.JournalBytes() == journal, "drag_server_hostile_transients_never_reach_the_journal" );
            rig.Clients[0].Send( Valid( rig, 0, 2000 ) );
            rig.Settle( 0 );
            assert( rig.Count( 1, "transient" ) == 1, "drag_server_a_valid_transient_still_flows_after_the_hostile_ones" );
        }

        // ---- the rate limit on the wire ----
        using ( var rig = Start( 2 ) )
        {
            for ( int i = 0; i < 100; i++ ) rig.Clients[0].Send( Valid( rig, 0, 1000 + i ) );
            rig.Settle( 0 );
            int received = rig.Count( 1, "transient" );
            assert( received >= 30 && received <= TransientLimits.MaxPacketsPerSecond + 5, $"drag_server_rate_limits_a_member_to_about_40_packets_a_second: {received} of 100 arrived" );
            assert( !rig.Seen.Any( s => s.Any( p => p.Kind == "fatal" ) ), "drag_server_excess_transients_are_dropped_silently" );
        }

        // ---- a transient and a real edit coexist: the edit is journaled and accepted, the transient is not ----
        using ( var rig = Start( 2 ) )
        {
            long journal = rig.JournalBytes();
            rig.Clients[0].Send( Valid( rig, 0, 1000 ) );
            rig.Clients[0].Send( new Packet { Kind = "edit", Scene = Scene, Final = true, Changes = new List<Change> { new( Id( 1 ), Cube( 1, "0,0,0" ), Cube( 1, "9,0,0" ) ) } } );
            rig.PumpUntil( () => rig.Count( 1, "accepted" ) == 1, "accepted" );
            assert( rig.Count( 1, "transient" ) == 1 && rig.JournalBytes() > journal, "drag_server_the_real_edit_is_journaled_and_the_transient_is_not" );
            int order = rig.Seen[1].FindIndex( p => p.Kind == "transient" ) - rig.Seen[1].FindIndex( p => p.Kind == "accepted" );
            assert( order < 0, "drag_server_keeps_wire_order_between_a_transient_and_the_edit_that_follows_it" );
        }

        // ---- the host advertises the channel ----
        using ( var rig = Start( 1 ) )
        {
            var welcome = rig.Seen[0].First( p => p.Kind == "welcome" );
            assert( welcome.Features != null && welcome.Features.Contains( TransientLimits.Feature ), "drag_welcome_advertises_the_transient_channel_so_an_older_host_is_never_sent_one" );
        }
    }

    // ---- Bandwidth (T4.5) ---------------------------------------------------------------------------------------------------------------

    private static void Bandwidth( Action<bool, string> assert )
    {
        // The session holds four participants (the host and three guests); the fake clients are the three guests, so the host's own editor, which
        // would receive the same packets in-process, is not a socket here. The four-peer figure below is computed from the measured packet size.
        using var rig = Start( 3 );
        const int perSender = 30;
        for ( int i = 0; i < perSender; i++ )
            for ( int sender = 0; sender < 3; sender++ )
                rig.Clients[sender].Send( TransientCodec.Build( Scene, rig.Peers[sender], 5000 + i * 33.3, new[] { new TransientItem( Id( 1 + sender ), new Pose( 123.456f + i, -45.678f, 7.5f, 0.1234f, -0.5f, 0.25f, 0.8f ) ) }, final: false ) );
        for ( int sender = 0; sender < 3; sender++ ) rig.Settle( sender );
        rig.Pump( 200 );
        long relayedBytes = 0;
        int relayedPackets = 0;
        for ( int client = 0; client < 3; client++ )
            foreach ( var p in rig.Seen[client].Where( p => p.Kind == "transient" ) )
            {
                relayedBytes += JsonSerializer.SerializeToUtf8Bytes( p ).Length;
                relayedPackets++;
            }
        double measuredUpload = relayedBytes / ( perSender / 30.0 );
        var sample = rig.Seen[1].First( p => p.Kind == "transient" );
        int oneObjectPacket = JsonSerializer.SerializeToUtf8Bytes( sample ).Length;
        int entry = TransientCodec.EntryBytes( new TransientItem( Id( 1 ), new Pose( 123.456f, -45.678f, 7.5f, 0.1234f, -0.5f, 0.25f, 0.8f ) ) );
        // Four peers dragging at 30 Hz, each packet going to the three others.
        double fourPeers = 4.0 * 30 * 3 * oneObjectPacket;
        Console.WriteLine( $"INFO: drag bandwidth: one-object entry {entry} bytes, one-object packet as relayed {oneObjectPacket} bytes; three guests dragging at 30 Hz through the real host: {relayedPackets} packets relayed, {measuredUpload / 1000.0:F1} KB/s; four peers at 30 Hz fanned out to three others each: {fourPeers / 1000.0:F1} KB/s" );
        assert( relayedPackets == 3 * perSender * 2, $"drag_bandwidth_every_packet_reached_the_other_members: {relayedPackets}" );
        assert( entry <= 160, $"drag_bandwidth_one_object_entry_is_at_most_160_bytes: {entry}" );
        assert( fourPeers <= 100_000, $"drag_bandwidth_four_peers_at_30_hz_cost_at_most_100_kb_per_second: {fourPeers:F0} B/s" );

        // Idle: nothing is sent when nothing is dragged.
        var idle = new TransientCadence();
        int sent = 0;
        for ( int t = 0; t < 3000; t += 10 ) sent += idle.TakeDue( t ).Count;
        assert( sent == 0, "drag_bandwidth_idle_sends_nothing" );
    }
}
