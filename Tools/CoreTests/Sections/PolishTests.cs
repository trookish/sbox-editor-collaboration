using System.Net;
using System.Net.Sockets;
using System.Text;
using TeamCreate;

/// <summary>
/// Polish: the package-reference notice, the four-participant cap, the coalesced undo reset, the classification of user-action failures,
/// the longer tunnel probe window and the relay wording. Pure decisions are tested directly; the cap and the package field are also driven
/// through a real SessionServer on loopback with fake guests.
/// </summary>
static class PolishTests
{
    private static readonly DateTime FixedClock = new( 2026, 10, 3, 12, 34, 56, DateTimeKind.Utc );

    public static void Run( Action<bool, string> assert )
    {
        string root = Path.Combine( Path.GetTempPath(), "polish-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( root );
        try
        {
            PackageDiff( assert );
            PackageWire( assert, root );
            MemberCap( assert, root );
            UndoReset( assert );
            ActionFailure( assert );
            ProbeWindow( assert );
            RelayWording( assert );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
        }
    }

    // ---------------------------------------------------------------- 1. package-reference notice

    private static string Sbproj( string packages, string editor = "null" ) =>
        "{ \"Title\": \"T\", \"Type\": \"game\", \"PackageReferences\": " + packages + ", \"EditorReferences\": " + editor + ", \"Metadata\": { \"MapList\": [ \"facepunch.flatgrass\" ] } }";

    private static void PackageDiff( Action<bool, string> assert )
    {
        string host = Sbproj( "[ \"facepunch.flatgrass\", \"cyn.zebra\", \"cyn.alpha\", \"cyn.alpha\" ]", "[ \"cyn.editortool\" ]" );
        string guest = Sbproj( "[ \"facepunch.flatgrass\" ]" );
        assert( PackageReferenceDiff.Missing( host, guest ).SequenceEqual( new[] { "cyn.alpha", "cyn.editortool", "cyn.zebra" } ),
            "polish_package_missing_is_sorted_ordinal_deduplicated_and_includes_editor_references" );
        assert( PackageReferenceDiff.Missing( host, host ).Count == 0, "polish_package_identical_lists_report_nothing" );
        assert( PackageReferenceDiff.Missing( guest, host ).Count == 0, "polish_package_a_guest_with_more_packages_is_not_reported" );
        assert( PackageReferenceDiff.Missing( host, Sbproj( "[ \"CYN.Zebra\", \"cyn.alpha\", \"cyn.editortool\", \"Facepunch.Flatgrass\" ]" ) ).Count == 0,
            "polish_package_membership_ignores_letter_case_because_idents_are_case_insensitive" );
        assert( PackageReferenceDiff.Missing( host, "" ).Count == 4 && PackageReferenceDiff.Missing( host, null ).Count == 4, "polish_package_a_guest_without_a_project_file_misses_all" );

        // The project's real settings file: the metadata map list is not a package reference, and the empty reference list yields nothing.
        string libraryProject = FindUp( "Libraries/collaboration/collaboration.sbproj" );
        string real = File.ReadAllText( libraryProject );
        assert( PackageReferenceDiff.Idents( real ).Count == 0 && PackageReferenceDiff.Missing( real, real ).Count == 0, "polish_package_real_sbproj_has_no_packages" );
        assert( PackageReferenceDiff.Missing( real.Replace( "\"PackageReferences\": []", "\"PackageReferences\": [ \"cyn.real\" ]" ), real ).SequenceEqual( new[] { "cyn.real" } ),
            "polish_package_real_sbproj_with_one_package_reports_it" );
        assert( PackageReferenceDiff.Idents( Sbproj( "[]" ) ).Count == 0, "polish_package_map_list_in_metadata_is_not_a_package" );

        // Hostile and malformed input never throws and never yields an ident that fails the pattern.
        string hostile = Sbproj( "[ \"ok.one\", \"has space\", \"semi;colon\", \"new\\nline\", \"<script>\", \"../up\", \"\", 5, null, { \"x\": 1 }, [ \"nested.one\" ], \"" + new string( 'a', 101 ) + "\", \"" + new string( 'b', 100 ) + "\", \"ünï.code\", \"ok.two#12\" ]" );
        var hostileIdents = PackageReferenceDiff.Idents( hostile );
        assert( hostileIdents.Contains( "ok.one" ) && hostileIdents.Contains( new string( 'b', 100 ) ) && hostileIdents.Count == 2,
            "polish_package_hostile_idents_are_dropped_by_the_pattern_and_the_length_limit: " + string.Join( "|", hostileIdents ) );
        string[] malformed = { null, "", "   ", "{", "[", "not json", "{\"PackageReferences\": \"cyn.x\"}", "{\"PackageReferences\": {\"a\": 1}}", "[1,2,3]", "null", "{\"PackageReferences\": [", new string( '[', 5000 ) };
        bool tolerant = true;
        foreach ( string text in malformed )
        {
            try { tolerant &= PackageReferenceDiff.Missing( text, guest ).Count == 0 && PackageReferenceDiff.Missing( host, text ).Count == 4; }
            catch ( Exception ) { tolerant = false; }
        }
        assert( tolerant, "polish_package_malformed_json_yields_nothing_and_never_throws" );
        string many = Sbproj( "[ " + string.Join( ", ", Enumerable.Range( 0, 200 ).Select( i => "\"cyn.p" + i.ToString( "D3" ) + "\"" ) ) + " ]" );
        assert( PackageReferenceDiff.Idents( many ).Count == PackageReferenceDiff.MaxIdents && PackageReferenceDiff.Missing( many, guest ).Count == PackageReferenceDiff.MaxIdents && PackageReferenceDiff.MaxIdents == 50,
            "polish_package_result_is_bounded_to_fifty" );
        assert( PackageReferenceDiff.Idents( "{\"PackageReferences\": [\"a\"]," + new string( ' ', 2 * 1024 * 1024 ) + "}" ).Count == 0, "polish_package_oversized_text_is_ignored" );

        // The notice: exactly the specified sentence for one package, one notice naming at most five for several.
        assert( PackageReferenceDiff.Notice( new[] { "cyn.alpha" } ) == "The host's project uses the package \"cyn.alpha\" which your project does not list. Add it in the Library Manager or through Git; scenes may show it as missing until then.",
            "polish_package_notice_for_one_package_is_the_specified_sentence" );
        string several = PackageReferenceDiff.Notice( Enumerable.Range( 1, 7 ).Select( i => "cyn.p" + i ).ToList() );
        assert( several.Contains( "\"cyn.p1\"" ) && several.Contains( "\"cyn.p5\"" ) && !several.Contains( "\"cyn.p6\"" ) && several.Contains( "2 more" ) && several.Contains( "which your project does not list" ),
            "polish_package_notice_names_at_most_five_and_counts_the_rest: " + several );
        assert( PackageReferenceDiff.Notice( Array.Empty<string>() ) == null && PackageReferenceDiff.Notice( null ) == null, "polish_package_no_missing_package_no_notice" );
    }

    private static string FindUp( string relative )
    {
        for ( var dir = new DirectoryInfo( AppContext.BaseDirectory ); dir != null; dir = dir.Parent )
        {
            string candidate = Path.Combine( dir.FullName, relative.Replace( '/', Path.DirectorySeparatorChar ) );
            if ( File.Exists( candidate ) ) return candidate;
        }
        throw new FileNotFoundException( relative );
    }

    // The host answers files-index with its own package list on the final page; the guest turns the difference into one notice.
    private static void PackageWire( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "pkg", "host" );
        string guest = Path.Combine( root, "pkg", "guest" );
        Write( host, "Assets/a.png", "a" );
        Write( host, "host.sbproj", Sbproj( "[ \"cyn.alpha\", \"cyn.beta\" ]" ) );
        Write( guest, "Assets/a.png", "a" );
        Write( guest, "guest.sbproj", Sbproj( "[ \"cyn.beta\" ]" ) );
        string hostBefore = File.ReadAllText( Path.Combine( host, "host.sbproj" ) );

        using var rig = Rig.Start( Path.Combine( root, "pkg" ), host );
        var member = rig.Join( "Guest", guest, withClient: true );
        rig.PumpUntil( () => member.Client.JoinReady );
        var notices = member.Notices();
        string expected = PackageReferenceDiff.Notice( new[] { "cyn.alpha" } );
        assert( notices.Count( x => x == expected ) == 1 && notices.Count( x => x.Contains( "package" ) ) == 1, "polish_package_guest_receives_exactly_one_notice_naming_the_missing_package: " + string.Join( " | ", notices ) );
        assert( File.ReadAllText( Path.Combine( host, "host.sbproj" ) ) == hostBefore && !File.Exists( Path.Combine( guest, "host.sbproj" ) ), "polish_package_the_project_file_is_never_transferred" );
        var indexPackets = member.Seen.Count( x => x.Kind == "files-index" );
        assert( indexPackets == 0, "polish_package_index_reply_is_consumed_by_the_client" );

        // A guest whose project already lists everything is told nothing.
        string same = Path.Combine( root, "pkg", "same" );
        Write( same, "Assets/a.png", "a" );
        Write( same, "same.sbproj", Sbproj( "[ \"cyn.alpha\" ]", "[ \"cyn.beta\" ]" ) );
        var quiet = rig.Join( "Quiet", same, withClient: true );
        rig.PumpUntil( () => quiet.Client.JoinReady );
        assert( !quiet.Notices().Any( x => x.Contains( "package" ) ), "polish_package_nothing_is_missing_so_no_notice" );
    }

    // ---------------------------------------------------------------- 2. four participants

    private static void MemberCap( Action<bool, string> assert, string root )
    {
        string refusal = "This session already has 4 participants.";
        assert( SessionLimits.MaxParticipants == 4, "polish_cap_is_four_participants" );
        assert( SessionLimits.AdmitGuest( 0 ) == null && SessionLimits.AdmitGuest( 2 ) == null, "polish_cap_the_host_and_up_to_three_guests_fit" );
        assert( SessionLimits.AdmitGuest( 3 ) == refusal && SessionLimits.AdmitGuest( 40 ) == refusal, "polish_cap_the_fourth_guest_is_the_fifth_participant_and_is_refused_with_the_formal_message" );
        assert( SessionLimits.AdmitGuest( -1 ) == null, "polish_cap_a_negative_count_is_treated_as_empty_not_as_credit" );
        assert( RelayLimits.MaxMembersPerRoom == SessionLimits.MaxParticipants, "polish_cap_the_relay_uses_the_same_number" );
        assert( SessionLimits.MaxConnections > SessionLimits.MaxParticipants, "polish_cap_unproven_sockets_may_exceed_participants_so_the_refusal_can_be_delivered" );

        // The hosted relay Worker counts guests (the host is its own connection), so its limit is one less than the participant count.
        var worker = System.Text.RegularExpressions.Regex.Match( RelayWorkerSource.Text, @"const MAX_GUESTS_PER_ROOM = (\d+);" );
        assert( worker.Success && int.Parse( worker.Groups[1].Value ) == SessionLimits.MaxParticipants - 1, "polish_cap_the_hosted_worker_admits_three_guests_beside_the_host" );

        string dir = Path.Combine( root, "cap" );
        Directory.CreateDirectory( dir );
        using var rig = Rig.Start( dir, null );
        var first = rig.Join( "One", Path.Combine( dir, "g1" ), withClient: false );
        var second = rig.Join( "Two", Path.Combine( dir, "g2" ), withClient: false );
        var third = rig.Join( "Three", Path.Combine( dir, "g3" ), withClient: false );
        assert( first.Welcome != null && second.Welcome != null && third.Welcome != null, "polish_cap_three_guests_are_admitted_beside_the_host" );

        var fifth = rig.Join( "Five", Path.Combine( dir, "g5" ), withClient: false );
        assert( fifth.Welcome == null && fifth.Refusal == refusal, "polish_cap_the_fifth_participant_is_refused_with_the_formal_message: " + fifth.Refusal );

        // A guest reconnecting under its own peer id replaces the old connection; it must not be counted twice and refused by its own ghost.
        var again = rig.Join( "One again", Path.Combine( dir, "g1" ), withClient: false, peer: first.PeerId );
        assert( again.Welcome != null && again.Refusal == null, "polish_cap_a_reconnecting_member_is_not_counted_twice" );
        var stillFull = rig.Join( "Six", Path.Combine( dir, "g6" ), withClient: false );
        assert( stillFull.Welcome == null && stillFull.Refusal == refusal, "polish_cap_after_a_reconnect_the_session_still_holds_exactly_four" );

        // A member who leaves frees the slot at once.
        second.Wire.Send( new Packet { Kind = "leave" } );
        rig.PumpFor( 200 );
        var replacement = rig.Join( "Seven", Path.Combine( dir, "g7" ), withClient: false );
        assert( replacement.Welcome != null, "polish_cap_a_leaving_member_frees_a_slot" );
        var overflow = rig.Join( "Eight", Path.Combine( dir, "g8" ), withClient: false );
        assert( overflow.Welcome == null && overflow.Refusal == refusal, "polish_cap_the_freed_slot_is_taken_only_once" );
    }

    // ---------------------------------------------------------------- 3. coalesced undo reset

    private static void UndoReset( Action<bool, string> assert )
    {
        // Nothing requested, nothing to run.
        var idle = new UndoResetScheduler();
        assert( !idle.ShouldRunNow( 0 ) && !idle.ShouldRunNow( 10_000 ) && !idle.Pending && !idle.Flush(), "polish_undo_without_a_request_nothing_runs" );

        // A single request runs once, at once (a person's single edit is reset without waiting), and not again.
        var single = new UndoResetScheduler();
        single.Request( 1000 );
        int singleRuns = 0;
        for ( double t = 1000; t <= 3000; t += 10 ) if ( single.ShouldRunNow( t ) ) singleRuns++;
        assert( singleRuns == 1 && !single.Pending, "polish_undo_a_single_request_runs_exactly_once" );

        // A burst: 100 requests over 5 s (one per 50 ms, a 20 Hz drag), polled every millisecond. At most once per 250 ms, and the last state is reset once the stream is quiet.
        var burst = new UndoResetScheduler();
        var runs = new List<double>();
        double lastRequest = 0;
        int requests = 0;
        for ( double t = 0; t <= 8000; t += 1 )
        {
            if ( t < 5000 && t % 50 == 0 ) { burst.Request( t ); requests++; lastRequest = t; }
            if ( burst.ShouldRunNow( t ) ) runs.Add( t );
        }
        assert( requests == 100 && runs.Count <= 21 && runs.Count >= 18, "polish_undo_a_burst_of_one_hundred_requests_runs_at_most_twenty_one_times: " + runs.Count );
        assert( runs.Zip( runs.Skip( 1 ), ( a, b ) => b - a ).All( gap => gap >= 250 ), "polish_undo_runs_are_at_least_250_ms_apart" );
        assert( runs[^1] >= lastRequest && runs[^1] <= lastRequest + UndoResetScheduler.IntervalMs && !burst.Pending, "polish_undo_the_final_state_is_reset_within_250_ms_of_the_last_request" );

        // The trailing edge exactly: a request inside the interval waits until 250 ms after it, then runs once.
        var tail = new UndoResetScheduler();
        tail.Request( 0 );
        bool first = tail.ShouldRunNow( 0 );
        tail.Request( 100 );
        bool inside = tail.ShouldRunNow( 120 ) || tail.ShouldRunNow( 249 );
        bool atEdge = tail.ShouldRunNow( 250 );
        bool again = tail.ShouldRunNow( 251 ) || tail.ShouldRunNow( 5000 );
        assert( first && !inside && atEdge && !again, "polish_undo_a_request_held_inside_the_interval_runs_once_when_it_ends_and_never_again" );

        // A request that arrives after the interval has passed runs at its next poll, with no extra quiet wait.
        var late = new UndoResetScheduler();
        late.Request( 0 ); late.ShouldRunNow( 0 );
        late.Request( 1000 );
        assert( late.ShouldRunNow( 1000 ) && !late.ShouldRunNow( 1001 ), "polish_undo_a_request_after_a_quiet_second_runs_at_once" );

        // Flush: a pending reset runs now, whatever the clock says, exactly once; with nothing pending it does nothing.
        var flush = new UndoResetScheduler();
        flush.Request( 0 ); flush.ShouldRunNow( 0 );
        flush.Request( 10 );
        assert( !flush.ShouldRunNow( 20 ) && flush.Pending, "polish_undo_a_request_inside_the_interval_is_held" );
        assert( flush.Flush() && !flush.Pending && !flush.Flush() && !flush.ShouldRunNow( 100_000 ), "polish_undo_flush_runs_the_pending_reset_once_and_nothing_stays_pending" );
        flush.Request( 30 );
        assert( flush.ShouldRunNow( 30 ), "polish_undo_after_a_flush_the_next_request_is_not_held_back_by_the_old_interval" );

        // Time going backwards (a clock reset, a reconnect) never throws and never leaves a reset stuck.
        var back = new UndoResetScheduler();
        back.Request( 5000 ); back.ShouldRunNow( 5000 );
        back.Request( 100 );
        bool stuck = true;
        try { for ( double t = 100; t <= 700; t += 10 ) if ( back.ShouldRunNow( t ) ) { stuck = false; break; } }
        catch ( Exception ) { stuck = true; }
        assert( !stuck && !back.Pending, "polish_undo_time_going_backwards_neither_throws_nor_sticks" );
        assert( ( (Func<bool>)( () => { var q = new UndoResetScheduler(); q.Request( 5000 ); q.ShouldRunNow( 5000 ); q.Request( 100 ); return !q.ShouldRunNow( 349 ) && q.ShouldRunNow( 350 ); } ) )(),
            "polish_undo_trailing_edge_after_a_clock_step_back_runs_250_ms_after_the_last_request" );
        var odd = new UndoResetScheduler();
        odd.Request( double.NaN );
        assert( odd.ShouldRunNow( double.PositiveInfinity ) && !odd.Pending && !odd.ShouldRunNow( double.NaN ), "polish_undo_a_non_finite_reading_runs_the_reset_rather_than_losing_it" );
    }

    // ---------------------------------------------------------------- 4. user actions do not end the session

    private static void ActionFailure( Action<bool, string> assert )
    {
        // Ordinary failures of a person's own action (save, undo, redo, play toggle, a dock button) are reported and the session goes on.
        Exception[] ordinary =
        {
            new IOException( "The disk is full." ), new UnauthorizedAccessException( "Access to the path is denied." ), new NullReferenceException(),
            new InvalidOperationException( "Engine did not preserve object GUID" ), new ArgumentException( "bad" ), new TimeoutException(), new KeyNotFoundException(),
            new ActionRefusal( "Undo is not available." ), new InvalidOperationException( "Undo refused: object changed since your edit" )
        };
        bool allNotices = ordinary.All( e => ContentFailureRules.ClassifyActionFailure( e ) == ActionFailureAction.Notice );
        assert( allNotices, "polish_action_io_null_reference_and_scene_adapter_errors_are_notices_not_session_ends" );

        // Integrity failures stay fail-closed: a frame that does not authenticate, a refused relay, an exhausted nonce space, memory gone.
        Exception[] integrity =
        {
            new System.Security.Cryptography.CryptographicException( "The sealed frame is too short." ), new System.Security.Authentication.AuthenticationException( "The peer could not be authenticated." ),
            new RelayRefusedException( RelayProtocol.LoopbackCode, "the code points to a relay on the host's own computer" ), new OutOfMemoryException()
        };
        assert( integrity.All( e => ContentFailureRules.ClassifyActionFailure( e ) == ActionFailureAction.Fail ), "polish_action_authentication_and_relay_refusals_still_end_the_session" );
        assert( ContentFailureRules.ClassifyActionFailure( new AggregateException( new IOException( "x" ), new System.Security.Cryptography.CryptographicException( "y" ) ) ) == ActionFailureAction.Fail
            && ContentFailureRules.ClassifyActionFailure( new System.Reflection.TargetInvocationException( new System.Security.Cryptography.CryptographicException( "y" ) ) ) == ActionFailureAction.Fail
            && ContentFailureRules.ClassifyActionFailure( new System.Reflection.TargetInvocationException( new IOException( "x" ) ) ) == ActionFailureAction.Notice,
            "polish_action_a_wrapped_integrity_failure_is_still_an_integrity_failure" );
        assert( ContentFailureRules.ClassifyActionFailure( null ) == ActionFailureAction.Notice, "polish_action_nothing_thrown_is_not_a_failure" );

        // The sentence: the first line of the reason, bounded, with no doubled full stop.
        assert( ContentFailureRules.ActionFailureNotice( new IOException( "The disk is full." ) ) == "The action could not be completed: The disk is full. The session continues.",
            "polish_action_notice_wording_has_one_full_stop_after_the_reason" );
        string multi = ContentFailureRules.ActionFailureNotice( new InvalidOperationException( "First line\nstack frame detail" ) );
        assert( multi == "The action could not be completed: First line. The session continues.", "polish_action_notice_uses_only_the_first_line: " + multi );
        string empty = ContentFailureRules.ActionFailureNotice( new NullReferenceException( "" ) );
        assert( empty.StartsWith( "The action could not be completed: " ) && empty.EndsWith( ". The session continues." ) && !empty.Contains( ".." ), "polish_action_notice_with_no_message_still_reads_formally: " + empty );
        string longReason = ContentFailureRules.ActionFailureNotice( new IOException( new string( 'x', 5000 ) ) );
        assert( longReason.Length < 250, "polish_action_notice_is_bounded" );
        assert( ContentFailureRules.ActionFailureNotice( new ActionRefusal( "Recovery is not possible in the session's current state." ) ) == "Recovery is not possible in the session's current state.",
            "polish_action_an_explicit_refusal_keeps_its_own_sentence" );
    }

    // ---------------------------------------------------------------- 5. probe window and wording

    private static void ProbeWindow( Action<bool, string> assert )
    {
        assert( HostingPlan.ReachabilityWindowSeconds == 150 && HostingPlan.ProbeIntervalSeconds == 3 && HostingPlan.RegistrationSettleSeconds == 5, "polish_probe_window_is_150_seconds_with_the_old_cadence_and_settle" );
        const string Url = "wss://abc-def.trycloudflare.com/relay";
        HostingInputs Up( double now, string probe = null, bool registered = true ) => new( now, RelayStarted: true, LocalHostReady: true, TunnelRunning: true, TunnelRelayUrl: Url, TunnelRegistered: registered, ProbeResult: probe );
        var plan = new HostingPlan();
        plan.Pump( new HostingInputs( 0 ) );
        plan.Pump( new HostingInputs( 1, RelayStarted: true ) );
        plan.Pump( Up( 2, registered: false ) );
        plan.Pump( Up( 10 ) );
        // Registered at 10 s, first probe at 15 s: a negative DNS answer cached for a minute must not end hosting at 75 s any more.
        string unreachable = "The public relay address could not be reached.";
        bool failedBefore = false;
        double failedAt = -1;
        for ( double t = 15; t <= 170; t += 0.5 )
        {
            var step = plan.Pump( Up( t, probe: unreachable ) );
            if ( step.Phase == HostingPhase.Failed ) { failedAt = t; break; }
            failedBefore |= t < 165 && step.Phase == HostingPhase.Failed;
        }
        assert( !failedBefore && failedAt == 165, "polish_probe_plan_keeps_probing_until_150_seconds_after_the_first_probe: " + failedAt );
        assert( plan.Reason.Contains( "150 seconds" ) && !plan.Reason.Contains( "75 seconds" ) && plan.Status.Contains( "Friends on your network can still join" ), "polish_probe_failure_sentence_names_150_seconds_and_keeps_the_lan_sentence" );

        // An address whose name only resolves long after the old 75 s window (the first probe's negative answer is cached for 60 s) is still published.
        var late = new HostingPlan();
        late.Pump( new HostingInputs( 0 ) );
        late.Pump( new HostingInputs( 1, RelayStarted: true ) );
        late.Pump( Up( 2, registered: false ) );
        late.Pump( Up( 10 ) );
        bool published = false;
        for ( double t = 15; t <= 150 && !published; t += 0.5 )
        {
            bool probeNow = t >= 100;
            var step = late.Pump( Up( t, probe: probeNow ? HostingPlan.ProbeOk : unreachable ) );
            published |= step.Action == HostingAction.PublishRelayInvite;
        }
        assert( published && late.Phase == HostingPhase.Ready, "polish_probe_an_address_that_resolves_after_90_seconds_is_still_published" );
    }

    private static void RelayWording( Action<bool, string> assert )
    {
        const string Legacy = "Set up relay";
        // The default path: the internet relay is on and nothing else names a relay. The dock shows the plan's status, never the Cloudflare sentence.
        string preparing = HostingPlan.NoRelayNotice( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: true );
        assert( preparing != null && !preparing.Contains( Legacy ) && !preparing.Contains( "No relay is set up" ) && preparing.Contains( "internet relay" ), "polish_wording_default_path_before_the_plan_exists_is_a_preparing_status: " + preparing );
        string status = "Preparing the internet relay: confirming that its public address is reachable. Friends on your network can already join.";
        assert( HostingPlan.NoRelayNotice( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: true, planStatus: status ) == status, "polish_wording_default_path_shows_the_plans_status" );
        string failed = "The internet relay could not be started. The tunnel did not register. Friends on your network can still join with the invite code.";
        assert( HostingPlan.NoRelayNotice( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: true, planStatus: failed ) == failed, "polish_wording_a_failed_plan_shows_its_failure_not_the_setup_sentence" );
        string off = HostingPlan.NoRelayNotice( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: false );
        assert( off != null && off.StartsWith( "The internet relay is turned off on this computer." ) && !off.Contains( Legacy ), "polish_wording_turned_off_on_this_computer_is_said_plainly: " + off );
        assert( !off.Contains( "Cloudflare" ), "polish_wording_turned_off_notice_does_not_mention_cloudflare" );

        // The shared fallback sentence no longer sends anyone to the setup button, whoever calls it.
        string fallback = RelayDefaults.NoRelayNotice( effectiveRelay: null, configured: null );
        assert( fallback != null && !fallback.Contains( Legacy ) && !fallback.Contains( "Setup tab" ), "polish_wording_relay_defaults_no_longer_points_to_the_cloudflare_setup" );

        // The card's own sentence: with the internet relay on, a missing relay of one's own is optional, not a problem; with it off, that is said.
        RelaySetupView Describe( bool internet, DeployResult last = null ) => RelaySetupRules.Describe( new RelaySetupInput( null, null, null, false, null, last, internet ) );
        var on = Describe( true );
        assert( on.Mode == RelaySetupMode.Missing && !on.Text.Contains( "No relay is set up" ) && !on.IsProblem && on.Text.Contains( "automatically" ), "polish_wording_card_with_the_internet_relay_on_says_a_relay_of_ones_own_is_optional: " + on.Text );
        var offView = Describe( false );
        assert( offView.Text.StartsWith( "The internet relay is turned off on this computer." ) && !offView.Text.Contains( "No relay is set up" ), "polish_wording_card_with_the_internet_relay_off_says_so: " + offView.Text );
        assert( Describe( true, new DeployResult( false, null, "Setup was cancelled.", Cancelled: true ) ).Text.Contains( "cancelled" ), "polish_wording_cancelled_setup_is_still_reported" );
        assert( on.ShowOpen && on.ShowSetup && offView.ShowOpen && offView.ShowSetup, "polish_wording_the_advanced_setup_remains_available" );
    }

    // ---------------------------------------------------------------- harness

    private static void Write( string root, string relative, string text )
    {
        string path = Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) );
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        File.WriteAllText( path, text, new UTF8Encoding( false ) );
    }

    private static int FreePort()
    {
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        int port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeGuest
    {
        public Wire Wire;

        public string PeerId;

        public FileSyncClient Client;

        public Packet Welcome;

        public readonly List<Packet> Seen = new();

        public readonly List<string> NoticeLog = new();

        public string Refusal => Seen.FirstOrDefault( p => p.Kind is "reject" or "fatal" )?.Error;

        public List<string> Notices()
        {
            for ( string n = Client?.TakeNotice(); n != null; n = Client.TakeNotice() ) NoticeLog.Add( n );
            return NoticeLog;
        }
    }

    private sealed class Rig : IDisposable
    {
        public SessionServer Server;

        private int port;

        private int joined;

        private readonly List<FakeGuest> guests = new();

        public static Rig Start( string directory, string hostRoot )
        {
            var rig = new Rig();
            Exception last = null;
            for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
            {
                try
                {
                    rig.port = FreePort();
                    rig.Server = new SessionServer( "127.0.0.1", rig.port, "test-project", "test-baseline", "test-token", Path.Combine( directory, $"journal-{Guid.NewGuid():N}.jsonl" ) );
                }
                catch ( SocketException error ) { last = error; }
            }
            if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the polish tests.", last );
            rig.Server.AllowUnverifiedPeers = true;
            rig.Server.FileProjectRoot = hostRoot;
            rig.Server.HostEngineBaseline = "engine-1";
            rig.Server.hostManifest = "host-manifest";
            return rig;
        }

        public FakeGuest Connect( string name, string peer = null )
        {
            joined++;
            var guest = new FakeGuest { Wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult(), PeerId = peer ?? Guid.NewGuid().ToString( "N" ) };
            guests.Add( guest );
            guest.Wire.Send( new Packet
            {
                Kind = "hello", Peer = guest.PeerId, ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                Name = name, Token = "test-token", Project = "test-project", Baseline = "guest-baseline", EngineBaseline = "engine-1",
                SteamId = ( 76561198000000001L + joined ).ToString(), SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "guest-manifest"
            } );
            return guest;
        }

        public FakeGuest Join( string name, string root, bool withClient, string peer = null )
        {
            Directory.CreateDirectory( root );
            var guest = Connect( name, peer );
            PumpUntil( () => guest.Seen.Any( p => p.Kind == "welcome" || p.Error != null ) );
            guest.Welcome = guest.Seen.FirstOrDefault( p => p.Kind == "welcome" );
            if ( withClient && guest.Welcome != null )
            {
                guest.Client = new FileSyncClient( root, () => FixedClock.AddSeconds( Environment.TickCount64 / 1000.0 ) ) { PollInterval = TimeSpan.Zero };
                guest.Client.Begin( guest.Wire.Send );
            }
            return guest;
        }

        public void PumpOnce()
        {
            Server.Pump();
            foreach ( var guest in guests )
            {
                while ( guest.Wire.Incoming.TryDequeue( out var packet ) )
                {
                    if ( guest.Client == null || !guest.Client.Handle( packet, guest.Wire.Send, Array.Empty<string>() ) ) guest.Seen.Add( packet );
                }
                if ( guest.Client != null && guest.Wire.Failure == null ) guest.Client.Poll( guest.Wire.Send, Array.Empty<string>() );
            }
        }

        public void PumpUntil( Func<bool> done, int milliseconds = 30000 )
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                PumpOnce();
                if ( done() ) return;
                Thread.Sleep( 1 );
            }
            throw new TimeoutException( "The fake guests did not reach the expected state." );
        }

        public void PumpFor( int milliseconds )
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                PumpOnce();
                Thread.Sleep( 1 );
            }
        }

        public void Dispose()
        {
            foreach ( var guest in guests ) { try { guest.Wire.Dispose(); } catch ( Exception ) { } }
            try { Server.Dispose(); } catch ( Exception ) { }
        }
    }
}
