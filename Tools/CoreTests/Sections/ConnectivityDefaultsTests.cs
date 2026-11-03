using TeamCreate;

/// <summary>
/// The connectivity defaults that make joining work with nothing configured: the built-in relay list, a route order in which the relay is never
/// crowded out by LAN answers, discovery replies that no longer give the session secret to everyone on the network, and limits that share one source.
/// </summary>
static class ConnectivityDefaultsTests
{
    private static readonly DateTime Now = new( 2026, 11, 3, 12, 0, 0, DateTimeKind.Utc );

    private static readonly string Secret = "00112233445566778899aabbccddeeff";

    public static void Run( Action<bool, string> assert )
    {
        BuiltInRelays( assert );
        RouteOrder( assert );
        DiscoverySecret( assert );
        SharedLimits( assert );
        SharedRelayCodes( assert );
    }

    // ---- E1: the shared relay's new refusal reads as a sentence and is retried --------------------------------------------------

    private static void SharedRelayCodes( Action<bool, string> assert )
    {
        assert( RelayProtocol.UserMessage( "idle", null ).Contains( "no activity" ), "conn_shared_idle_code_has_a_sentence" );
        assert( !RelayProtocol.IsTerminal( "idle" ), "conn_shared_idle_is_retried_not_terminal" );
        assert( RelayWorkerSource.Text.Contains( "\"idle\"" ) && RelayWorkerSource.Text.Contains( "RELAY_MODE" ), "conn_shared_mode_is_in_the_embedded_worker" );
    }

    // ---- E2: an ordered list of built-in relays; configured > built-in > none ---------------------------------------------------

    private static void BuiltInRelays( Action<bool, string> assert )
    {
        assert( RelayDefaults.BuiltInUrls != null, "conn_builtin_list_exists" );
        assert( RelayDefaults.BuiltInUrls.All( u => RelayProtocol.TryValidateUrl( u, out _, out _ ) && RelayProtocol.IsPublicRelay( u ) ),
            "conn_builtin_list_holds_only_valid_public_relays" );
        assert( RelayDefaults.DefaultUrl == RelayDefaults.FirstUsable( RelayDefaults.BuiltInUrls ), "conn_default_is_first_usable_builtin" );
        assert( RelayDefaults.FirstUsable( null ) == null && RelayDefaults.FirstUsable( Array.Empty<string>() ) == null, "conn_no_builtin_means_no_default" );
        assert( RelayDefaults.FirstUsable( new[] { "", "  ", "not a url", "ws://localhost:8080/relay", "wss://a.example.workers.dev/relay", "wss://b.example.workers.dev/relay" } )
            == "wss://a.example.workers.dev/relay", "conn_first_usable_skips_blank_invalid_and_loopback_and_keeps_order" );
        assert( RelayDefaults.FirstUsable( new[] { " wss://a.example.workers.dev/relay " } ) == "wss://a.example.workers.dev/relay", "conn_first_usable_trims" );
        string builtIn = RelayDefaults.FirstUsable( new[] { "wss://a.example.workers.dev/relay" } );
        assert( RelayDefaults.Resolve( "wss://mine.example.com/relay", builtIn ) == "wss://mine.example.com/relay", "conn_configured_beats_builtin" );
        assert( RelayDefaults.Resolve( null, builtIn ) == builtIn, "conn_builtin_used_when_nothing_configured" );
        assert( RelayDefaults.Resolve( RelayDefaults.Off, builtIn ) == null, "conn_off_beats_builtin" );
    }

    // ---- E3: the relay always keeps a slot --------------------------------------------------------------------------------------

    private static void RouteOrder( Action<bool, string> assert )
    {
        var many = Enumerable.Range( 1, 12 ).Select( i => new DiscoveredHost( "10.0.0." + i, 27845, "h" + i, null, "deadbeef", Now ) ).ToList();
        var withRelay = new InviteData { Port = 27845, Relay = "wss://relay.example.workers.dev/relay", Endpoints = new List<byte[]> { new byte[] { 203, 0, 113, 7 } } };
        var ordered = EndpointResolver.Order( withRelay, many, "10.9.8.7", 27845 ).ToList();
        assert( ordered.Count == EndpointResolver.MaxCandidates, "conn_route_still_bounded" );
        assert( ordered.Count( c => c.Source == EndpointResolver.RelaySource ) == 1, "conn_route_relay_kept_when_lan_floods" );
        assert( ordered.Take( ordered.Count - 1 ).All( c => c.Source == "lan" ) && ordered[^1].Source == EndpointResolver.RelaySource,
            "conn_route_lan_first_then_reserved_relay" );
        var exactlyFull = Enumerable.Range( 1, EndpointResolver.MaxCandidates ).Select( i => new DiscoveredHost( "10.0.1." + i, 27845, "h", null, "deadbeef", Now ) ).ToList();
        assert( EndpointResolver.Order( withRelay, exactlyFull, null, 0 ).Any( c => c.Source == EndpointResolver.RelaySource ), "conn_route_relay_kept_at_exact_limit" );
        var noRelay = new InviteData { Port = 27845, Endpoints = new List<byte[]>() };
        assert( EndpointResolver.Order( noRelay, many, null, 0 ).Count( c => c.Source == "lan" ) == EndpointResolver.MaxCandidates,
            "conn_route_no_slot_wasted_without_a_relay" );
        assert( EndpointResolver.Order( withRelay, null, null, 0 ).Select( c => c.Source ).SequenceEqual( new[] { EndpointResolver.RelaySource, "code" } ),
            "conn_route_relay_then_code_unchanged" );
    }

    // ---- E5: discovery replies stop carrying the session secret -----------------------------------------------------------------

    private static void DiscoverySecret( Action<bool, string> assert )
    {
        string reply = DiscoveryProtocol.BuildReply( "deadbeef", 32123, "build-box", "0a1b2c3d" );
        assert( reply == "TC1!pid=deadbeef;port=32123;name=build-box;rev=0a1b2c3d\n", "conn_discovery_reply_has_no_secret" );
        assert( DiscoveryProtocol.TryParseReply( reply, out var parsed ) && parsed.SecretHex == null && parsed.SessionPort == 32123 && parsed.Manifest8 == "0a1b2c3d",
            "conn_discovery_secretless_reply_parses" );
        // A 1.2.0 host still sends the secret; a newer joiner keeps understanding it.
        string old = "TC1!pid=deadbeef;port=32123;name=build-box;secret=" + Secret + ";rev=0a1b2c3d\n";
        assert( DiscoveryProtocol.TryParseReply( old, out var oldParsed ) && oldParsed.SecretHex == Secret, "conn_discovery_old_reply_still_parses" );
        assert( !DiscoveryProtocol.TryParseReply( old.Replace( Secret, Secret.Substring( 0, 31 ) ), out _ ), "conn_discovery_malformed_secret_still_refused" );
        // A 1.2.0 joiner requires the secret field and so ignores this reply; it joins with the full invite code, whose candidates do not depend on discovery.
        var invite = new InviteData { Port = 32123, Secret = Convert.FromHexString( Secret ), Endpoints = new List<byte[]> { new byte[] { 192, 168, 1, 20 } } };
        assert( EndpointResolver.Order( invite, null, null, 0 ).Any( c => c.Address == "192.168.1.20" && c.Port == 32123 ), "conn_discovery_old_joiner_has_code_route" );
        assert( LanDiscovery.HostReply( "deadbeef", 32123, "box", "0a1b2c3d" ).IndexOf( "secret", StringComparison.Ordinal ) < 0, "conn_discovery_host_broadcast_has_no_secret" );
    }

    // ---- E6: limits that come from one place ------------------------------------------------------------------------------------

    private static void SharedLimits( Action<bool, string> assert )
    {
        assert( RelayHost.MaxChannels == SessionLimits.MaxConnections, "conn_relay_channels_match_host_connections" );
        assert( RelayHost.MaxChannels >= SessionLimits.MaxParticipants, "conn_relay_channels_leave_headroom_over_participants" );
        assert( LanDiscovery.MaxTrackedHosts >= EndpointResolver.MaxCandidates, "conn_discovery_tracks_at_least_a_full_route_list" );
    }
}
