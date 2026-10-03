using System.Text.Json;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>The spectator's side: what a frame does to the mirror, what it refuses, and what the wire and the bandwidth look like with the real serialiser.</summary>
static partial class SharedPlayTests
{
    static PlayFrame ThroughWire( PlayFrame frame )
    {
        // The real wire path: a Packet serialised by System.Text.Json exactly as Wire.Send does, read back, and parsed by the receiver's checks.
        var packet = new Packet { Kind = "play-frame", Peer = "player", RuntimeRunId = "run-1", RuntimeSequence = frame.Seq, Play = frame.ToJson() };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes( packet );
        var back = JsonSerializer.Deserialize<Packet>( bytes );
        return PlayFrame.TryParse( back.Play, out PlayFrame parsed, out string error ) ? parsed : throw new Exception( "round trip refused: " + error );
    }

    static void Mirror( Action<bool, string> assert )
    {
        var sampler = new PlaySampler();
        var mirror = new PlayMirror();
        assert( mirror.AwaitingKeyframe && mirror.Objects.Count == 0, "play_mirror_starts_waiting_for_a_keyframe" );

        var scene = new List<PlayObject> { Obj( GuidN( 1 ), null, 1, 2, 3, components: new JsonArray( Renderer( GuidN( 50 ) ) ) ), Obj( GuidN( 2 ), GuidN( 1 ), 4, 5, 6, name: "child" ) };
        var key = sampler.Sample( scene, 0 )[0];
        assert( mirror.Apply( ThroughWire( key ) ) == PlayApplyResult.Applied && !mirror.AwaitingKeyframe && mirror.Objects.Count == 2, "play_mirror_applies_a_keyframe_through_the_wire" );
        var first = mirror.Objects.Values.First( o => o.Guid == GuidN( 1 ) );
        assert( first.Position[0] == 1 && first.Position[1] == 2 && first.Position[2] == 3 && first.Components.Count == 1 && first.Components[GuidN( 50 )].Type == "Sandbox.ModelRenderer", "play_mirror_holds_state_and_component_from_the_keyframe" );
        assert( mirror.Objects.Values.First( o => o.Guid == GuidN( 2 ) ).Name == "child" && mirror.Objects.Values.First( o => o.Guid == GuidN( 2 ) ).Parent == first.Index, "play_mirror_keeps_names_and_parents" );

        // Deltas follow the sender exactly.
        scene = new List<PlayObject> { scene[0] with { Position = new Vec3( 9, 2, 3 ), Enabled = false, Components = new JsonArray( Renderer( GuidN( 50 ), tint: "0,1,0,1" ) ) }, scene[1] };
        var delta = sampler.Sample( scene, 50 )[0];
        assert( mirror.Apply( ThroughWire( delta ) ) == PlayApplyResult.Applied, "play_mirror_applies_a_delta" );
        first = mirror.Objects.Values.First( o => o.Guid == GuidN( 1 ) );
        assert( first.Position[0] == 9 && !first.Enabled && (string)first.Components[GuidN( 50 )].Props["Tint"] == "0,1,0,1" && (string)first.Components[GuidN( 50 )].Props["Model"] == "models/a.vmdl", "play_mirror_delta_moves_disables_and_merges_properties" );
        scene = new List<PlayObject> { scene[0] };
        assert( mirror.Apply( ThroughWire( sampler.Sample( scene, 100 )[0] ) ) == PlayApplyResult.Applied && mirror.Objects.Count == 1, "play_mirror_destroyed_removes_the_object" );

        // Duplicates and gaps: an old or repeated frame changes nothing; a missing one makes the mirror ask for a keyframe and ignore deltas until it arrives.
        var m2 = new PlayMirror();
        var s2 = new PlaySampler();
        var frames = new List<PlayFrame>();
        for ( int i = 0; i < 4; i++ ) frames.AddRange( s2.Sample( new[] { Obj( G1, null, i * 5 ) }, i * 50 ) );
        assert( frames.Count == 4, "play_mirror_four_frames_produced" );
        m2.Apply( frames[0] );
        m2.Apply( frames[1] );
        assert( m2.Apply( frames[1] ) == PlayApplyResult.Ignored && m2.Objects.Values.First().Position[0] == 5, "play_mirror_repeated_frame_is_ignored" );
        assert( m2.Apply( frames[3] ) == PlayApplyResult.NeedsKeyframe && m2.AwaitingKeyframe && m2.Objects.Values.First().Position[0] == 5, "play_mirror_gap_asks_for_a_keyframe_and_applies_nothing" );
        assert( m2.Apply( frames[2] ) == PlayApplyResult.NeedsKeyframe, "play_mirror_deltas_wait_for_the_keyframe" );
        s2.RequestKeyframe();
        var recovery = s2.Sample( new[] { Obj( G1, null, 20 ) }, 300 );
        assert( m2.Apply( recovery[0] ) == PlayApplyResult.Applied && !m2.AwaitingKeyframe && m2.Objects.Values.First().Position[0] == 20, "play_mirror_recovers_on_the_next_keyframe" );

        // A late joiner starts from any keyframe, whatever sequence number it carries.
        var late = new PlayMirror();
        s2.RequestKeyframe();
        assert( late.Apply( s2.Sample( new[] { Obj( G1, null, 21 ) }, 400 )[0] ) == PlayApplyResult.Applied && late.Objects.Count == 1, "play_mirror_late_joiner_starts_on_a_keyframe" );

        // A split keyframe: the mirror is complete only when the last part arrived, and the parts are consecutive frames.
        var bigSampler = new PlaySampler();
        var bigScene = Enumerable.Range( 0, 40 ).Select( i => Obj( GuidN( i + 1 ), components: new JsonArray( Renderer( GuidN( 1000 + i ), model: "models/" + new string( 'm', 30000 ) + ".vmdl" ) ) ) ).ToList();
        var bigMirror = new PlayMirror();
        foreach ( var part in bigSampler.Sample( bigScene, 0 ) ) bigMirror.Apply( ThroughWire( part ) );
        assert( bigMirror.Objects.Count == 40 && !bigMirror.AwaitingKeyframe, "play_mirror_split_keyframe_rebuilds_everything" );

        // Receiver-side refusal. These frames are written by hand, as a hostile editor would, and never pass through the sampler.
        string hostile = "{\"s\":1,\"k\":true,\"c\":[{\"i\":0,\"u\":\"" + GuidN( 1 ) + "\",\"p\":-1,\"n\":\"x\",\"t\":[0,0,0,0,0,0,1,1,1,1],\"k\":["
            + "{\"__type\":\"MyGame.PlayerMovement\",\"__guid\":\"" + GuidN( 60 ) + "\",\"Speed\":9999},"
            + "{\"__type\":\"../../Code/Evil\",\"__guid\":\"" + GuidN( 61 ) + "\"},"
            + "{\"__type\":\"Sandbox.CameraComponent\",\"__guid\":\"" + GuidN( 62 ) + "\"},"
            + "{\"__type\":\"Sandbox.ModelRenderer\",\"__guid\":\"" + GuidN( 63 ) + "\",\"Model\":\"models/a.vmdl\",\"OnComponentStart\":\"evil\",\"MaterialGroup\":{\"__type\":\"MyGame.Evil\"},\"Prefab\":\"prefabs/evil.prefab\"}]}]}";
        assert( PlayFrame.TryParse( JsonNode.Parse( hostile ), out PlayFrame hostileFrame, out string hostileError ) && hostileFrame.Stripped == 3, "play_receiver_counts_every_refused_component: " + hostileError + " " + hostileFrame?.Stripped );
        var victim = new PlayMirror();
        assert( victim.Apply( hostileFrame ) == PlayApplyResult.Applied, "play_receiver_applies_the_rest_of_a_hostile_frame" );
        var comps = victim.Objects[0].Components;
        assert( comps.Count == 1 && comps.ContainsKey( GuidN( 63 ) ), "play_receiver_creates_only_the_whitelisted_component" );
        var props = comps[GuidN( 63 )].Props;
        assert( !props.ContainsKey( "OnComponentStart" ) && !props.ContainsKey( "MaterialGroup" ) && !props.ContainsKey( "Prefab" ) && (string)props["Model"] == "models/a.vmdl", "play_receiver_drops_hooks_prefab_and_nested_type_properties" );
        assert( !hostileFrame.ToJson().ToJsonString().Contains( "PlayerMovement" ) && !hostileFrame.ToJson().ToJsonString().Contains( "Evil" ), "play_receiver_refused_types_are_gone_from_the_parsed_frame" );

        // The mirror does not trust a frame object either: one built in process (never parsed) with a script component still creates nothing.
        var direct = new PlayFrame { Seq = 1, Key = true };
        direct.Created.Add( JsonNode.Parse( "{\"i\":0,\"u\":\"" + GuidN( 1 ) + "\",\"p\":-1,\"n\":\"x\",\"t\":[0,0,0,0,0,0,1,1,1,1],\"k\":[{\"__type\":\"MyGame.Evil\",\"__guid\":\"" + GuidN( 64 ) + "\"},{\"__type\":\"Sandbox.PointLight\",\"__guid\":\"" + GuidN( 65 ) + "\",\"Radius\":300}]}" ) );
        var directMirror = new PlayMirror();
        directMirror.Apply( direct );
        assert( directMirror.Objects[0].Components.Count == 1 && directMirror.Objects[0].Components.ContainsKey( GuidN( 65 ) ), "play_mirror_resanitises_a_frame_that_was_never_parsed" );

        // A change entry cannot introduce a type, or smuggle properties of a type the mirror already holds.
        string change = "{\"s\":2,\"x\":[{\"i\":0,\"g\":\"" + GuidN( 70 ) + "\",\"y\":\"MyGame.Evil\",\"p\":{\"A\":1}},{\"i\":0,\"g\":\"" + GuidN( 63 ) + "\",\"p\":{\"Tint\":\"1,0,0,1\",\"OnComponentUpdate\":\"x\",\"Prefab\":\"p\"}}]}";
        assert( PlayFrame.TryParse( JsonNode.Parse( change ), out PlayFrame changeFrame, out _ ) && changeFrame.Changed.Count == 1 && changeFrame.Stripped == 1, "play_receiver_changed_entry_with_a_foreign_type_is_refused" );
        victim.Apply( changeFrame );
        props = victim.Objects[0].Components[GuidN( 63 )].Props;
        assert( (string)props["Tint"] == "1,0,0,1" && !props.ContainsKey( "OnComponentUpdate" ) && !props.ContainsKey( "Prefab" ), "play_receiver_changed_properties_are_filtered_again_on_apply" );

        // Collision stays forced off on the receiver even when the sender "changes" it back on.
        string meshFrame = "{\"s\":1,\"k\":true,\"c\":[{\"i\":0,\"u\":\"" + GuidN( 1 ) + "\",\"p\":-1,\"n\":\"m\",\"t\":[0,0,0,0,0,0,1,1,1,1],\"k\":[{\"__type\":\"Sandbox.MeshComponent\",\"__guid\":\"" + GuidN( 80 ) + "\",\"Collision\":\"Mesh\"}]}],\"x\":[{\"i\":0,\"g\":\"" + GuidN( 80 ) + "\",\"p\":{\"Collision\":\"Mesh\"}}]}";
        PlayFrame.TryParse( JsonNode.Parse( meshFrame ), out PlayFrame mf, out _ );
        var meshMirror = new PlayMirror();
        meshMirror.Apply( mf );
        assert( (string)meshMirror.Objects[0].Components[GuidN( 80 )].Props["Collision"] == "None", "play_receiver_collision_is_forced_off_on_create_and_change" );

        // Malformed frames are refused whole, with a specific reason, and never throw.
        string[] malformed =
        {
            "[]", "\"x\"", "{}", "{\"s\":-1}", "{\"s\":1,\"c\":[1]}", "{\"s\":1,\"c\":[{\"i\":-5,\"u\":\"" + GuidN( 1 ) + "\",\"t\":[0,0,0,0,0,0,1,1,1,1]}]}",
            "{\"s\":1,\"c\":[{\"i\":0,\"u\":\"not-a-guid\",\"t\":[0,0,0,0,0,0,1,1,1,1]}]}", "{\"s\":1,\"c\":[{\"i\":0,\"u\":\"" + GuidN( 1 ) + "\",\"t\":[0,0]}]}",
            "{\"s\":1,\"m\":[[0,9,1,2,3]]}", "{\"s\":1,\"m\":[[0,1,1,2]]}", "{\"s\":1,\"m\":[[0,1,1,2,\"x\"]]}", "{\"s\":1,\"m\":[[0,1,1,2,1e300]]}",
            "{\"s\":1,\"d\":[99999999999]}", "{\"s\":1,\"e\":[[0]]}", "{\"s\":1,\"r\":[[0,\"zz\"]]}",
            "{\"s\":1,\"m\":" + "[" + string.Join( ",", Enumerable.Repeat( "[0,1,1,2,3]", PlayFrame.MaxEntries + 1 ) ) + "]}",
        };
        foreach ( string text in malformed )
        {
            bool refused = !PlayFrame.TryParse( JsonNode.Parse( text ), out _, out string why ) && !string.IsNullOrEmpty( why );
            assert( refused, "play_receiver_refuses_malformed_frame_" + ( text.Length > 50 ? text.Substring( 0, 50 ) : text ) );
        }
        assert( !PlayFrame.TryParse( null, out _, out _ ), "play_receiver_refuses_a_missing_frame" );

        // Bounds on the mirror itself: a hostile sender cannot grow it without limit.
        var flood = new PlayMirror();
        var floodFrame = new PlayFrame { Seq = 1, Key = true };
        for ( int i = 0; i < PlayFrame.MaxEntries; i++ ) floodFrame.Created.Add( JsonNode.Parse( "{\"i\":" + i + ",\"u\":\"" + GuidN( i + 1 ) + "\",\"p\":-1,\"n\":\"n\",\"t\":[0,0,0,0,0,0,1,1,1,1]}" ) );
        flood.Apply( floodFrame );
        for ( int part = 2; part <= 8; part++ )
        {
            var more = new PlayFrame { Seq = part };
            for ( int i = 0; i < PlayFrame.MaxEntries; i++ ) more.Created.Add( JsonNode.Parse( "{\"i\":" + ( part * 10000 + i ) + ",\"u\":\"" + GuidN( i + 1 ) + "\",\"p\":-1,\"n\":\"n\",\"t\":[0,0,0,0,0,0,1,1,1,1]}" ) );
            flood.Apply( more );
        }
        assert( flood.Objects.Count <= PlayMirror.MaxObjects, "play_mirror_object_count_is_bounded: " + flood.Objects.Count );
    }

    static void Bandwidth( Action<bool, string> assert )
    {
        BandwidthRun( 20, 60, assert );
        BandwidthRun( 50, double.MaxValue, assert );
    }

    static void BandwidthRun( int movers, double limitKiB, Action<bool, string> assert )
    {
        // A typical scene: 300 objects with a renderer each, `movers` of them moving all the time (position and rotation), for ten seconds at 20 Hz,
        // keyframes every 5 s included. The packet is measured as the Wire serialises it, envelope and all.
        var sampler = new PlaySampler();
        var rng = new Random( 7 );
        long bytes = 0, keyBytes = 0;
        int frames = 0;
        double seconds = 10;
        for ( int tick = 0; tick < (int)( seconds * 20 ); tick++ )
        {
            double nowMs = tick * 50.0;
            var scene = new List<PlayObject>();
            for ( int i = 0; i < 300; i++ )
            {
                float x = i * 3f, y = 0, z = 0;
                var rotation = Quat.Identity;
                if ( i < movers )
                {
                    double t = tick / 20.0;
                    x += (float)( Math.Sin( t + i ) * 50 ) + (float)rng.NextDouble() * 0.01f;
                    y = (float)( Math.Cos( t * 0.7 + i ) * 50 );
                    double a = t * 0.5 + i;
                    rotation = new Quat( 0, 0, (float)Math.Sin( a / 2 ), (float)Math.Cos( a / 2 ) );
                }
                scene.Add( new PlayObject( GuidN( i + 1 ), i == 0 ? null : GuidN( 1 ), "Object " + i, new Vec3( x, y, z ), rotation, new Vec3( 1, 1, 1 ), true,
                    new JsonArray( Renderer( GuidN( 5000 + i ), "models/props/crate_" + ( i % 12 ) + ".vmdl" ) ) ) );
            }
            foreach ( PlayFrame frame in sampler.Sample( scene, nowMs ) )
            {
                var packet = new Packet { Kind = "play-frame", Peer = GuidN( 99999 ), RuntimeRunId = "run-1", RuntimeSequence = frame.Seq, Play = frame.ToJson() };
                int size = JsonSerializer.SerializeToUtf8Bytes( packet ).Length;
                bytes += size;
                if ( frame.Key ) keyBytes += size;
                frames++;
                assert( size <= PlaySampler.MaxFrameBytes + 1024, "play_bandwidth_every_packet_is_bounded" );
            }
        }
        double kbPerSecond = bytes / seconds / 1024.0;
        Console.WriteLine( "PLAY-BANDWIDTH: 300 objects, " + movers + " moving, 20 Hz, " + seconds + " s: " + bytes + " bytes in " + frames + " frames = " + kbPerSecond.ToString( "F1" ) + " KiB/s per spectator (keyframes " + ( keyBytes / seconds / 1024.0 ).ToString( "F1" ) + " KiB/s)" );
        assert( kbPerSecond <= limitKiB, "play_bandwidth_300_objects_" + movers + "_moving_stays_within_" + limitKiB + "_KiB_per_second: " + kbPerSecond.ToString( "F1" ) );
    }
}
