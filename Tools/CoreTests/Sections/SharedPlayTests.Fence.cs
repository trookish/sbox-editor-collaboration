using TeamCreate;

/// <summary>The opt-in setting and the source fence: shared play is off until chosen, and nothing in the library reaches the game's networking.</summary>
static partial class SharedPlayTests
{
    static string RepoRoot()
    {
        string root = Directory.GetCurrentDirectory();
        while ( root != null && !File.Exists( Path.Combine( root, "Libraries", "collaboration", "VERSION" ) ) )
            root = Directory.GetParent( root )?.FullName;
        return root ?? throw new DirectoryNotFoundException( "The repository root was not found from " + Directory.GetCurrentDirectory() );
    }

    static void Fence( Action<bool, string> assert )
    {
        // Opt-in: off unless the file says on, and a damaged file fails closed with a message.
        assert( !SharedPlaySetting.Parse( null, out string e1 ) && e1 == null, "play_setting_absent_is_off" );
        assert( !SharedPlaySetting.Parse( "  \n# only a comment\n", out string e2 ) && e2 == null, "play_setting_blank_is_off" );
        assert( SharedPlaySetting.Parse( SharedPlaySetting.Format( true ), out string e3 ) && e3 == null, "play_setting_on_round_trip" );
        assert( !SharedPlaySetting.Parse( SharedPlaySetting.Format( false ), out string e4 ) && e4 == null, "play_setting_off_round_trip" );
        assert( !SharedPlaySetting.Parse( "yes please\n", out string e5 ) && e5 != null && e5.Contains( SharedPlaySetting.FileName ), "play_setting_unknown_value_stays_off_and_says_why" );
        assert( SharedPlaySetting.Parse( "ON\r\n", out _ ), "play_setting_is_case_insensitive_and_reads_crlf" );

        // Spectating pauses source sync like playing does, and changes nothing when there is no remote run.
        assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.Source, true, true ) == PlaySpectateDecision.Normal, "play_decide_default_is_unchanged" );
        assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.Source, true, true, spectatingRemoteRun: true ) == PlaySpectateDecision.PausedForPlay, "play_decide_spectating_a_remote_run_pauses_source_sync" );
        assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.RuntimeBlocked, true, true, spectatingRemoteRun: true ) == PlaySpectateDecision.FailStop, "play_decide_a_hotload_interrupted_runtime_still_fails_stop" );
        assert( PlaySpectateRules.Decide( true, PlayModeLifecycleState.Source, false, true, spectatingRemoteRun: true ) == PlaySpectateRules.Decide( true, PlayModeLifecycleState.Source, false, true ), "play_decide_a_local_run_keeps_its_own_rules" );

        // The source fence. Two rules over the library's own source text:
        //  (1) no file of the library names the game's networking API at all (the library must not change a project's networking mode, open a
        //      lobby for itself, make a spectator join one, or spawn a player), and
        //  (2) the shared play files open no connection of their own: they ride the collaboration connection that already exists.
        string root = RepoRoot();
        string library = Path.Combine( root, "Libraries", "collaboration" );
        var all = Directory.EnumerateFiles( library, "*.cs", SearchOption.AllDirectories ).ToList();
        assert( all.Count > 50, "play_fence_found_the_library_sources: " + all.Count );
        // (A GameObject's NetworkMode property, which SoloScript writes into its throw-away test scene as data, is not an API call and is not listed.)
        string[] gameNetworking = { "Networking.", "NetworkHelper", "CreateLobby", "GameNetworkSystem", "NetworkSpawn", "Connection.Host", "LobbyConfig" };
        foreach ( string file in all )
        {
            string text = File.ReadAllText( file );
            foreach ( string token in gameNetworking )
            {
                assert( !text.Contains( token, StringComparison.Ordinal ), "play_fence_" + Path.GetFileName( file ) + "_does_not_reference_" + token );
            }
        }

        var playFiles = all.Where( f =>
        {
            string name = Path.GetFileName( f );
            return name.StartsWith( "Play", StringComparison.Ordinal ) && name != "PlayModeLifecycle.cs" && !name.StartsWith( "PlaySpectate", StringComparison.Ordinal )
                || name.StartsWith( "Spectat", StringComparison.Ordinal ) || name.StartsWith( "SharedPlay", StringComparison.Ordinal ) || name == "FreeFlyCamera.cs" || name == "SessionServer.Play.cs" || name == "RuntimeSceneAdapter.cs";
        } ).ToList();
        string[] own = { "Connect(", "TcpClient", "TcpListener", "UdpClient", "HttpClient", "Http.", "WebSocket", "Socket(" };
        string[] expected = { "PlayRoster.cs", "PlaySampler.cs", "PlayMirror.cs", "PlayModel.cs", "PlayRelayPolicy.cs", "SpectateWhitelist.cs", "FreeFlyCamera.cs", "SharedPlaySetting.cs", "SessionServer.Play.cs" };
        assert( expected.All( e => playFiles.Any( f => Path.GetFileName( f ) == e ) ), "play_fence_covers_every_shared_play_file" );
        foreach ( string file in playFiles )
        {
            string text = File.ReadAllText( file );
            foreach ( string token in own )
            {
                assert( !text.Contains( token, StringComparison.Ordinal ), "play_fence_" + Path.GetFileName( file ) + "_opens_no_connection_of_its_own_" + token );
            }
        }

        // The Core pieces stay engine-independent (the test project compiles them without the engine, so this is already enforced; pin the intent).
        foreach ( string file in playFiles.Where( f => f.Contains( Path.DirectorySeparatorChar + "Core" + Path.DirectorySeparatorChar ) ) )
        {
            string text = File.ReadAllText( file );
            assert( !text.Contains( "using Sandbox", StringComparison.Ordinal ) && !text.Contains( "using Editor", StringComparison.Ordinal ), "play_fence_" + Path.GetFileName( file ) + "_is_engine_independent" );
        }
    }
}
