using TeamCreate;

/// <summary>
/// The one-click internet relay's state machine (Editor/Core/HostingPlan.cs), driven with an injected clock. The rules it pins are the ones measured in
/// the tunnel smoke test: the printed address is not yet reachable, a lookup made too early is cached as "no such name" for 60 s, so the invite may carry
/// the public relay only after a real health answer, and a tunnel that dies must be taken back out of the invite before anyone is sent to it.
/// </summary>
static class HostingPlanTests
{
    private const string Url = "wss://quiet-river-1234.trycloudflare.com/relay";

    private const string OtherUrl = "wss://loud-ocean-5678.trycloudflare.com/relay";

    private const string Unreachable = "The public relay address answered HTTP 530.";

    // A tunnel that is up, has printed its address and (by default) registered with the edge, while the host's own relay connection is ready.
    private static HostingInputs Up( double now, string probe = null, bool registered = true, bool hostReady = true, string url = Url ) =>
        new( Now: now, RelayStarted: true, LocalHostReady: hostReady, TunnelRunning: true, TunnelRelayUrl: url, TunnelRegistered: registered, ProbeResult: probe );

    private static HostingInputs Down( double now, string error = null ) =>
        new( Now: now, RelayStarted: true, LocalHostReady: true, TunnelError: error );

    // A fresh plan pumped past StartRelay and StartTunnel at t = 0.
    private static HostingPlan Started()
    {
        var plan = new HostingPlan();
        plan.Pump( new HostingInputs( Now: 0 ) );
        plan.Pump( new HostingInputs( Now: 0, RelayStarted: true ) );
        return plan;
    }

    // Registered at 10, first probe at 15, answered at 15.5: Ready with Url published.
    private static HostingPlan ReadyPlan()
    {
        HostingPlan plan = Started();
        plan.Pump( Up( 10 ) );
        plan.Pump( Up( 15 ) );
        plan.Pump( Up( 15.5, probe: HostingPlan.ProbeOk ) );
        return plan;
    }

    public static void Run( Action<bool, string> assert )
    {
        // When the plan applies at all. A project that names a relay, a computer that set one up, a project that turned relaying off and a person who
        // switched the internet relay off all keep exactly the behaviour they had: no tunnel is opened for them.
        assert( HostingPlan.Applies( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: true ), "hosting_applies_without_any_relay" );
        assert( !HostingPlan.Applies( effectiveRelay: "wss://relay.example.com/relay", configuredRelay: "wss://relay.example.com/relay", internetRelayEnabled: true ), "hosting_project_relay_keeps_worker_path" );
        assert( !HostingPlan.Applies( effectiveRelay: "wss://mine.example.workers.dev/relay", configuredRelay: null, internetRelayEnabled: true ), "hosting_own_relay_keeps_worker_path" );
        assert( !HostingPlan.Applies( effectiveRelay: null, configuredRelay: " OFF ", internetRelayEnabled: true ), "hosting_project_off_opens_no_tunnel" );
        assert( !HostingPlan.Applies( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: false ), "hosting_local_opt_out_opens_no_tunnel" );
        assert( new HostingPlan().Phase == HostingPhase.Off, "hosting_off_until_pumped" );

        // Relay first, then the tunnel, each only once the step before it has succeeded.
        var order = new HostingPlan();
        HostingDecision first = order.Pump( new HostingInputs( Now: 0 ) );
        assert( first.Action == HostingAction.StartRelay && first.Phase == HostingPhase.StartingRelay, "hosting_starts_relay_first" );
        assert( order.Pump( new HostingInputs( Now: 0.1 ) ).Action == HostingAction.None, "hosting_tunnel_waits_for_relay" );
        HostingDecision tunnel = order.Pump( new HostingInputs( Now: 0.2, RelayStarted: true ) );
        assert( tunnel.Action == HostingAction.StartTunnel && tunnel.Phase == HostingPhase.StartingTunnel, "hosting_starts_tunnel_after_relay" );
        assert( tunnel.Status.Contains( "Friends on your network can already join" ), "hosting_preparing_says_lan_works" );
        HostingDecision downloading = order.Pump( new HostingInputs( Now: 1, RelayStarted: true, TunnelProgress: "Downloading the tunnel tool (3 of 55 MB)." ) );
        assert( downloading.Status.Contains( "3 of 55 MB" ), "hosting_shows_download_progress" );

        var relayFails = new HostingPlan();
        relayFails.Pump( new HostingInputs( Now: 0 ) );
        HostingDecision relayFailed = relayFails.Pump( new HostingInputs( Now: 0.1, RelayError: "The local relay could not listen on localhost port 8443." ) );
        assert( relayFailed.Phase == HostingPhase.Failed && relayFailed.Action == HostingAction.None, "hosting_relay_error_fails" );
        assert( relayFailed.Status.Contains( "port 8443" ) && relayFailed.Status.Contains( "Friends on your network can still join" ), "hosting_failure_names_reason_and_lan" );

        // No probe while the tunnel has an address but no registered edge connection, and the first probe only 5 s after registration.
        HostingPlan waiting = Started();
        HostingDecision unregistered = waiting.Pump( Up( 10, registered: false ) );
        assert( unregistered.Action == HostingAction.None && unregistered.Phase == HostingPhase.WaitingForTunnelConnection, "hosting_no_probe_before_registered" );
        assert( waiting.Pump( Up( 12, registered: false ) ).Action == HostingAction.None, "hosting_still_no_probe_unregistered" );
        assert( waiting.Pump( Up( 13 ) ).Action == HostingAction.None, "hosting_no_probe_at_registration" );
        assert( waiting.Pump( Up( 17.9 ) ).Action == HostingAction.None, "hosting_five_second_wait" );
        HostingDecision probe = waiting.Pump( Up( 18 ) );
        assert( probe.Action == HostingAction.ProbeReachability && probe.PublicUrl == Url && probe.Phase == HostingPhase.CheckingReachability, "hosting_probe_after_wait" );
        assert( waiting.Pump( Up( 18.5 ) ).Action == HostingAction.None, "hosting_one_probe_in_flight" );

        // A failed probe publishes nothing, and the next one waits 3 s.
        HostingDecision failed = waiting.Pump( Up( 19, probe: Unreachable ) );
        assert( failed.Action == HostingAction.None && failed.Phase == HostingPhase.CheckingReachability, "hosting_no_publish_on_failed_probe" );
        assert( waiting.Pump( Up( 21.9 ) ).Action == HostingAction.None, "hosting_probe_interval" );
        assert( waiting.Pump( Up( 22 ) ).Action == HostingAction.ProbeReachability, "hosting_probe_retry" );
        HostingDecision published = waiting.Pump( Up( 22.4, probe: HostingPlan.ProbeOk ) );
        assert( published.Action == HostingAction.PublishRelayInvite && published.Phase == HostingPhase.Ready && published.PublicUrl == Url, "hosting_publish_only_on_ok" );
        assert( published.Status == "Internet relay ready. The invite code now works over the internet.", "hosting_ready_wording" );
        assert( waiting.Pump( Up( 23 ) ).Action == HostingAction.None, "hosting_publish_once" );

        // An address that registers no edge connection within the bound is a failed start.
        HostingPlan silent = Started();
        silent.Pump( Up( 10, registered: false ) );
        assert( silent.Pump( Up( 39.9, registered: false ) ).Phase == HostingPhase.WaitingForTunnelConnection, "hosting_registration_bound_not_early" );
        assert( silent.Pump( Up( 40, registered: false ) ).Phase == HostingPhase.Failed, "hosting_registration_bound" );

        // A reachable tunnel is still not published while the host's own connection to its relay is not registered: a guest would find no room.
        HostingPlan hostLate = Started();
        hostLate.Pump( Up( 10, hostReady: false ) );
        hostLate.Pump( Up( 15, hostReady: false ) );
        HostingDecision reachableNoHost = hostLate.Pump( Up( 15.5, probe: HostingPlan.ProbeOk, hostReady: false ) );
        assert( reachableNoHost.Action == HostingAction.None && reachableNoHost.Phase != HostingPhase.Ready, "hosting_publish_waits_for_local_host" );
        assert( hostLate.Pump( Up( 16 ) ).Action == HostingAction.PublishRelayInvite, "hosting_publish_when_local_host_ready" );

        // Probes every 3 s, never a publish, and a failure only once 150 s of probing have passed.
        HostingPlan capped = Started();
        capped.Pump( Up( 10 ) );
        int probes = 0;
        bool publishedEarly = false, failedEarly = false;
        for ( double t = 15; t < 165; t += 0.5 )
        {
            HostingDecision step = capped.Pump( Up( t, probe: Unreachable ) );
            probes += step.Action == HostingAction.ProbeReachability ? 1 : 0;
            publishedEarly |= step.Action == HostingAction.PublishRelayInvite;
            failedEarly |= step.Phase == HostingPhase.Failed;
        }
        assert( !publishedEarly && !failedEarly, "hosting_cap_not_before_one_hundred_fifty" );
        assert( probes >= 40 && probes <= 46, "hosting_probe_cadence_three_seconds" );
        HostingDecision timedOut = capped.Pump( Up( 165, probe: Unreachable ) );
        assert( timedOut.Phase == HostingPhase.Failed && capped.Reason.Contains( "150 seconds" ), "hosting_one_hundred_fifty_second_cap" );
        assert( timedOut.Status.Contains( "Friends on your network can still join" ), "hosting_lan_survives_failure" );

        // Only a public wss address is ever published, whatever the tunnel reported.
        HostingPlan loopback = Started();
        bool loopbackPublished = false;
        for ( double t = 10; t < 20; t += 0.5 )
            loopbackPublished |= loopback.Pump( Up( t, probe: HostingPlan.ProbeOk, url: "ws://localhost:8443/relay" ) ).Action == HostingAction.PublishRelayInvite;
        assert( !loopbackPublished && loopback.Phase == HostingPhase.Failed, "hosting_never_publishes_loopback" );

        // A failure before the relay was ever ready is final: a download that timed out is not fixed by retrying seconds later.
        HostingPlan early = Started();
        HostingDecision earlyFail = early.Pump( Down( 5, "The tunnel tool download timed out after five minutes." ) );
        assert( earlyFail.Phase == HostingPhase.Failed && earlyFail.Status.Contains( "timed out" ), "hosting_first_start_failure_is_final" );

        // After Ready: the relay is withdrawn the moment the tunnel dies, then restarted at 5, 15 and 45 s, then given up with LAN still working.
        HostingPlan dying = ReadyPlan();
        assert( dying.Phase == HostingPhase.Ready, "hosting_ready_fixture" );
        HostingDecision died = dying.Pump( Down( 20, "The tunnel stopped unexpectedly." ) );
        assert( died.Action == HostingAction.WithdrawRelayInvite && died.Phase == HostingPhase.Degraded, "hosting_withdraw_on_tunnel_death" );
        assert( died.Status.Contains( "attempt 1 of 3" ), "hosting_degraded_wording" );
        assert( dying.Pump( Down( 24.9 ) ).Action == HostingAction.None, "hosting_first_backoff_five" );
        assert( dying.Pump( Down( 25 ) ).Action == HostingAction.StartTunnel, "hosting_first_restart" );
        assert( dying.Pump( Down( 26, "The tunnel stopped unexpectedly." ) ).Phase == HostingPhase.Degraded, "hosting_restart_failure_degrades" );
        assert( dying.Status.Contains( "attempt 2 of 3" ), "hosting_attempt_two_wording" );
        assert( dying.Pump( Down( 40.9 ) ).Action == HostingAction.None, "hosting_second_backoff_fifteen" );
        assert( dying.Pump( Down( 41 ) ).Action == HostingAction.StartTunnel, "hosting_second_restart" );
        dying.Pump( Down( 42, "The tunnel stopped unexpectedly." ) );
        assert( dying.Pump( Down( 86.9 ) ).Action == HostingAction.None, "hosting_third_backoff_forty_five" );
        assert( dying.Pump( Down( 87 ) ).Action == HostingAction.StartTunnel, "hosting_third_restart" );
        HostingDecision exhausted = dying.Pump( Down( 88, "The tunnel stopped unexpectedly." ) );
        assert( exhausted.Phase == HostingPhase.Failed && exhausted.Action == HostingAction.None, "hosting_three_restart_limit" );
        assert( exhausted.Status.Contains( "could not be restarted" ) && exhausted.Status.Contains( "Friends on your network can still join" ), "hosting_exhausted_wording" );
        assert( dying.Pump( Down( 500 ) ).Action == HostingAction.None, "hosting_failed_is_final" );

        // A restarted tunnel has a NEW address: it is probed again from scratch and the invite carries the new one, with wording that says so.
        HostingPlan restarted = ReadyPlan();
        restarted.Pump( Down( 20, "The tunnel stopped unexpectedly." ) );
        restarted.Pump( Down( 25 ) );
        assert( restarted.Pump( Up( 26, url: OtherUrl ) ).Action == HostingAction.None, "hosting_restart_waits_again" );
        assert( restarted.Pump( Up( 30.9, url: OtherUrl ) ).Action == HostingAction.None, "hosting_restart_five_second_wait" );
        HostingDecision reprobe = restarted.Pump( Up( 31, url: OtherUrl ) );
        assert( reprobe.Action == HostingAction.ProbeReachability && reprobe.PublicUrl == OtherUrl, "hosting_restart_reprobes_new_address" );
        HostingDecision republished = restarted.Pump( Up( 31.5, probe: HostingPlan.ProbeOk, url: OtherUrl ) );
        assert( republished.Action == HostingAction.PublishRelayInvite && republished.PublicUrl == OtherUrl, "hosting_republishes_new_address" );
        assert( republished.Status.Contains( "new address" ) && republished.Status.Contains( "invite code" ), "hosting_new_address_wording" );

        // A tunnel that is simply no longer running (no error text) is withdrawn too.
        HostingPlan vanished = ReadyPlan();
        assert( vanished.Pump( new HostingInputs( Now: 20, RelayStarted: true, LocalHostReady: true ) ).Action == HostingAction.WithdrawRelayInvite, "hosting_withdraw_when_not_running" );

        // The local relay failing after Ready withdraws and fails: a tunnel restart cannot bring back the room the guests were in.
        HostingPlan relayLost = ReadyPlan();
        HostingDecision lost = relayLost.Pump( new HostingInputs( Now: 20, RelayStarted: true, RelayError: "The host's connection to its local relay failed.", TunnelRunning: true, TunnelRelayUrl: Url, TunnelRegistered: true ) );
        assert( lost.Action == HostingAction.WithdrawRelayInvite && lost.Phase == HostingPhase.Failed, "hosting_relay_loss_withdraws_and_fails" );

        // The health answer: exactly the relay's own JSON, from a 200, bounded.
        string health = "{\"status\":\"ok\",\"service\":\"collab-relay\",\"protocol\":" + RelayProtocol.ProtocolVersion + "}";
        assert( HostingPlan.ProbeVerdict( 200, health ) == HostingPlan.ProbeOk, "hosting_probe_accepts_relay_health" );
        assert( HostingPlan.ProbeVerdict( 530, health ) != HostingPlan.ProbeOk, "hosting_probe_refuses_non_200" );
        assert( HostingPlan.ProbeVerdict( null, null ) != HostingPlan.ProbeOk, "hosting_probe_refuses_no_answer" );
        assert( HostingPlan.ProbeVerdict( 200, "ok" ) != HostingPlan.ProbeOk, "hosting_probe_refuses_other_server" );
        assert( HostingPlan.ProbeVerdict( 200, "<html>Cloudflare</html>" ) != HostingPlan.ProbeOk, "hosting_probe_refuses_html" );
        assert( HostingPlan.ProbeVerdict( 200, "{\"status\":\"ok\",\"service\":\"collab-relay\",\"protocol\":" + ( RelayProtocol.ProtocolVersion + 1 ) + "}" ) != HostingPlan.ProbeOk, "hosting_probe_refuses_other_protocol" );
        assert( HostingPlan.ProbeVerdict( 200, health + new string( ' ', 5000 ) ) != HostingPlan.ProbeOk, "hosting_probe_bounded" );
        assert( HostingPlan.HealthUrl( Url ) == "https://quiet-river-1234.trycloudflare.com/health", "hosting_health_url" );
        assert( HostingPlan.HealthUrl( "ws://localhost:8443/relay" ) == null && HostingPlan.HealthUrl( "not a url" ) == null, "hosting_health_url_public_only" );

        // The host's own connection: a loopback port the engine's WebSocket policy allows, and never something an invite may carry.
        foreach ( int port in HostingPlan.LocalRelayPorts )
        {
            string local = HostingPlan.LocalRelayUrl( port );
            assert( RelayProtocol.TryValidateUrl( local, out _, out _ ) && RelayProtocol.InviteRelayFor( local, allowLoopback: false ) == null, "hosting_local_url_engine_allowed_" + port );
        }

        // The invite's relay, at its single call site's composition: the embedded path carries only the published public address.
        const string Local = "ws://localhost:8443/relay";
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: true, embeddedRelayInUse: true, publicRelayUrl: Url, effectiveRelayUrl: null, soloHarness: false ) == Url, "hosting_invite_carries_published_url" );
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: true, embeddedRelayInUse: true, publicRelayUrl: null, effectiveRelayUrl: null, soloHarness: true ) == null, "hosting_invite_nothing_before_publish" );
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: true, embeddedRelayInUse: true, publicRelayUrl: Local, effectiveRelayUrl: Local, soloHarness: true ) == null, "hosting_invite_never_loopback_even_solo" );
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: false, embeddedRelayInUse: true, publicRelayUrl: Url, effectiveRelayUrl: null, soloHarness: false ) == null, "hosting_invite_withdrawn" );
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: true, embeddedRelayInUse: false, publicRelayUrl: null, effectiveRelayUrl: "wss://relay.example.com/relay", soloHarness: false ) == "wss://relay.example.com/relay", "hosting_invite_worker_path_unchanged" );
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: true, embeddedRelayInUse: false, publicRelayUrl: null, effectiveRelayUrl: Local, soloHarness: true ) == Local, "hosting_invite_solo_loopback_unchanged" );
        assert( HostingPlan.InviteRelay( inviteCarriesRelay: true, embeddedRelayInUse: false, publicRelayUrl: null, effectiveRelayUrl: Local, soloHarness: false ) == null, "hosting_invite_worker_loopback_refused" );

        // What the dock says when no plan runs because this computer turned the internet relay off.
        string lanOnly = HostingPlan.NoRelayNotice( effectiveRelay: null, configuredRelay: null, internetRelayEnabled: false );
        assert( lanOnly != null && lanOnly.Contains( "turned off on this computer" ) && !lanOnly.Contains( "Cloudflare" ), "hosting_lan_only_notice" );
        assert( HostingPlan.NoRelayNotice( effectiveRelay: null, configuredRelay: "off", internetRelayEnabled: false ).Contains( "RelayUrl is \"off\"" ), "hosting_project_off_notice_kept" );
        assert( HostingPlan.NoRelayNotice( effectiveRelay: "wss://relay.example.com/relay", configuredRelay: null, internetRelayEnabled: false ) == null, "hosting_worker_needs_no_notice" );
    }
}
