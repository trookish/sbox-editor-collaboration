using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>The player-side sampler: what a run's scene turns into on the wire, frame by frame.</summary>
static partial class SharedPlayTests
{
    const string G1 = "11111111-1111-1111-1111-111111111111";

    static PlayObject Obj( string id, string parent = null, float x = 0, float y = 0, float z = 0, bool enabled = true, JsonArray components = null, string name = null ) =>
        new PlayObject( id, parent, name ?? id, new Vec3( x, y, z ), Quat.Identity, new Vec3( 1, 1, 1 ), enabled, components ?? new JsonArray() );

    static string GuidN( int n ) => new Guid( n, 0, 0, new byte[8] ).ToString();

    static JsonObject Renderer( string guid, string model = "models/a.vmdl", string tint = "1,1,1,1" ) => JsonNode.Parse(
        "{\"__type\":\"Sandbox.ModelRenderer\",\"__guid\":\"" + guid + "\",\"__enabled\":true,\"Model\":\"" + model + "\",\"Tint\":\"" + tint + "\",\"OnComponentStart\":\"x\"}" )!.AsObject();

    static List<PlayObject> Scene( int count, Func<int, float> x = null ) =>
        Enumerable.Range( 0, count ).Select( i => Obj( GuidN( i + 1 ), null, x?.Invoke( i ) ?? i, 0, 0 ) ).ToList();

    static string Text( PlayFrame frame ) => frame.ToJson().ToJsonString();

    static void Sampler( Action<bool, string> assert )
    {
        // The first sample of a run is a keyframe holding everything, parents before children whatever order the scene lists them in.
        var sampler = new PlaySampler();
        var child = Obj( GuidN( 2 ), GuidN( 1 ), 1, 0, 0 );
        var parent = Obj( GuidN( 1 ), null, 0, 0, 0 );
        var grand = Obj( GuidN( 3 ), GuidN( 2 ), 2, 0, 0 );
        var first = sampler.Sample( new[] { grand, child, parent }, 0 );
        assert( first.Count == 1 && first[0].Key && first[0].Created.Count == 3, "play_sampler_first_sample_is_a_keyframe_with_everything" );
        var order = first[0].Created.Select( c => (string)c["u"] ).ToList();
        assert( order.IndexOf( GuidN( 1 ) ) < order.IndexOf( GuidN( 2 ) ) && order.IndexOf( GuidN( 2 ) ) < order.IndexOf( GuidN( 3 ) ), "play_sampler_created_lists_parents_before_children" );
        assert( first[0].Seq == 1 && sampler.Sample( new[] { grand, child, parent }, 50 ).Count == 0, "play_sampler_an_unchanged_scene_sends_nothing" );

        // Moved: threshold, rounding, and only the parts that changed.
        sampler = new PlaySampler();
        sampler.Sample( new[] { Obj( G1, null, 10, 0, 0 ) }, 0 );
        assert( sampler.Sample( new[] { Obj( G1, null, 10.0004f, 0, 0 ) }, 50 ).Count == 0, "play_sampler_move_below_threshold_is_skipped" );
        var moved = sampler.Sample( new[] { Obj( G1, null, 11.23456f, 0, 0 ) }, 100 );
        assert( moved.Count == 1 && moved[0].Moved.Count == 1 && !moved[0].Key, "play_sampler_move_over_threshold_is_sent" );
        var entry = moved[0].Moved[0].AsArray();
        assert( (int)entry[1] == 1 && entry.Count == 5 && Math.Abs( (double)entry[2] - 11.235 ) < 1e-9, "play_sampler_position_is_rounded_to_three_decimals_and_alone: " + entry.ToJsonString() );
        var rot = new PlayObject( G1, null, G1, new Vec3( 11.235f, 0, 0 ), new Quat( 0.123456f, 0, 0, 0.992349f ), new Vec3( 1, 1, 1 ), true, new JsonArray() );
        var rotated = sampler.Sample( new[] { rot }, 150 );
        var rotEntry = rotated[0].Moved[0].AsArray();
        assert( (int)rotEntry[1] == 2 && rotEntry.Count == 6 && Math.Abs( (double)rotEntry[2] - 0.1235 ) < 1e-9, "play_sampler_rotation_is_rounded_to_four_decimals: " + rotEntry.ToJsonString() );
        var scaled = sampler.Sample( new[] { rot with { Scale = new Vec3( 2, 2, 2 ) } }, 200 );
        assert( (int)scaled[0].Moved[0][1] == 4 && scaled[0].Moved[0].AsArray().Count == 5, "play_sampler_scale_change_sends_scale_alone" );
        var all = sampler.Sample( new[] { rot with { Position = new Vec3( 1, 2, 3 ), Rotation = Quat.Identity, Scale = new Vec3( 3, 3, 3 ) } }, 250 );
        assert( (int)all[0].Moved[0][1] == 7 && all[0].Moved[0].AsArray().Count == 12, "play_sampler_all_three_parts_in_one_entry" );

        // Created, destroyed and enabled.
        sampler = new PlaySampler();
        sampler.Sample( new[] { Obj( GuidN( 1 ) ), Obj( GuidN( 2 ) ) }, 0 );
        var added = sampler.Sample( new[] { Obj( GuidN( 1 ) ), Obj( GuidN( 2 ) ), Obj( GuidN( 3 ), GuidN( 1 ), 5, 6, 7 ) }, 50 );
        assert( added.Count == 1 && added[0].Created.Count == 1 && (string)added[0].Created[0]["u"] == GuidN( 3 ) && added[0].Moved.Count == 0, "play_sampler_new_object_is_created_not_moved" );
        var removed = sampler.Sample( new[] { Obj( GuidN( 1 ) ), Obj( GuidN( 2 ) ) }, 100 );
        assert( removed.Count == 1 && removed[0].Destroyed.Count == 1, "play_sampler_vanished_object_is_destroyed" );
        var again = sampler.Sample( new[] { Obj( GuidN( 1 ) ), Obj( GuidN( 2 ) ), Obj( GuidN( 3 ), GuidN( 1 ), 5, 6, 7 ) }, 150 );
        assert( again[0].Created.Count == 1 && (int)again[0].Created[0]["i"] > (int)added[0].Created[0]["i"], "play_sampler_indices_are_never_reused_in_a_run" );
        var disabled = sampler.Sample( new[] { Obj( GuidN( 1 ), enabled: false ), Obj( GuidN( 2 ) ), Obj( GuidN( 3 ), GuidN( 1 ), 5, 6, 7 ) }, 200 );
        assert( disabled[0].Enabled.Count == 1 && (bool)disabled[0].Enabled[0][1] == false, "play_sampler_enabled_change_is_sent" );

        // Components: the whitelist runs on the sender. A script component never reaches a frame, and a property change sends that property alone.
        sampler = new PlaySampler();
        Func<JsonObject> evil = () => JsonNode.Parse( "{\"__type\":\"MyGame.Evil\",\"__guid\":\"" + GuidN( 90 ) + "\",\"Secret\":\"leak\"}" )!.AsObject();
        var camera = JsonNode.Parse( "{\"__type\":\"Sandbox.CameraComponent\",\"__guid\":\"" + GuidN( 91 ) + "\"}" )!.AsObject();
        var key = sampler.Sample( new[] { Obj( G1, components: new JsonArray( Renderer( GuidN( 50 ) ), evil(), camera ) ) }, 0 );
        string keyText = Text( key[0] );
        assert( keyText.Contains( "Sandbox.ModelRenderer" ) && !keyText.Contains( "MyGame.Evil" ) && !keyText.Contains( "leak" ) && !keyText.Contains( "CameraComponent" ) && !keyText.Contains( "OnComponentStart" ), "play_sampler_non_whitelisted_components_never_reach_a_frame" );
        var tint = sampler.Sample( new[] { Obj( G1, components: new JsonArray( Renderer( GuidN( 50 ), tint: "1,0,0,1" ), evil() ) ) }, 50 );
        assert( tint.Count == 1 && tint[0].Changed.Count == 1 && !Text( tint[0] ).Contains( "models/a.vmdl" ) && Text( tint[0] ).Contains( "1,0,0,1" ), "play_sampler_property_change_sends_only_that_property" );
        var dropped = sampler.Sample( new[] { Obj( G1 ) }, 100 );
        assert( dropped.Count == 1 && dropped[0].RemovedComponents.Count == 1, "play_sampler_removed_component_is_sent" );
        var added2 = sampler.Sample( new[] { Obj( G1, components: new JsonArray( Renderer( GuidN( 51 ) ) ) ) }, 150 );
        assert( added2.Count == 1 && added2[0].Changed.Count == 1 && Text( added2[0] ).Contains( "Sandbox.ModelRenderer" ), "play_sampler_added_component_is_sent_with_its_type" );

        // Bounds: 500 moving objects are sent 200 at a time, round robin, and every one arrives before any repeats.
        sampler = new PlaySampler();
        sampler.Sample( Scene( 500 ), 0 );
        var seen = new Dictionary<int, int>();
        var roundOne = new HashSet<int>();
        var roundTwo = new HashSet<int>();
        for ( int round = 1; round <= 3; round++ )
        {
            foreach ( var frame in sampler.Sample( Scene( 500, i => i + round * 10 ), round * 50 ) )
            {
                assert( frame.Moved.Count <= PlaySampler.MaxMovedPerFrame, "play_sampler_moved_per_frame_is_bounded" );
                foreach ( var m in frame.Moved )
                {
                    seen[(int)m[0]] = seen.GetValueOrDefault( (int)m[0] ) + 1;
                    if ( round == 1 ) roundOne.Add( (int)m[0] );
                    if ( round == 2 ) roundTwo.Add( (int)m[0] );
                }
            }
        }
        // Every object moves in every round, so the 500 do not fit in one frame; the 200 sent last time are the youngest and wait their turn.
        // Rounds 1 and 2 therefore send 400 DIFFERENT objects, and round 3 reaches the last 100 before it repeats any.
        assert( seen.Count == 500, "play_sampler_500_moved_objects_are_all_reached_within_three_frames: " + seen.Count );
        assert( roundOne.Count == 200 && roundTwo.Count == 200 && !roundOne.Overlaps( roundTwo ), "play_sampler_round_robin_sends_different_objects_each_frame" );
        sampler = new PlaySampler();
        sampler.Sample( Scene( 500 ), 0 );
        sampler.Sample( Scene( 500, i => i + 10 ), 50 );
        sampler.Sample( Scene( 500, i => i + 10 ), 100 );
        sampler.Sample( Scene( 500, i => i + 10 ), 150 );
        assert( sampler.Sample( Scene( 500, i => i + 10 ), 200 ).Sum( f => f.Moved.Count ) == 0, "play_sampler_nothing_is_resent_once_everything_arrived" );

        // A frame never exceeds the size bound: a flood of large created objects spills into following frames, created before moved.
        sampler = new PlaySampler();
        var big = Enumerable.Range( 0, 40 ).Select( i => Obj( GuidN( i + 1 ), components: new JsonArray( Renderer( GuidN( 1000 + i ), model: "models/" + new string( 'm', 30000 ) + ".vmdl" ) ) ) ).ToList();
        var frames = sampler.Sample( big, 0 );
        assert( frames.Count > 1 && frames.All( f => Text( f ).Length <= PlaySampler.MaxFrameBytes ), "play_sampler_frames_never_exceed_the_size_bound" );
        assert( frames[0].Key && frames.Skip( 1 ).All( f => !f.Key ) && frames.Sum( f => f.Created.Count ) == 40, "play_sampler_oversized_keyframe_is_split_and_complete" );
        var seqs = frames.Select( f => f.Seq ).ToList();
        assert( seqs.SequenceEqual( Enumerable.Range( 1, seqs.Count ).Select( n => (long)n ) ), "play_sampler_frame_sequence_is_consecutive" );
        var enormous = Obj( GuidN( 7 ), components: new JsonArray( Renderer( GuidN( 8 ), model: new string( 'z', 70000 ) ) ) );
        var guarded = new PlaySampler().Sample( new[] { enormous }, 0 );
        assert( guarded.Count == 1 && guarded[0].Created.Count == 1 && Text( guarded[0] ).Length <= PlaySampler.MaxFrameBytes && !Text( guarded[0] ).Contains( "zzzz" ), "play_sampler_an_unbounded_property_is_dropped_not_sent" );

        // Keyframe cadence: every 5 s, and on request, and a request is honoured once.
        sampler = new PlaySampler();
        sampler.Sample( Scene( 3 ), 0 );
        assert( sampler.Sample( Scene( 3 ), 4999 ).Count == 0, "play_sampler_no_keyframe_before_the_interval" );
        var cadence = sampler.Sample( Scene( 3 ), 5000 );
        assert( cadence.Count == 1 && cadence[0].Key && cadence[0].Created.Count == 3, "play_sampler_keyframe_every_five_seconds" );
        sampler.RequestKeyframe();
        var requested = sampler.Sample( Scene( 3 ), 5100 );
        assert( requested.Count == 1 && requested[0].Key && sampler.Sample( Scene( 3 ), 5150 ).Count == 0, "play_sampler_requested_keyframe_is_sent_once" );
        assert( sampler.Sample( Scene( 3 ), 9999 ).Count == 0 && sampler.Sample( Scene( 3 ), 10100 )[0].Key, "play_sampler_cadence_restarts_after_a_keyframe" );

        // Hostile input from the engine side: NaN positions and duplicate ids do not throw or produce non-finite numbers.
        sampler = new PlaySampler();
        var weird = sampler.Sample( new[] { Obj( G1, x: float.NaN ), Obj( G1, x: 5 ), Obj( null ) }, 0 );
        assert( weird.Count == 1 && weird[0].Created.Count == 1 && !Text( weird[0] ).Contains( "NaN" ), "play_sampler_duplicate_nan_and_null_ids_are_tolerated" );
    }
}
