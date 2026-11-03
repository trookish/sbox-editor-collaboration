using System.Net.Sockets;
using TeamCreate;

// A guest's session heals itself: automatic recovery is on unless someone turns it off, it keeps trying for as long as the editor is
// open (backing off to every thirty seconds), and only a failure that retrying cannot fix stops it.
static class AutoRecoveryDefaultsTests
{
    private static AutoRecoveryInput Input( int attemptsUsed ) => new(
        AutoRecoverEnabled: true, ErrorRecorded: true, FailureClass: SessionFailureClass.Transient, TerminalTeardown: false, HostedSession: false,
        UnsavedChanges: false, ReconnectAccepted: true, AttemptInFlight: false, AttemptsUsed: attemptsUsed, Now: DateTime.UnixEpoch, NextAttemptAt: DateTime.UnixEpoch );

    public static void Run( Action<bool, string> assert )
    {
        assert( AutoRecoveryRules.EnabledByDefault, "recover_automatic_recovery_is_on_by_default" );

        // The schedule: quick at first, for a blip, then settling at every thirty seconds, for a host that is restarting.
        int[] expected = { 1, 2, 4, 8, 15, 30, 30, 30 };
        bool schedule = true;
        for ( int i = 0; i < expected.Length; i++ )
        {
            schedule &= AutoRecoveryRules.Backoff( i ) == TimeSpan.FromSeconds( expected[i] );
        }
        assert( schedule, "recover_attempts_back_off_at_1_2_4_8_15_then_every_30_seconds" );
        assert( AutoRecoveryRules.Backoff( -5 ) == TimeSpan.FromSeconds( 1 ) && AutoRecoveryRules.Backoff( int.MaxValue ) == TimeSpan.FromSeconds( 30 ),
            "recover_the_backoff_is_defined_for_any_attempt_count" );
        TimeSpan firstMinute = TimeSpan.Zero;
        int attemptsInFirstMinute = 0;
        while ( firstMinute < TimeSpan.FromSeconds( 60 ) )
        {
            firstMinute += AutoRecoveryRules.Backoff( attemptsInFirstMinute++ );
        }
        assert( attemptsInFirstMinute >= 5, "recover_a_drop_of_up_to_a_minute_is_retried_at_least_five_times: " + attemptsInFirstMinute );

        // No attempt budget: the session keeps trying while the editor is open.
        assert( AutoRecoveryRules.Eligible( Input( 3 ) ) && AutoRecoveryRules.Eligible( Input( 50 ) ) && AutoRecoveryRules.Eligible( Input( 100_000 ) ),
            "recover_there_is_no_attempt_budget_after_which_recovery_stops" );

        // What is worth retrying.
        assert( AutoRecoveryRules.Classify( new IOException( "Disconnected: Transport failed: connection reset" ) ) == SessionFailureClass.Transient,
            "recover_a_dropped_connection_is_retried" );
        assert( AutoRecoveryRules.Classify( new IOException( "Disconnected: Transport failed: " + Wire.DecryptionFailure ) ) == SessionFailureClass.Terminal,
            "recover_a_key_mismatch_or_tampered_frame_is_not_retried" );
        assert( AutoRecoveryRules.Classify( new IOException( "Disconnected: " + Wire.PreambleFailure ) ) == SessionFailureClass.Terminal,
            "recover_a_peer_that_does_not_speak_the_protocol_is_not_retried" );
        assert( AutoRecoveryRules.Classify( new AggregateException( new IOException( Wire.DecryptionFailure ) ) ) == SessionFailureClass.Terminal,
            "recover_a_wrapped_key_mismatch_is_not_retried" );
        assert( AutoRecoveryRules.Classify( new SocketException() ) == SessionFailureClass.Transient, "recover_a_socket_error_is_retried" );
        foreach ( string code in new[] { "no-host", "full", "limit" } )
        {
            assert( AutoRecoveryRules.Classify( new RelayRefusedException( code, RelayProtocol.UserMessage( code, null ) ) ) == SessionFailureClass.Transient,
                "recover_a_relay_refusal_that_can_pass_is_retried: " + code );
        }
        foreach ( string code in new[] { "bad-key", "bad-proof", "room-taken", "version", "bad-hello" } )
        {
            assert( AutoRecoveryRules.Classify( new RelayRefusedException( code, RelayProtocol.UserMessage( code, null ) ) ) == SessionFailureClass.Terminal,
                "recover_a_relay_refusal_that_retrying_cannot_fix_is_not_retried: " + code );
        }

        // What the host said when it refused.
        assert( AutoRecoveryRules.ClassifyRefusal( SteamIdentityAdmission.DuplicateAccountError ) == SessionFailureClass.Transient,
            "recover_the_host_still_holding_this_accounts_old_connection_is_retried" );
        assert( AutoRecoveryRules.ClassifyRefusal( SessionServer.VerificationBusyError ) == SessionFailureClass.Transient,
            "recover_a_host_busy_verifying_others_is_retried" );
        assert( AutoRecoveryRules.ClassifyRefusal( "Host processing failed: bad edit" ) == SessionFailureClass.Terminal
            && AutoRecoveryRules.ClassifyRefusal( "Protocol mismatch" ) == SessionFailureClass.Terminal
            && AutoRecoveryRules.ClassifyRefusal( null ) == SessionFailureClass.Terminal,
            "recover_any_other_refusal_by_the_host_is_not_retried" );
    }
}
