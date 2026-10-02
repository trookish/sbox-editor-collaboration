using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// Scene-level settings (<c>Scene.SerializeProperties()</c>) are host-authoritative state merged per top-level key, the same
/// later-wins rule as object properties. Any change used to throw in the scan loop and end the session. Covered here: the pure
/// three-way merge, the local-change rule the scan uses, the host authority (merge, clash notice, idempotency, size bound,
/// journal replay), a real <see cref="SessionServer"/> with two fake members, and the drift digest's coverage of settings.
/// </summary>
static class SettingsSyncTests
{
    const string Scene = "scenes/settings.scene";

    // Copied verbatim from the installed sample templates/game.playercontroller/Assets/scenes/basic_example.scene, lines 1465-1494
    // (the "SceneProperties" object), so the tests merge what the engine really writes rather than an invented shape.
    const string SampleSceneProperties = """
    {
        "NetworkInterpolation": true,
        "TimeScale": 1,
        "WantsSystemScene": true,
        "Metadata": {},
        "NavMesh": {
          "Enabled": false,
          "IncludeStaticBodies": true,
          "IncludeKeyframedBodies": true,
          "EditorAutoUpdate": false,
          "AgentHeight": 64,
          "AgentRadius": 16,
          "AgentStepSize": 18,
          "AgentMaxSlope": 40,
          "ExcludedBodies": "",
          "IncludedBodies": "",
          "DeferGeneration": false,
          "CustomBounds": false
        },
        "GameObjectSystems": {
          "Sandbox.MovieMaker.MovieRecorderSystem": {
            "DisableClientRecording": false
          },
          "Sandbox.Clutter.ClutterGridSystem": {
            "Storage": {
              "$blob": "bab54360-3186-4d3a-9f92-e9544090f36d"
            }
          }
        }
    }
    """;

    static JsonObject Sample() => JsonNode.Parse( SampleSceneProperties )!.AsObject();

    static JsonObject With( JsonObject settings, string key, JsonNode value )
    {
        var copy = (JsonObject)settings.DeepClone();
        copy[key] = value;
        return copy;
    }

    static JsonObject Without( JsonObject settings, string key )
    {
        var copy = (JsonObject)settings.DeepClone();
        copy.Remove( key );
        return copy;
    }

    static JsonObject WithNavMesh( JsonObject settings, string field, JsonNode value )
    {
        var copy = (JsonObject)settings.DeepClone();
        copy["NavMesh"]![field] = value;
        return copy;
    }

    // Read through the JSON text: a value built in code from an int does not answer GetValue<double>.
    static double Number( JsonObject settings, string key ) => double.Parse( settings[key]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture );

    static double Number( JsonNode node ) => double.Parse( node!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture );

    static ObjectState Cube( int n, string name )
    {
        var id = "00000000-0000-0000-0000-" + n.ToString( "D12" );
        return new ObjectState( id, "", new JsonObject
        {
            ["__guid"] = id,
            ["Name"] = name,
            ["Position"] = "0,0,0",
            ["Rotation"] = "0,0,0,1",
            ["Scale"] = "1,1,1",
            ["Enabled"] = true,
            ["Tags"] = "",
            ["Components"] = new JsonArray()
        } );
    }

    static List<ObjectState> Objects() => new() { Cube( 1, "Floor" ), Cube( 2, "Lamp" ) };

    static Packet Settings( string peer, JsonObject before, JsonObject after, string scene = Scene ) => new()
    {
        Kind = "settings",
        Peer = peer,
        Scene = scene,
        SettingsBefore = (JsonObject)before?.DeepClone(),
        Settings = (JsonObject)after?.DeepClone(),
    };

    static Authority Seeded( out List<Packet> journal, JsonObject settings )
    {
        var written = new List<Packet>();
        journal = written;
        var authority = new Authority( written.Add );
        authority.Subscribe( new Packet { Kind = "subscribe", Scene = Scene, Objects = Objects(), Settings = (JsonObject)settings?.DeepClone() } );
        return authority;
    }

    // A settings object whose serialised text is just over the 1 MiB bound, built from the real sample so only the size is wrong.
    static JsonObject Oversized()
    {
        var big = Sample();
        big["Metadata"] = new JsonObject { ["Notes"] = new string( 'x', SettingsMerge.MaxSettingsBytes ) };
        return big;
    }

    public static void Run( Action<bool, string> assert )
    {
        Merge( assert );
        LocalChange( assert );
        AuthorityRules( assert );
        JournalReplay( assert );
        Digest( assert );
        Routing( assert );
        ServerBehaviour( assert );
    }

    // ---- Client routing --------------------------------------------------------------------------------------------------------------

    // A settings broadcast changes the scene like an accepted edit: it must wait while the user plays (the scene on screen is a copy) and
    // it must pass the same tracked-scene admission, which refuses every kind it does not know.
    static void Routing( Action<bool, string> assert )
    {
        assert( HeldPacketBuffer.IsSceneChange( SettingsMerge.PacketKind ) && !HeldPacketBuffer.IsSceneChange( "presence" ), "settings_routing_is_held_during_play" );
        var target = new object();
        var targets = new Dictionary<string, object> { ["scenes/runtime.scene"] = target };
        var admitted = ScenePacketRouting.RouteTrackedPacket( SettingsMerge.PacketKind, "Scenes\\Runtime.Scene", targets, _ => false, _ => "scenes/runtime.scene", new PlayModeLifecycle( false ), _ => null );
        var missing = ScenePacketRouting.RouteTrackedPacket( SettingsMerge.PacketKind, "scenes/missing.scene", targets, _ => false, _ => "scenes/runtime.scene", new PlayModeLifecycle( false ), _ => null );
        var presence = ScenePacketRouting.RouteTrackedPacket( "presence", "scenes/runtime.scene", targets, _ => false, _ => "scenes/runtime.scene", new PlayModeLifecycle( false ), _ => null );
        assert( admitted.Admitted && admitted.CanonicalSceneKey == "scenes/runtime.scene", "settings_routing_admits_a_tracked_scene: " + admitted.Error );
        assert( !missing.Admitted && !presence.Admitted, "settings_routing_still_refuses_unknown_scenes_and_kinds" );
    }

    // ---- SettingsMerge.TryMerge ------------------------------------------------------------------------------------------------------

    static void Merge( Action<bool, string> assert )
    {
        var original = Sample();

        {
            // The host already holds someone's TimeScale change; this sender changed NetworkInterpolation from the original.
            var current = With( original, "TimeScale", 2 );
            var after = With( original, "NetworkInterpolation", false );
            var ok = SettingsMerge.TryMerge( current, original, after, out var merged, out var clashed );
            assert( ok && Number( merged, "TimeScale" ) == 2 && merged["NetworkInterpolation"]!.GetValue<bool>() == false && clashed.Count == 0,
                "settings_merge_different_keys_both_survive" );
            assert( StateRules.ValuesEqual( merged["NavMesh"], original["NavMesh"] ) && StateRules.ValuesEqual( merged["GameObjectSystems"], original["GameObjectSystems"] ),
                "settings_merge_untouched_keys_keep_the_hosts_value" );
        }

        {
            var current = With( original, "TimeScale", 2 );
            var after = With( original, "TimeScale", 0.5 );
            var ok = SettingsMerge.TryMerge( current, original, after, out var merged, out var clashed );
            assert( ok && Number( merged, "TimeScale" ) == 0.5 && clashed.SequenceEqual( new[] { "TimeScale" } ),
                "settings_merge_same_key_later_wins_and_the_key_is_named: " + ( merged?.ToJsonString() ?? "null" ) + " " + string.Join( ",", clashed ?? new List<string>() ) );
        }

        {
            var current = With( original, "TimeScale", 2 );
            var after = With( original, "TimeScale", 2.0 );
            var ok = SettingsMerge.TryMerge( current, original, after, out var merged, out var clashed );
            assert( ok && Number( merged, "TimeScale" ) == 2 && clashed.Count == 0, "settings_merge_identical_change_is_not_a_clash" );
        }

        {
            // The sender's TimeScale moved only by the engine's 32-bit round trip: that is not a change, so the host's value stays.
            var before = With( original, "TimeScale", JsonNode.Parse( "0.6" ) );
            var after = With( original, "TimeScale", JsonNode.Parse( "0.600000024" ) );
            var current = With( original, "TimeScale", 2 );
            var ok = SettingsMerge.TryMerge( current, before, after, out var merged, out var clashed );
            assert( ok && Number( merged, "TimeScale" ) == 2 && clashed.Count == 0, "settings_merge_float_noise_is_not_a_change" );
        }

        {
            // Two people changed different fields inside NavMesh. A top-level key is one unit: the later NavMesh replaces the earlier
            // one whole, and the clash is reported, rather than stitching an object together that neither person made.
            var current = WithNavMesh( original, "AgentHeight", 72 );
            var after = WithNavMesh( original, "AgentRadius", 24 );
            var ok = SettingsMerge.TryMerge( current, original, after, out var merged, out var clashed );
            assert( ok && StateRules.ValuesEqual( merged["NavMesh"], after["NavMesh"] ) && Number( merged["NavMesh"]!["AgentHeight"] ) == 64 && clashed.SequenceEqual( new[] { "NavMesh" } ),
                "settings_merge_nested_value_is_one_unit" );
        }

        {
            // A key the sender added is added; a key the sender removed is removed, unless the host changed it meanwhile (then later wins, which removes it too).
            var after = Without( With( original, "PhysicsMode", "Default" ), "GameObjectSystems" );
            var ok = SettingsMerge.TryMerge( original, original, after, out var merged, out var clashed );
            assert( ok && merged["PhysicsMode"]!.GetValue<string>() == "Default" && !merged.ContainsKey( "GameObjectSystems" ) && clashed.Count == 0,
                "settings_merge_added_and_removed_keys_follow_the_sender" );
        }

        {
            var current = With( original, "TimeScale", 2 );
            var before = original;
            var after = With( original, "NetworkInterpolation", false );
            var texts = new[] { current, before, after }.Select( x => x.ToJsonString() ).ToArray();
            SettingsMerge.TryMerge( current, before, after, out var merged, out _ );
            var unchanged = new[] { current, before, after }.Select( x => x.ToJsonString() ).SequenceEqual( texts );
            // The result must not share nodes with any input: changing it must leave every input as it was.
            merged["NavMesh"]!["AgentHeight"] = 999;
            merged["TimeScale"] = 7;
            merged["NetworkInterpolation"] = true;
            var stillUnchanged = new[] { current, before, after }.Select( x => x.ToJsonString() ).SequenceEqual( texts );
            assert( unchanged && stillUnchanged, "settings_merge_inputs_are_not_mutated_and_not_shared" );
        }

        {
            var refused = !SettingsMerge.TryMerge( null, original, original, out var m1, out _ )
                && !SettingsMerge.TryMerge( original, null, original, out var m2, out _ )
                && !SettingsMerge.TryMerge( original, original, null, out var m3, out _ );
            assert( refused, "settings_merge_refuses_a_missing_input" );
        }
    }

    // ---- SettingsMerge.LocalChange -----------------------------------------------------------------------------------------------------

    static void LocalChange( Action<bool, string> assert )
    {
        var confirmed = Sample();

        assert( SettingsMerge.LocalChange( confirmed, confirmed, confirmed, (JsonObject)confirmed.DeepClone() ) == null, "settings_local_no_change_sends_nothing" );
        assert( SettingsMerge.LocalChange( confirmed, confirmed, confirmed, With( confirmed, "TimeScale", JsonNode.Parse( "1.00000001" ) ) ) == null, "settings_local_float_noise_sends_nothing" );

        {
            var live = With( confirmed, "TimeScale", 2 );
            var change = SettingsMerge.LocalChange( confirmed, confirmed, confirmed, live );
            assert( change != null && StateRules.ValuesEqual( change, live ), "settings_local_real_change_is_sent_whole" );
        }

        {
            // The host's settings were applied and the engine kept a different NavMesh (it could not take that value). That residue is the
            // engine's, not the user's: it is never sent, otherwise two editors would revert each other forever.
            var echo = WithNavMesh( confirmed, "AgentHeight", 80 );
            assert( SettingsMerge.LocalChange( confirmed, confirmed, echo, (JsonObject)echo.DeepClone() ) == null, "settings_local_engine_residue_is_not_a_change" );

            var live = With( echo, "TimeScale", 2 );
            var change = SettingsMerge.LocalChange( confirmed, confirmed, echo, live );
            assert( change != null && Number( change, "TimeScale" ) == 2 && StateRules.ValuesEqual( change["NavMesh"], confirmed["NavMesh"] ),
                "settings_local_change_carries_the_users_key_and_the_hosts_value_for_residue" );

            // The user then edits the very key the engine refused: that is a change again, and it is sent.
            var edited = WithNavMesh( echo, "AgentHeight", 96 );
            var resent = SettingsMerge.LocalChange( confirmed, confirmed, echo, edited );
            assert( resent != null && Number( resent["NavMesh"]!["AgentHeight"] ) == 96, "settings_local_edit_of_a_residue_key_is_sent" );
        }

        {
            // A collaborator's settings arrived while this editor had an unsent change, so the applied settings were the host's with the
            // local change on top, and the engine took all of it. The local change is still unsent and must still go out: it is not residue.
            var applied = With( confirmed, "TimeScale", 2 );
            var change = SettingsMerge.LocalChange( confirmed, applied, (JsonObject)applied.DeepClone(), (JsonObject)applied.DeepClone() );
            assert( change != null && Number( change, "TimeScale" ) == 2, "settings_local_unsent_change_applied_on_top_is_still_sent" );
        }

        {
            // Nothing applied yet (no applied or echo): every difference from the confirmed settings is the user's.
            var live = WithNavMesh( confirmed, "AgentRadius", 24 );
            var change = SettingsMerge.LocalChange( confirmed, null, null, live );
            assert( change != null && StateRules.ValuesEqual( change, live ), "settings_local_without_echo_every_difference_counts" );
        }
    }

    // ---- Authority ---------------------------------------------------------------------------------------------------------------------

    static void AuthorityRules( Action<bool, string> assert )
    {
        var original = Sample();

        {
            var authority = Seeded( out var journal, original );
            var snapshot = authority.Subscribe( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState>(), Settings = With( original, "TimeScale", 4 ) } );
            assert( StateRules.ValuesEqual( authority.SceneSettings[Scene], original ), "settings_authority_first_subscribe_initialises_the_scenes_settings" );
            assert( snapshot.Kind == "snapshot" && StateRules.ValuesEqual( snapshot.Settings, original ), "settings_authority_snapshot_carries_the_hosts_settings_not_the_later_subscribers" );
            assert( journal.Count == 1 && journal[0].Kind == "seed" && StateRules.ValuesEqual( journal[0].Settings, original ), "settings_authority_seed_is_journaled_with_settings" );
            snapshot.Settings["TimeScale"] = 99;
            assert( Number( authority.SceneSettings[Scene], "TimeScale" ) == 1, "settings_authority_snapshot_does_not_alias_the_hosts_settings" );
        }

        {
            var authority = Seeded( out var journal, original );
            var a = authority.ApplySettings( Settings( "a", original, With( original, "TimeScale", 2 ) ) );
            var b = authority.ApplySettings( Settings( "b", original, With( original, "NetworkInterpolation", false ) ) );
            var host = authority.SceneSettings[Scene];
            assert( a.Kind == "settings" && b.Kind == "settings" && Number( host, "TimeScale" ) == 2 && host["NetworkInterpolation"]!.GetValue<bool>() == false,
                "settings_authority_concurrent_changes_to_different_keys_both_survive: " + a.Kind + " " + a.Error + " " + b.Kind + " " + b.Error );
            assert( a.Revision == 1 && b.Revision == 2 && authority.Revision == 2 && a.Peer == "a" && b.Peer == "b" && b.Scene == Scene && b.Notice == null,
                "settings_authority_each_change_advances_the_revision_and_names_its_sender" );
            assert( StateRules.ValuesEqual( b.Settings, host ) && b.Merged && !a.Merged, "settings_authority_broadcast_carries_the_merged_settings_and_flags_a_merge" );
            assert( journal.Count == 3 && ReferenceEquals( journal[2], b ), "settings_authority_change_is_journaled_as_broadcast" );
        }

        {
            var authority = Seeded( out _, original );
            authority.ApplySettings( Settings( "a", original, With( original, "TimeScale", 2 ) ) );
            var later = authority.ApplySettings( Settings( "b", original, With( original, "TimeScale", 0.5 ) ) );
            assert( later.Kind == "settings" && Number( authority.SceneSettings[Scene], "TimeScale" ) == 0.5,
                "settings_authority_same_key_clash_later_wins: " + later.Kind + " " + later.Error );
            assert( later.Notice == "Concurrent changes to the scene settings were combined; the later change was kept for: TimeScale." && later.Merged,
                "settings_authority_clash_notice_names_the_key: " + later.Notice );
        }

        {
            var authority = Seeded( out var journal, original );
            var packet = Settings( "a", original, With( original, "TimeScale", 2 ) );
            var first = authority.ApplySettings( packet );
            var count = journal.Count;
            // A resend after a reconnect: the host must answer with the same receipt, not apply it a second time.
            var resend = Settings( "a", original, With( original, "TimeScale", 2 ) );
            resend.Id = packet.Id;
            var again = authority.ApplySettings( resend );
            assert( ReferenceEquals( first, again ) && authority.Revision == 1 && journal.Count == count, "settings_authority_resent_packet_is_idempotent" );
        }

        {
            var authority = Seeded( out var journal, original );
            var count = journal.Count;
            var refused = authority.ApplySettings( Settings( "a", original, Oversized() ) );
            assert( refused.Kind == "reject" && refused.Scene == Scene && refused.Error != null && refused.Error.Contains( "1048576" ),
                "settings_authority_oversize_is_refused_with_a_formal_reason: " + refused.Kind + " " + refused.Error );
            assert( authority.Revision == 0 && journal.Count == count && StateRules.ValuesEqual( authority.SceneSettings[Scene], original ),
                "settings_authority_oversize_changes_neither_state_nor_revision" );
        }

        {
            var authority = Seeded( out _, original );
            var noScene = authority.ApplySettings( Settings( "a", original, original, "scenes/never.scene" ) );
            var noBefore = authority.ApplySettings( Settings( "a", null, original ) );
            var noAfter = authority.ApplySettings( Settings( "a", original, null ) );
            assert( noScene.Kind == "reject" && noBefore.Kind == "reject" && noAfter.Kind == "reject" && authority.Revision == 0,
                "settings_authority_malformed_packets_are_refused: " + noScene.Error + " | " + noBefore.Error + " | " + noAfter.Error );
        }

        {
            // A scene whose first subscriber carried no settings: the first change is adopted whole, since there is nothing to merge with.
            var authority = Seeded( out _, null );
            var adopted = authority.ApplySettings( Settings( "a", original, With( original, "TimeScale", 2 ) ) );
            assert( adopted.Kind == "settings" && Number( authority.SceneSettings[Scene], "TimeScale" ) == 2, "settings_authority_scene_without_settings_adopts_the_first_change" );
        }
    }

    // ---- Journal replay ----------------------------------------------------------------------------------------------------------------

    static void JournalReplay( Action<bool, string> assert )
    {
        var original = Sample();
        var authority = Seeded( out var journal, original );
        authority.ApplySettings( Settings( "a", original, With( original, "TimeScale", 2 ) ) );
        authority.ApplySettings( Settings( "b", original, WithNavMesh( original, "Enabled", true ) ) );
        authority.ApplySettings( Settings( "c", original, With( original, "TimeScale", 0.5 ) ) );
        authority.Apply( new Packet { Kind = "edit", Peer = "d", Scene = Scene, Final = true, Changes = new List<Change> { new( Cube( 2, "Lamp" ).Id, Cube( 2, "Lamp" ), Cube( 2, "Lamp moved" ) ) } } );
        authority.ApplySettings( Settings( "e", original, Oversized() ) );

        // The host's journal is one JSON packet per line; a recovery replays the seed, then each accepted edit and each settings change in order.
        JsonObject settings = null;
        var objects = new Dictionary<string, ObjectState>();
        foreach ( var line in journal.Select( x => JsonSerializer.Serialize( x ) ) )
        {
            var packet = JsonSerializer.Deserialize<Packet>( line );
            if ( packet.Kind == "seed" )
            {
                objects = StateRules.Map( packet.Objects );
                settings = packet.Settings;
            }
            else if ( packet.Kind == "accepted" )
            {
                foreach ( var change in packet.Changes )
                {
                    objects[change.Id] = change.After;
                }
            }
            else if ( packet.Kind == "settings" )
            {
                settings = packet.Settings;
            }
        }
        assert( journal.Count( x => x.Kind == "settings" ) == 3, "settings_journal_holds_one_line_per_accepted_settings_change_and_none_for_a_refusal" );
        assert( StateRules.ValuesEqual( settings, authority.SceneSettings[Scene] ) && Number( settings, "TimeScale" ) == 0.5 && settings["NavMesh"]!["Enabled"]!.GetValue<bool>(),
            "settings_journal_replay_reproduces_the_hosts_settings" );
        assert( StateRules.Diff( objects, authority.Scenes[Scene] ).Count == 0, "settings_journal_replay_still_reproduces_the_objects" );
    }

    // ---- Digest ------------------------------------------------------------------------------------------------------------------------

    static void Digest( Action<bool, string> assert )
    {
        var scene = StateRules.Map( Objects() );
        var original = Sample();
        assert( SceneDigest.Compute( scene, null ) == SceneDigest.Compute( scene ), "settings_digest_without_settings_is_the_object_digest" );
        assert( SceneDigest.Compute( scene, original ) != SceneDigest.Compute( scene ), "settings_digest_with_settings_differs_from_objects_alone" );
        assert( SceneDigest.Compute( scene, With( original, "TimeScale", 2 ) ) != SceneDigest.Compute( scene, original ), "settings_digest_a_settings_only_difference_changes_the_digest" );
        assert( SceneDigest.Compute( scene, With( original, "TimeScale", JsonNode.Parse( "0.6" ) ) ) == SceneDigest.Compute( scene, With( original, "TimeScale", JsonNode.Parse( "0.600000024" ) ) ),
            "settings_digest_ignores_float_noise_in_settings" );
        var reordered = new JsonObject();
        foreach ( var pair in original.Reverse() ) reordered[pair.Key] = pair.Value.DeepClone();
        assert( SceneDigest.Compute( scene, reordered ) == SceneDigest.Compute( scene, original ), "settings_digest_ignores_key_order" );
    }

    // ---- A real SessionServer with two fake members -------------------------------------------------------------------------------------

    sealed class Rig : IDisposable
    {
        public SessionServer Server;
        public Wire A;
        public Wire B;
        public List<Packet> SeenA = new();
        public List<Packet> SeenB = new();
        public string Directory;

        public void Dispose()
        {
            A?.Dispose();
            B?.Dispose();
            Server?.Dispose();
            try { if ( System.IO.Directory.Exists( Directory ) ) System.IO.Directory.Delete( Directory, true ); }
            catch ( IOException ) { }
        }

        public void PumpFor( Func<bool> done, int milliseconds = 10000 )
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                Server.Pump();
                while ( A != null && A.Incoming.TryDequeue( out var a ) ) SeenA.Add( a );
                while ( B != null && B.Incoming.TryDequeue( out var b ) ) SeenB.Add( b );
                if ( done() ) return;
                Thread.Sleep( 5 );
            }
            throw new TimeoutException( "The fake members did not receive the expected packets from the host." );
        }

        // The wire is ordered and the host answers in order, so once a member's sentinel pong arrives everything the host sent that
        // member before it has arrived too.
        public void Settle()
        {
            var a = SeenA.Count( p => p.Kind == "pong" ) + 1;
            var b = SeenB.Count( p => p.Kind == "pong" ) + 1;
            A.Send( new Packet { Kind = "ping" } );
            B.Send( new Packet { Kind = "ping" } );
            PumpFor( () => SeenA.Count( p => p.Kind == "pong" ) >= a && SeenB.Count( p => p.Kind == "pong" ) >= b );
        }
    }

    static int FreePort()
    {
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        var port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        listener.Stop();
        return port;
    }

    static Wire Join( int port, string name, string steamId )
    {
        var wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult();
        wire.Send( new Packet
        {
            Kind = "hello", Peer = Guid.NewGuid().ToString(), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
            Name = name, Token = "test-token", Project = "test-project", Baseline = "test-baseline",
            SteamId = steamId, SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
        } );
        return wire;
    }

    static Rig Start( JsonObject settings )
    {
        var rig = new Rig();
        var root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        rig.Directory = Path.Combine( root, "teamcreate-coretests", Guid.NewGuid().ToString( "N" ) );
        Exception last = null;
        var port = 0;
        for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
        {
            try
            {
                port = FreePort();
                rig.Server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", Path.Combine( rig.Directory, $"journal-{attempt}.jsonl" ) );
            }
            catch ( SocketException error ) { last = error; }
        }
        if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the settings tests.", last );
        rig.Server.AllowUnverifiedPeers = true;
        rig.A = Join( port, "Alpha", "76561198000000001" );
        rig.PumpFor( () => rig.SeenA.Any( p => p.Kind == "welcome" ) );
        rig.B = Join( port, "Bravo", "76561198000000002" );
        rig.PumpFor( () => rig.SeenB.Any( p => p.Kind == "welcome" ) );
        rig.A.Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = Objects(), Settings = (JsonObject)settings?.DeepClone() } );
        rig.PumpFor( () => rig.SeenA.Any( p => p.Kind == "snapshot" ) );
        rig.B.Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = Objects(), Settings = (JsonObject)settings?.DeepClone() } );
        rig.PumpFor( () => rig.SeenB.Any( p => p.Kind == "snapshot" ) );
        rig.SeenA.Clear();
        rig.SeenB.Clear();
        return rig;
    }

    // Sends a ping carrying a digest, then a plain sentinel ping, and returns what member A received up to the sentinel's pong.
    static List<Packet> Ping( Rig rig, string digest, long revision )
    {
        var before = rig.SeenA.Count;
        var expected = rig.SeenA.Count( p => p.Kind == "pong" ) + 2;
        rig.A.Send( new Packet { Kind = "ping", Digest = digest, DigestRevision = revision, DigestScene = Scene } );
        rig.A.Send( new Packet { Kind = "ping" } );
        rig.PumpFor( () => rig.SeenA.Count( p => p.Kind == "pong" ) >= expected );
        return rig.SeenA.Skip( before ).ToList();
    }

    static void ServerBehaviour( Action<bool, string> assert )
    {
        var original = Sample();

        using ( var rig = Start( original ) )
        {
            rig.A.Send( Settings( null, original, With( original, "TimeScale", 2 ) ) );
            rig.PumpFor( () => rig.SeenA.Any( p => p.Kind == "settings" ) && rig.SeenB.Any( p => p.Kind == "settings" ) );
            var toA = rig.SeenA.First( p => p.Kind == "settings" );
            var toB = rig.SeenB.First( p => p.Kind == "settings" );
            assert( toA.Id == toB.Id && toA.Revision == 1 && toB.Revision == 1 && Number( toB.Settings, "TimeScale" ) == 2 && toA.Peer == toB.Peer && toA.Peer != null,
                "settings_server_change_is_broadcast_to_every_member" );

            rig.SeenA.Clear();
            rig.SeenB.Clear();
            rig.B.Send( Settings( null, original, Oversized() ) );
            rig.PumpFor( () => rig.SeenB.Any( p => p.Kind == "reject" ) );
            rig.Settle();
            assert( rig.SeenB.Count( p => p.Kind == "reject" ) == 1 && !rig.SeenA.Any( p => p.Kind == "reject" || p.Kind == "settings" ),
                "settings_server_reject_goes_only_to_the_sender" );
            assert( !rig.SeenA.Concat( rig.SeenB ).Any( p => p.Kind == "fatal" ), "settings_server_refusal_never_fails_the_session" );
        }

        // Drift: the host's digest covers settings, so a member that agrees on every object but holds other settings is resynchronised,
        // and the snapshot carries the host's settings.
        using ( var rig = Start( original ) )
        {
            var objects = StateRules.Map( Objects() );
            var agreed = SceneDigest.Compute( objects, original );
            var quiet = new List<Packet>();
            for ( int i = 0; i < 3; i++ ) quiet.AddRange( Ping( rig, agreed, 0 ) );
            assert( !quiet.Any( p => p.Kind == "snapshot" ), "settings_server_matching_settings_digest_is_quiet" );

            var drifted = SceneDigest.Compute( objects, With( original, "TimeScale", 2 ) );
            var resync = new List<Packet>();
            for ( int i = 0; i < 2; i++ ) resync.AddRange( Ping( rig, drifted, 0 ) );
            var snapshot = resync.FirstOrDefault( p => p.Kind == "snapshot" );
            assert( snapshot != null && StateRules.ValuesEqual( snapshot.Settings, original ),
                "settings_server_settings_only_drift_resyncs_with_the_hosts_settings" );
        }

        // After a settings change the host's digest moves with the revision: a member still reporting the old settings at the new revision has drifted.
        using ( var rig = Start( original ) )
        {
            rig.A.Send( Settings( null, original, With( original, "TimeScale", 2 ) ) );
            rig.PumpFor( () => rig.SeenA.Any( p => p.Kind == "settings" ) );
            var revision = rig.SeenA.First( p => p.Kind == "settings" ).Revision;
            var objects = StateRules.Map( Objects() );
            var stale = new List<Packet>();
            for ( int i = 0; i < 2; i++ ) stale.AddRange( Ping( rig, SceneDigest.Compute( objects, original ), revision ) );
            var current = new List<Packet>();
            for ( int i = 0; i < 2; i++ ) current.AddRange( Ping( rig, SceneDigest.Compute( objects, With( original, "TimeScale", 2 ) ), revision ) );
            assert( stale.Count( p => p.Kind == "snapshot" ) == 1 && !current.Any( p => p.Kind == "snapshot" ), "settings_server_digest_follows_the_settings_revision" );
        }
    }
}
