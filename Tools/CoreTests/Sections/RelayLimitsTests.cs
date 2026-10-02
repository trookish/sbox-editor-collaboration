using System.Security.Cryptography;
using TeamCreate;

// The caps that protect a relay the host runs on their own computer and exposes through a tunnel: anyone who learns the random address
// can open a socket, so how many may join, how big a frame may be, how fast a room may talk, how long an unproven socket may linger and
// which URLs the listener serves are all fixed here and exercised without a socket.
public static class RelayLimitsTests
{
    public static void Run( Action<bool, string> assert )
    {
        Constants( assert );
        Members( assert );
        Frames( assert );
        Proof( assert );
        Window( assert );
        Paths( assert );
    }

    private static void Constants( Action<bool, string> assert )
    {
        assert( RelayLimits.MaxMembersPerRoom == 4, "relaylimits_member_cap_is_4" );
        assert( RelayLimits.MaxFrameBytes == 65536, "relaylimits_frame_cap_is_64_kib" );
        assert( RelayLimits.MaxMessagesPerSecond == 200, "relaylimits_rate_cap_is_200_per_second" );
        assert( RelayLimits.ProofDeadlineSeconds == 10.0, "relaylimits_proof_deadline_is_10_seconds" );

        // Everything the library itself ever sends through a relay must fit under the caps, or the cap would refuse the host's own friends.
        assert( RelayProtocol.MaxMessageBytes <= RelayLimits.MaxFrameBytes, "relaylimits_largest_library_message_fits_under_the_frame_cap" );
        assert( RelayProtocol.MaxControlChars * 4 <= RelayLimits.MaxFrameBytes, "relaylimits_largest_control_message_fits_under_the_frame_cap_even_as_utf8" );
    }

    private static void Members( Action<bool, string> assert )
    {
        assert( RelayLimits.AdmitMember( 0 ) == null, "relaylimits_member_0_admitted" );
        assert( RelayLimits.AdmitMember( 3 ) == null, "relaylimits_member_at_3_admits_the_fourth" );
        assert( RelayLimits.AdmitMember( 4 ) == "The session already has the maximum of 4 members.", "relaylimits_member_at_4_refused_with_message" );
        assert( RelayLimits.AdmitMember( 5 ) != null, "relaylimits_member_at_5_refused" );
        assert( RelayLimits.AdmitMember( int.MaxValue ) != null, "relaylimits_member_at_int_max_refused" );

        // A negative count means the caller's bookkeeping is broken. Admitting on it would let the room grow past the cap by that error, so it refuses.
        string negative = RelayLimits.AdmitMember( -1 );
        assert( negative == "The member count was not valid, so no further member was admitted.", "relaylimits_negative_member_count_refused_with_message" );
        assert( RelayLimits.AdmitMember( int.MinValue ) == negative, "relaylimits_int_min_member_count_refused" );
    }

    private static void Frames( Action<bool, string> assert )
    {
        assert( RelayLimits.AdmitFrame( 1 ) == null, "relaylimits_frame_1_byte_allowed" );
        assert( RelayLimits.AdmitFrame( 16389 ) == null, "relaylimits_frame_of_largest_library_message_allowed" );
        assert( RelayLimits.AdmitFrame( 65535 ) == null, "relaylimits_frame_65535_allowed" );
        assert( RelayLimits.AdmitFrame( 65536 ) == null, "relaylimits_frame_65536_allowed" );
        assert( RelayLimits.AdmitFrame( 65537 ) == "The frame of 65537 bytes is larger than the maximum of 65536 bytes.", "relaylimits_frame_65537_refused_with_message" );
        assert( RelayLimits.AdmitFrame( int.MaxValue ) != null, "relaylimits_frame_int_max_refused" );
        assert( RelayLimits.AdmitFrame( 0 ) == "The frame was empty or its size was not valid.", "relaylimits_frame_0_refused_with_message" );
        assert( RelayLimits.AdmitFrame( -1 ) == "The frame was empty or its size was not valid.", "relaylimits_frame_negative_refused" );
        assert( RelayLimits.AdmitFrame( int.MinValue ) != null, "relaylimits_frame_int_min_refused" );
    }

    private static void Proof( Action<bool, string> assert )
    {
        // The deadline is reached, not exceeded: it matches the relay worker, which closes a socket once 10 s have passed without a hello.
        assert( !RelayLimits.ProofExpired( 100.0, 100.0, false ), "relaylimits_proof_not_expired_at_connect_time" );
        assert( !RelayLimits.ProofExpired( 100.0, 105.0, false ), "relaylimits_proof_not_expired_before_deadline" );
        assert( !RelayLimits.ProofExpired( 100.0, 109.999, false ), "relaylimits_proof_not_expired_just_before_deadline" );
        assert( RelayLimits.ProofExpired( 100.0, 110.0, false ), "relaylimits_proof_expired_at_deadline" );
        assert( RelayLimits.ProofExpired( 100.0, 110.001, false ), "relaylimits_proof_expired_just_after_deadline" );
        assert( RelayLimits.ProofExpired( 100.0, 1000000.0, false ), "relaylimits_proof_expired_long_after_deadline" );

        assert( !RelayLimits.ProofExpired( 100.0, 110.0, true ), "relaylimits_proven_socket_does_not_expire_at_deadline" );
        assert( !RelayLimits.ProofExpired( 100.0, 1000000.0, true ), "relaylimits_proven_socket_never_expires" );

        assert( !RelayLimits.ProofExpired( 100.0, 50.0, false ), "relaylimits_clock_stepping_back_does_not_expire_a_socket" );

        // A clock reading that is not a number cannot show the socket is still within its time, so it counts as expired.
        assert( RelayLimits.ProofExpired( double.NaN, 100.0, false ), "relaylimits_nan_connect_time_expires_an_unproven_socket" );
        assert( RelayLimits.ProofExpired( 100.0, double.NaN, false ), "relaylimits_nan_now_expires_an_unproven_socket" );
        assert( RelayLimits.ProofExpired( 100.0, double.PositiveInfinity, false ), "relaylimits_infinite_now_expires_an_unproven_socket" );
        assert( RelayLimits.ProofExpired( double.PositiveInfinity, 100.0, false ), "relaylimits_infinite_connect_time_expires_an_unproven_socket" );
        assert( !RelayLimits.ProofExpired( double.NaN, double.NaN, true ), "relaylimits_proven_socket_ignores_a_bad_clock" );
    }

    private static void Window( Action<bool, string> assert )
    {
        // The advertised cap: 200 in a second, the 201st refused.
        var room = new RateWindow( RelayLimits.MaxMessagesPerSecond );
        bool allAllowed = true;
        for ( int i = 0; i < 200; i++ )
            allAllowed &= room.TryConsume( 10.0 + i * 0.004 );
        assert( allAllowed, "relaylimits_window_200_allowed_within_a_second" );
        assert( !room.TryConsume( 10.9 ), "relaylimits_window_201st_within_the_second_refused" );
        assert( !room.TryConsume( 10.95 ), "relaylimits_window_stays_refused_while_the_window_is_full" );

        // Sliding, not bucketed: the first event ages out at 11.0, so exactly one slot opens then, not 200.
        assert( room.TryConsume( 11.0 ), "relaylimits_window_allowed_again_after_the_window_slides" );
        assert( !room.TryConsume( 11.001 ), "relaylimits_window_only_one_slot_opens_as_one_event_ages_out" );
        assert( room.TryConsume( 11.005 ), "relaylimits_window_next_slot_opens_as_the_next_event_ages_out" );

        // A burst straddling a bucket boundary is the case a fixed one-second bucket gets wrong: 400 events in a quarter of a second.
        // The times are exact binary fractions so that "exactly one second later" is exact in floating point too.
        var burst = new RateWindow( 200 );
        bool firstBatch = true;
        for ( int i = 0; i < 200; i++ )
            firstBatch &= burst.TryConsume( 0.875 );
        assert( firstBatch, "relaylimits_window_burst_first_200_allowed" );
        assert( !burst.TryConsume( 1.125 ), "relaylimits_window_burst_across_a_whole_second_boundary_refused" );
        assert( !burst.TryConsume( 1.8125 ), "relaylimits_window_burst_still_refused_just_inside_the_second" );
        assert( burst.TryConsume( 1.875 ), "relaylimits_window_burst_allowed_exactly_one_second_later" );
        bool secondBatch = true;
        for ( int i = 0; i < 199; i++ )
            secondBatch &= burst.TryConsume( 1.875 );
        assert( secondBatch, "relaylimits_window_burst_a_full_second_later_allows_a_full_batch_again" );
        assert( !burst.TryConsume( 1.875 ), "relaylimits_window_burst_refused_again_once_full" );

        // The span is half open: with 4 per second at an exact quarter-second spacing nothing is ever refused, and a fifth inside one second is.
        var exact = new RateWindow( 4 );
        bool steadyExact = true;
        for ( int i = 0; i < 400; i++ )
            steadyExact &= exact.TryConsume( i * 0.25 );
        assert( steadyExact, "relaylimits_window_exactly_limit_per_second_at_even_spacing_never_refused" );
        var inside = new RateWindow( 4 );
        bool four = true;
        for ( int i = 0; i < 4; i++ )
            four &= inside.TryConsume( i * 0.25 );
        assert( four && !inside.TryConsume( 0.99 ) && inside.TryConsume( 1.0 ), "relaylimits_window_fifth_event_inside_one_second_refused_and_allowed_at_exactly_one_second" );

        // A steady 199 per second for ten simulated seconds stays under the 200 cap in every window.
        var steady = new RateWindow( 200 );
        int refused = 0;
        for ( int i = 0; i < 1990; i++ )
        {
            if ( !steady.TryConsume( i / 199.0 ) )
                refused++;
        }
        assert( refused == 0, "relaylimits_window_steady_199_per_second_never_refused_over_10_seconds" );

        // A steady 400 per second against a cap of 200 is held to the cap: about 200 allowed per second, and never more than 200 in any one-second span.
        var flood = new RateWindow( 200 );
        var allowedAt = new List<double>();
        for ( int i = 0; i < 4000; i++ )
        {
            if ( flood.TryConsume( i / 400.0 ) )
                allowedAt.Add( i / 400.0 );
        }
        assert( allowedAt.Count >= 1990 && allowedAt.Count <= 2010, "relaylimits_window_flood_of_400_per_second_is_held_to_about_200_per_second" );
        int busiest = 0;
        for ( int start = 0; start < allowedAt.Count; start++ )
        {
            int inSpan = 0;
            // A nanosecond of slack keeps a span that ends exactly on the next event from being counted twice by rounding.
            while ( start + inSpan < allowedAt.Count && allowedAt[start + inSpan] < allowedAt[start] + 1.0 - 1e-9 )
                inSpan++;
            busiest = Math.Max( busiest, inSpan );
        }
        assert( busiest <= 200, "relaylimits_window_flood_never_exceeds_200_in_any_one_second_span" );

        // Time going backwards is treated as the latest time seen: it neither frees slots nor corrupts the window.
        var back = new RateWindow( 3 );
        assert( back.TryConsume( 10.0 ) && back.TryConsume( 10.1 ), "relaylimits_window_backwards_setup" );
        assert( back.TryConsume( 2.0 ), "relaylimits_window_backwards_timestamp_still_counts_as_one_event" );
        assert( !back.TryConsume( 1.0 ), "relaylimits_window_backwards_timestamp_cannot_free_a_slot" );
        assert( !back.TryConsume( 10.2 ), "relaylimits_window_full_after_a_backwards_timestamp" );
        assert( back.TryConsume( 11.05 ), "relaylimits_window_recovers_after_the_oldest_event_ages_out_following_a_backwards_timestamp" );
        assert( !back.TryConsume( 11.06 ), "relaylimits_window_is_full_again_after_recovering" );

        var wide = new RateWindow( 1 );
        assert( wide.TryConsume( 100.0 ), "relaylimits_window_backwards_limit_1_first" );
        assert( !wide.TryConsume( 0.0 ), "relaylimits_window_backwards_limit_1_cannot_use_the_past" );
        assert( !wide.TryConsume( 100.5 ), "relaylimits_window_backwards_limit_1_still_refused_after_the_past_call" );
        assert( wide.TryConsume( 101.0 ), "relaylimits_window_backwards_limit_1_allowed_one_second_after_the_only_event" );

        // A clock reading that is not finite is refused and changes nothing, so it can neither fill the window nor lock it open.
        var odd = new RateWindow( 2 );
        assert( !odd.TryConsume( double.NaN ), "relaylimits_window_nan_refused" );
        assert( !odd.TryConsume( double.PositiveInfinity ), "relaylimits_window_infinity_refused" );
        assert( !odd.TryConsume( double.NegativeInfinity ), "relaylimits_window_negative_infinity_refused" );
        assert( odd.TryConsume( 5.0 ) && odd.TryConsume( 5.1 ) && !odd.TryConsume( 5.2 ), "relaylimits_window_non_finite_readings_left_the_window_untouched" );

        assert( Throws<ArgumentOutOfRangeException>( () => new RateWindow( 0 ) ), "relaylimits_window_limit_0_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => new RateWindow( -1 ) ), "relaylimits_window_negative_limit_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => new RateWindow( int.MaxValue ) ), "relaylimits_window_absurd_limit_throws" );
        assert( Throws<ArgumentOutOfRangeException>( () => new RateWindow( RateWindow.MaxLimitPerSecond + 1 ) ), "relaylimits_window_limit_above_the_ceiling_throws" );
        assert( new RateWindow( RateWindow.MaxLimitPerSecond ).TryConsume( 0.0 ), "relaylimits_window_limit_at_the_ceiling_is_usable" );
        var one = new RateWindow( 1 );
        assert( one.TryConsume( 0.0 ) && !one.TryConsume( 0.5 ) && one.TryConsume( 1.0 ), "relaylimits_window_limit_1_allows_one_per_second" );
    }

    private static void Paths( Action<bool, string> assert )
    {
        string hash = Convert.ToHexString( SHA256.HashData( new byte[] { 1, 2, 3 } ) ).ToLowerInvariant();
        assert( hash.Length == 64, "relaylimits_path_fixture_hash_is_64_characters" );

        assert( RelayLimits.IsServedPath( "/relay" ), "relaylimits_path_relay_served" );
        assert( RelayLimits.IsServedPath( RelayProtocol.DefaultPath ), "relaylimits_path_relay_matches_the_protocols_default_path" );
        assert( RelayLimits.IsServedPath( "/health" ), "relaylimits_path_health_served" );
        assert( RelayLimits.IsServedPath( "/files/" + hash ), "relaylimits_path_files_with_64_lower_hex_served" );
        assert( RelayLimits.IsServedPath( "/files/" + new string( '0', 64 ) ), "relaylimits_path_files_all_zero_hash_served" );
        assert( RelayLimits.IsServedPath( "/files/" + new string( 'f', 64 ) ), "relaylimits_path_files_all_f_hash_served" );
        assert( RelayLimits.IsServedPath( "/files/" + new string( '9', 64 ) ), "relaylimits_path_files_all_nine_hash_served" );

        string[] refused =
        {
            null, "", "/", " ", "relay", "/relay/", "/relay?x", "/relay?room=abc", "/relay#x", "/relayx", "/relay/x", "/relay ", " /relay",
            "/relay\0", "/relay\n", "/relay%00", "/relay%2f", "//relay", "\\relay", "/\\relay",
            "/Relay", "/RELAY", "/Health", "/HEALTH", "/health/", "/healthz", "/health?x=1", "/health ",
            "/../relay", "/./relay", "/relay/..", "/relay/.", "/health/..", "/..", "/.", "/files/../relay", "/files/../../etc/passwd",
            "/files", "/files/", "/Files/" + "0000000000000000000000000000000000000000000000000000000000000000",
            "/files//" + "0000000000000000000000000000000000000000000000000000000000000000",
            "files/" + "0000000000000000000000000000000000000000000000000000000000000000",
            "/files/" + "000000000000000000000000000000000000000000000000000000000000000",
            "/files/" + "00000000000000000000000000000000000000000000000000000000000000000",
            "/files/" + "000000000000000000000000000000000000000000000000000000000000000g",
            "/files/" + "g000000000000000000000000000000000000000000000000000000000000000",
            "/files/" + "000000000000000000000000000000000000000000000000000000000000000 ",
            "/files/" + "000000000000000000000000000000000000000000000000000000000000000/",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000/",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000?x=1",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000#x",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000.bin",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000\n",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000/..",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000/../relay",
            "/files/..%2f" + "00000000000000000000000000000000000000000000000000000000000",
            "/files/" + "..000000000000000000000000000000000000000000000000000000000000",
            "/files/" + "0000000000000000000000000000000000000000000000000000000000000000" + "0000000000000000000000000000000000000000000000000000000000000000",
            "/files/" + new string( '\u0660', 64 ),
            "/files/" + new string( '\uFF10', 64 ),
        };
        for ( int i = 0; i < refused.Length; i++ )
        {
            string path = refused[i];
            assert( !RelayLimits.IsServedPath( path ), "relaylimits_path_refused_" + i + "_" + Describe( path ) );
        }

        // Upper-case hex is refused: every hash in this library is written in lower case, so an upper-case request names nothing the host holds.
        assert( !RelayLimits.IsServedPath( "/files/" + hash.ToUpperInvariant() ), "relaylimits_path_files_upper_case_hash_refused" );
        assert( !RelayLimits.IsServedPath( "/files/" + hash.Substring( 0, 63 ) + "A" ), "relaylimits_path_files_mixed_case_hash_refused" );
    }

    // A readable suffix so a failure names the offending path without putting raw control characters in the log.
    private static string Describe( string path )
    {
        if ( path == null )
            return "null";
        var text = new System.Text.StringBuilder();
        foreach ( char c in path.Length > 24 ? path.Substring( 0, 24 ) : path )
            text.Append( c >= ' ' && c < '\u007f' ? c : '_' );
        return text.Length == 0 ? "empty" : text.ToString();
    }

    private static bool Throws<T>( Action action ) where T : Exception
    {
        try
        {
            action();
        }
        catch ( T )
        {
            return true;
        }
        return false;
    }
}
