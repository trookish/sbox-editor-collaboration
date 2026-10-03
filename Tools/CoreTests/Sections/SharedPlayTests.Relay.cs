using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// Shared play through a real <see cref="SessionServer"/> and real wires over loopback: who may play, where frames go, and what happens to a
/// spectator that falls behind or a player that leaves. The members are fake clients that speak the protocol directly.
/// </summary>
static partial class SharedPlayTests
{
    sealed class PlayRig : IDisposable
    {
        public SessionServer Server;

        public string Directory;

        public int Port;

        static int steamCounter;

        public readonly Dictionary<string, Wire> Wires = new Dictionary<string, Wire>();

        public readonly Dictionary<string, List<Packet>> Seen = new Dictionary<string, List<Packet>>();

        public readonly Dictionary<string, string> Peers = new Dictionary<string, string>();

        public void Dispose()
        {
            foreach ( var wire in Wires.Values ) wire.Dispose();
            Server?.Dispose();
        }

        public void Drain()
        {
            Server.Pump();
            foreach ( var pair in Wires ) while ( pair.Value.Incoming.TryDequeue( out var p ) ) Seen[pair.Key].Add( p );
        }

        public void PumpFor( Func<bool> done, string what = "the expected packets" )
        {
            var until = DateTime.UtcNow.AddSeconds( 8 );
            while ( DateTime.UtcNow < until )
            {
                Drain();
                if ( done() ) return;
                Thread.Sleep( 5 );
            }
            throw new TimeoutException( "The fake members did not receive " + what + "." );
        }

        public void Join( string name, bool optIn )
        {
            var wire = Wire.Connect( "127.0.0.1", Port ).GetAwaiter().GetResult();
            string peer = Guid.NewGuid().ToString();
            wire.Send( new Packet
            {
                Kind = "hello", Peer = peer, ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                Name = name, Token = "test-token", Project = "test-project", Baseline = "test-baseline",
                SteamId = "7656119800000" + ( 1000 + ++steamCounter ), SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4",
            } );
            Wires[name] = wire;
            Seen[name] = new List<Packet>();
            Peers[name] = peer;
            PumpFor( () => Seen[name].Any( p => p.Kind == "welcome" ), "a welcome for " + name );
            if ( optIn ) wire.Send( new Packet { Kind = "play-optin" } );
        }

        public void Send( string who, Packet packet ) => Wires[who].Send( packet );

        /// <summary>Round-trips a ping through the host for every member: the wire is ordered, so everything sent before the pong has arrived.</summary>
        public void Settle()
        {
            var expected = Wires.Keys.ToDictionary( k => k, k => Seen[k].Count( p => p.Kind == "pong" ) + 1 );
            foreach ( var wire in Wires.Values ) wire.Send( new Packet { Kind = "ping" } );
            PumpFor( () => expected.All( e => Seen[e.Key].Count( p => p.Kind == "pong" ) >= e.Value ) );
        }

        public List<Packet> Of( string who, string kind ) => Seen[who].Where( p => p.Kind == kind ).ToList();
    }

    static PlayRig StartPlayRig( bool sharedPlay = true )
    {
        var rig = new PlayRig();
        var root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        rig.Directory = Path.Combine( root, "teamcreate-coretests", Guid.NewGuid().ToString( "N" ) );
        Exception last = null;
        for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
        {
            var probe = new TcpListener( IPAddress.Loopback, 0 );
            probe.Start();
            int port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
            probe.Stop();
            try
            {
                rig.Server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", Path.Combine( rig.Directory, "journal-" + attempt + ".jsonl" ) );
                rig.Port = port;
            }
            catch ( SocketException error ) { last = error; }
        }
        if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the shared play tests.", last );
        rig.Server.AllowUnverifiedPeers = true;
        rig.Server.SharedPlayEnabled = sharedPlay;
        return rig;
    }

    static Packet Frame( string runId, long seq, bool key = false ) => new Packet
    {
        Kind = "play-frame", RuntimeRunId = runId, RuntimeSequence = seq, Play = new PlayFrame { Seq = seq, Key = key }.ToJson(),
    };

    static void Relay( Action<bool, string> assert )
    {
        // Policy first: the pure rule for a spectator that falls behind.
        assert( PlayRelayPolicy.ShouldSend( false, false, 0, 0, out bool behind ) && !behind, "play_policy_healthy_spectator_gets_every_frame" );
        assert( !PlayRelayPolicy.ShouldSend( false, false, PlayRelayPolicy.MaxPendingBytes + 1, 0, out behind ) && behind, "play_policy_over_one_mebibyte_is_behind" );
        assert( !PlayRelayPolicy.ShouldSend( false, true, PlayRelayPolicy.MaxPendingBytes + 1, 0, out behind ) && behind, "play_policy_even_a_keyframe_waits_while_the_queue_is_full" );
        assert( !PlayRelayPolicy.ShouldSend( false, false, 0, PlayRelayPolicy.MaxPendingMessages + 1, out behind ) && behind, "play_policy_message_backlog_is_behind_too" );
        assert( !PlayRelayPolicy.ShouldSend( true, false, 0, 0, out behind ) && behind, "play_policy_a_spectator_that_was_behind_skips_deltas_even_when_drained" );
        assert( PlayRelayPolicy.ShouldSend( true, true, 0, 0, out behind ) && !behind, "play_policy_the_next_keyframe_resumes_a_drained_spectator" );
        assert( !PlayRelayPolicy.ShouldSend( true, true, PlayRelayPolicy.ResumeBytes + 1, 0, out behind ) && behind, "play_policy_a_keyframe_is_not_sent_into_a_queue_that_has_not_drained" );

        using ( var rig = StartPlayRig() )
        {
            rig.Join( "Player", true );
            rig.Join( "Spec1", true );
            rig.Join( "Spec2", true );
            assert( rig.Seen["Player"].First( p => p.Kind == "welcome" ).Features?.Contains( "shared-play" ) == true, "play_server_welcome_advertises_shared_play_when_enabled" );

            // Who plays.
            rig.Send( "Player", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => new[] { "Player", "Spec1", "Spec2" }.All( w => rig.Of( w, "play-start" ).Count == 1 ) );
            rig.Settle();
            var start = rig.Of( "Spec1", "play-start" )[0];
            string run = start.RuntimeRunId;
            assert( start.Peer == rig.Peers["Player"] && start.Scene == PlayScene && start.Name == "Player" && !string.IsNullOrEmpty( run ), "play_server_start_is_broadcast_with_player_scene_and_run" );

            // The single-player rule, on the real server.
            rig.Send( "Spec1", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => rig.Of( "Spec1", "play-refused" ).Count == 1 );
            rig.Settle();
            assert( rig.Of( "Spec1", "play-refused" )[0].Notice == "Player is already playing. Wait until they stop, or ask them to stop. Your run is local and is not shown to the others.", "play_server_second_player_gets_the_formal_refusal" );
            assert( rig.Of( "Player", "play-refused" ).Count == 0 && rig.Of( "Spec2", "play-refused" ).Count == 0 && rig.Of( "Spec1", "play-start" ).Count == 1 && rig.Of( "Spec2", "play-start" ).Count == 1, "play_server_refusal_goes_only_to_the_refused_and_changes_nothing" );
            rig.Send( "Spec2", new Packet { Kind = "play-start", Scene = "../../etc/passwd" } );
            rig.Settle();
            assert( rig.Of( "Spec2", "play-start" ).Count == 1 && !rig.Seen.Values.SelectMany( s => s ).Any( p => p.Kind == "fatal" ), "play_server_invalid_starts_are_dropped_without_fatal" );

            // Frames: to spectators only, never echoed to the player.
            rig.Send( "Player", Frame( run, 1, key: true ) );
            rig.Send( "Player", Frame( run, 2 ) );
            rig.PumpFor( () => rig.Of( "Spec1", "play-frame" ).Count == 2 && rig.Of( "Spec2", "play-frame" ).Count == 2 );
            rig.Settle();
            assert( rig.Of( "Player", "play-frame" ).Count == 0, "play_server_never_echoes_frames_to_the_player" );
            assert( rig.Of( "Spec1", "play-frame" ).Select( f => f.RuntimeSequence ).SequenceEqual( new long?[] { 1, 2 } ) && rig.Of( "Spec1", "play-frame" )[0].Peer == rig.Peers["Player"], "play_server_frames_arrive_in_order_stamped_with_the_player" );

            // A frame from anybody but the player, or for another run, goes nowhere.
            rig.Send( "Spec1", Frame( run, 3 ) );
            rig.Send( "Player", Frame( "other-run", 3 ) );
            rig.Send( "Player", new Packet { Kind = "play-frame", RuntimeRunId = run, RuntimeSequence = 3, Play = null } );
            rig.Settle();
            assert( rig.Of( "Spec2", "play-frame" ).Count == 2 && rig.Of( "Player", "play-frame" ).Count == 0 && rig.Of( "Spec1", "play-frame" ).Count == 2, "play_server_frames_from_non_players_or_other_runs_are_dropped" );

            // Keyframe on request: the spectator asks, the player is asked, a flood of requests is one.
            rig.Send( "Spec1", new Packet { Kind = "play-key", RuntimeRunId = run } );
            rig.Send( "Spec1", new Packet { Kind = "play-key", RuntimeRunId = run } );
            rig.Send( "Spec2", new Packet { Kind = "play-key", RuntimeRunId = run } );
            rig.PumpFor( () => rig.Of( "Player", "play-key" ).Count >= 1 );
            rig.Settle();
            assert( rig.Of( "Player", "play-key" ).Count == 1 && rig.Of( "Player", "play-key" )[0].RuntimeRunId == run, "play_server_keyframe_requests_are_coalesced_and_reach_only_the_player" );

            // Stop: only the player ends the run, naming it.
            rig.Send( "Spec1", new Packet { Kind = "play-stop", RuntimeRunId = run } );
            rig.Send( "Player", new Packet { Kind = "play-stop", RuntimeRunId = "stale" } );
            rig.Settle();
            assert( rig.Of( "Spec2", "play-stop" ).Count == 0, "play_server_stop_from_a_spectator_or_with_a_stale_run_is_ignored" );
            rig.Send( "Player", new Packet { Kind = "play-stop", RuntimeRunId = run } );
            rig.PumpFor( () => rig.Of( "Spec2", "play-stop" ).Count == 1 && rig.Of( "Player", "play-stop" ).Count == 1 );
            rig.Send( "Player", new Packet { Kind = "play-stop", RuntimeRunId = run } );
            rig.Send( "Player", Frame( run, 4 ) );
            rig.Settle();
            assert( rig.Of( "Spec2", "play-stop" ).Count == 1 && rig.Of( "Spec2", "play-frame" ).Count == 2, "play_server_a_stopped_run_neither_repeats_its_stop_nor_relays_late_frames" );

            // After a stop somebody else may play.
            rig.Send( "Spec1", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => rig.Of( "Spec2", "play-start" ).Count == 2 );
            assert( rig.Of( "Spec2", "play-start" )[1].Peer == rig.Peers["Spec1"] && rig.Of( "Spec2", "play-start" )[1].RuntimeRunId != run, "play_server_the_next_player_gets_a_new_run" );
            string second = rig.Of( "Spec2", "play-start" )[1].RuntimeRunId;

            // A guest playing: its frames reach the others, and the former player is now a spectator of it.
            rig.Send( "Spec1", Frame( second, 1, key: true ) );
            rig.PumpFor( () => rig.Of( "Player", "play-frame" ).Count == 1 && rig.Of( "Spec2", "play-frame" ).Count == 3 );
            rig.Settle();
            assert( rig.Of( "Spec1", "play-frame" ).Count == 2, "play_server_a_guest_player_is_not_echoed_either" );

            // The player leaves cleanly: the run ends for everyone.
            rig.Send( "Spec1", new Packet { Kind = "leave" } );
            rig.PumpFor( () => rig.Of( "Player", "play-stop" ).Count == 2 && rig.Of( "Spec2", "play-stop" ).Count == 2 );
            assert( rig.Of( "Spec2", "play-stop" )[1].RuntimeRunId == second && rig.Of( "Spec2", "play-stop" )[1].Peer == rig.Peers["Spec1"], "play_server_player_leaving_ends_the_run_for_everyone" );
        }

        // A peer that did not opt in sees nothing. (A separate rig: the session holds the host and three guests at most.)
        using ( var rig = StartPlayRig() )
        {
            rig.Join( "Player", true );
            rig.Join( "Spec1", true );
            rig.Join( "Outsider", false );
            rig.Send( "Player", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => rig.Of( "Spec1", "play-start" ).Count == 1 );
            string run = rig.Of( "Spec1", "play-start" )[0].RuntimeRunId;
            rig.Send( "Outsider", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.Send( "Player", Frame( run, 1, key: true ) );
            rig.PumpFor( () => rig.Of( "Spec1", "play-frame" ).Count == 1 );
            rig.Settle();
            assert( rig.Of( "Outsider", "play-start" ).Count == 0, "play_server_peers_that_did_not_opt_in_see_nothing" );
            assert( rig.Of( "Outsider", "play-frame" ).Count == 0, "play_server_frames_skip_peers_that_did_not_opt_in" );
            assert( !rig.Seen.Values.SelectMany( x => x ).Any( x => x.Kind == "fatal" ), "play_server_a_non_opted_in_start_is_dropped_without_fatal" );
        }

        // Disconnect without a goodbye (the connection drops), the host being the player, and a late opt-in.
        using ( var rig = StartPlayRig() )
        {
            rig.Join( "Host", true );
            rig.Join( "Guest", true );
            rig.Send( "Host", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => rig.Of( "Guest", "play-start" ).Count == 1 );
            string run = rig.Of( "Guest", "play-start" )[0].RuntimeRunId;
            rig.Join( "Late", true );
            rig.PumpFor( () => rig.Of( "Late", "play-start" ).Count == 1 && rig.Of( "Host", "play-key" ).Count >= 1 );
            assert( rig.Of( "Late", "play-start" )[0].RuntimeRunId == run && rig.Of( "Late", "play-start" )[0].Peer == rig.Peers["Host"], "play_server_late_opt_in_is_told_about_the_running_run" );
            assert( rig.Of( "Host", "play-key" )[0].RuntimeRunId == run, "play_server_late_opt_in_asks_the_player_for_a_keyframe" );
            rig.Wires["Host"].Dispose();
            rig.Wires.Remove( "Host" );
            rig.PumpFor( () => rig.Of( "Guest", "play-stop" ).Count == 1 && rig.Of( "Late", "play-stop" ).Count == 1, "the play-stop after the player's connection dropped" );
            assert( rig.Of( "Guest", "play-stop" )[0].RuntimeRunId == run, "play_server_dropped_connection_of_the_player_ends_the_run" );

            // Reconnect mid-play: the same person plays again as a new run.
            rig.Join( "Host2", true );
            rig.Send( "Host2", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => rig.Of( "Guest", "play-start" ).Count == 2 );
            assert( rig.Of( "Guest", "play-start" )[1].RuntimeRunId != run, "play_server_reconnected_player_starts_a_new_run" );
        }

        // A lagging spectator: skipped while behind, and given only the next keyframe afterwards.
        using ( var rig = StartPlayRig() )
        {
            rig.Join( "Player", true );
            rig.Join( "Slow", true );
            rig.Join( "Fast", true );
            rig.Send( "Player", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.PumpFor( () => rig.Of( "Slow", "play-start" ).Count == 1 );
            string run = rig.Of( "Slow", "play-start" )[0].RuntimeRunId;
            long slowBytes = 0;
            rig.Server.PlayBacklogProbe = peer => peer == rig.Peers["Slow"] ? (slowBytes, 0) : (0, 0);
            rig.Send( "Player", Frame( run, 1, key: true ) );
            rig.PumpFor( () => rig.Of( "Slow", "play-frame" ).Count == 1 && rig.Of( "Fast", "play-frame" ).Count == 1 );
            slowBytes = PlayRelayPolicy.MaxPendingBytes + 1;
            rig.Send( "Player", Frame( run, 2 ) );
            rig.Send( "Player", Frame( run, 3 ) );
            rig.PumpFor( () => rig.Of( "Fast", "play-frame" ).Count == 3 );
            rig.Settle();
            assert( rig.Of( "Slow", "play-frame" ).Count == 1, "play_server_a_spectator_over_one_mebibyte_receives_no_deltas" );
            slowBytes = 0;
            rig.Send( "Player", Frame( run, 4 ) );
            rig.PumpFor( () => rig.Of( "Fast", "play-frame" ).Count == 4 );
            rig.Settle();
            assert( rig.Of( "Slow", "play-frame" ).Count == 1, "play_server_a_drained_spectator_still_waits_for_a_keyframe" );
            rig.Send( "Player", Frame( run, 5, key: true ) );
            rig.Send( "Player", Frame( run, 6 ) );
            rig.PumpFor( () => rig.Of( "Fast", "play-frame" ).Count == 6 && rig.Of( "Slow", "play-frame" ).Count == 3 );
            assert( rig.Of( "Slow", "play-frame" ).Select( f => f.RuntimeSequence ).SequenceEqual( new long?[] { 1, 5, 6 } ), "play_server_keyframe_only_catch_up_then_deltas_resume" );
            assert( rig.Of( "Player", "play-key" ).Count >= 1, "play_server_a_spectator_that_fell_behind_asks_for_a_keyframe" );
        }

        // Off by default: a host without the setting neither advertises the feature nor reacts, and nothing fails.
        using ( var rig = StartPlayRig( sharedPlay: false ) )
        {
            rig.Join( "A", true );
            rig.Join( "B", true );
            assert( rig.Seen["A"].First( p => p.Kind == "welcome" ).Features?.Contains( "shared-play" ) != true, "play_server_off_by_default_does_not_advertise" );
            rig.Send( "A", new Packet { Kind = "play-start", Scene = PlayScene } );
            rig.Send( "A", Frame( "x", 1, key: true ) );
            rig.Settle();
            assert( rig.Seen.Values.SelectMany( s => s ).All( p => !p.Kind.StartsWith( "play-", StringComparison.Ordinal ) && p.Kind != "fatal" ), "play_server_off_by_default_ignores_play_messages_quietly" );
        }
    }
}
