using System.Text.Json;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// Property-level merge in the host authority. Two people editing different properties of one object
/// used to produce "Conflict on ...: first accepted edit wins" for the second person; the host now
/// merges per property and reports a clash only when both touched the same property.
/// </summary>
static class PropertyMergeTests
{
    const string Scene = "scenes/test.scene";

    // ---- Builders -------------------------------------------------------------------------------

    static ObjectState Cube( string name = "Cube", string position = "0,0,0", string parent = "", params JsonObject[] components )
    {
        var id = Guid.NewGuid().ToString();
        var list = new JsonArray();
        foreach ( var component in components )
        {
            list.Add( component.DeepClone() );
        }
        return new ObjectState( id, parent, new JsonObject
        {
            ["__guid"] = id,
            ["Name"] = name,
            ["Position"] = position,
            ["Rotation"] = "0,0,0,1",
            ["Scale"] = "1,1,1",
            ["Enabled"] = true,
            ["Tags"] = "",
            ["Components"] = list,
        } );
    }

    static JsonObject Renderer( string tint = "1,1,1,1", string guid = null ) => new()
    {
        ["__guid"] = guid ?? Guid.NewGuid().ToString(),
        ["__type"] = "Sandbox.ModelRenderer",
        ["Tint"] = tint,
    };

    static ObjectState With( ObjectState state, string field, JsonNode value )
    {
        var data = (JsonObject)state.Data.DeepClone();
        data[field] = value;
        return state with { Data = data };
    }

    static Packet Edit( string peer, params Change[] changes ) => new()
    {
        Kind = "edit",
        Peer = peer,
        Scene = Scene,
        Changes = changes.ToList(),
        Final = true,
    };

    static Authority Seeded( out List<Packet> journal, params ObjectState[] objects )
    {
        var written = new List<Packet>();
        journal = written;
        var authority = new Authority( written.Add );
        authority.Subscribe( new Packet { Kind = "subscribe", Scene = Scene, Objects = objects.ToList() } );
        return authority;
    }

    // ---- Tests ----------------------------------------------------------------------------------

    public static void Run( Action<bool, string> assert )
    {
        FloatTolerance( assert );
    }

    // ---- Part 4: float tolerance ------------------------------------------------------------------

    static JsonNode J( string json ) => JsonNode.Parse( json );

    static void FloatTolerance( Action<bool, string> assert )
    {
        // The engine stores 32-bit floats, so a value written as 0.6 comes back as 0.600000024. Compared as
        // text-exact numbers those are different and every such echo would look like an edit.
        assert( StateRules.ValuesEqual( J( "0.6" ), J( "0.600000024" ) ), "merge_values_float_noise_is_equal" );
        assert( !StateRules.ValuesEqual( J( "0.6" ), J( "0.61" ) ), "merge_values_real_float_difference_is_not_equal" );
        assert( StateRules.ValuesEqual( J( "1" ), J( "1.0" ) ), "merge_values_integer_and_decimal_spelling_are_equal" );
        assert( StateRules.ValuesEqual( J( "9007199254740993" ), J( "9007199254740993" ) ), "merge_values_same_huge_long_is_equal" );
        assert( !StateRules.ValuesEqual( J( "9007199254740993" ), J( "9007199254740992" ) ), "merge_values_longs_above_2_pow_53_compare_exactly" );
        assert( !StateRules.ValuesEqual( J( "9223372036854775807" ), J( "9223372036854775806" ) ), "merge_values_long_max_neighbours_differ" );
        assert( StateRules.ValuesEqual( J( "{\"a\":{\"x\":1,\"y\":[1,2]},\"b\":true}" ), J( "{\"b\":true,\"a\":{\"y\":[1,2],\"x\":1}}" ) ), "merge_values_nested_key_order_is_ignored" );
        assert( !StateRules.ValuesEqual( J( "[1,2]" ), J( "[2,1]" ) ), "merge_values_array_order_is_respected" );
        assert( !StateRules.ValuesEqual( J( "[1,2]" ), J( "[1,2,3]" ) ), "merge_values_array_length_is_respected" );
        assert( !StateRules.ValuesEqual( J( "{\"a\":1}" ), J( "{\"a\":1,\"b\":2}" ) ), "merge_values_extra_key_is_a_difference" );
        assert( !StateRules.ValuesEqual( J( "{\"a\":1,\"b\":null}" ), J( "{\"a\":1}" ) ), "merge_values_a_null_member_is_not_an_absent_member" );
        assert( StateRules.ValuesEqual( null, null ), "merge_values_null_equals_null" );
        assert( !StateRules.ValuesEqual( null, J( "0" ) ) && !StateRules.ValuesEqual( J( "\"\"" ), null ), "merge_values_null_differs_from_a_value" );
        assert( !StateRules.ValuesEqual( J( "\"1\"" ), J( "1" ) ), "merge_values_string_and_number_differ" );
        assert( StateRules.ValuesEqual( J( "\"abc\"" ), J( "\"abc\"" ) ) && !StateRules.ValuesEqual( J( "\"abc\"" ), J( "\"abd\"" ) ), "merge_values_strings_compare_exactly" );
        assert( StateRules.ValuesEqual( J( "true" ), J( "true" ) ) && !StateRules.ValuesEqual( J( "true" ), J( "false" ) ), "merge_values_booleans_compare_exactly" );
        assert( StateRules.ValuesEqual( JsonValue.Create( 1 ), JsonValue.Create( 1.0 ) ) && StateRules.ValuesEqual( JsonValue.Create( 0.6 ), JsonValue.Create( 0.600000024 ) ), "merge_values_numbers_built_in_code_compare_like_parsed_ones" );
        assert( !StateRules.ValuesEqual( J( "{\"a\":1}" ), J( "[1]" ) ) && !StateRules.ValuesEqual( J( "{}" ), J( "[]" ) ), "merge_values_object_and_array_differ" );

        // The scene-level equality the editor and the host both use: float noise is not a change.
        ObjectState Lamp( string radius )
        {
            var light = new JsonObject { ["__guid"] = Guid.NewGuid().ToString(), ["__type"] = "Sandbox.PointLight", ["Radius"] = JsonNode.Parse( radius ) };
            return Cube( "Lamp", "0,0,0", "", light );
        }
        var noisy = Lamp( "0.6" );
        var echoed = noisy with { Data = (JsonObject)noisy.Data.DeepClone() };
        echoed.Data["Components"]!.AsArray()[0]!.AsObject()["Radius"] = JsonNode.Parse( "0.600000024" );
        assert( StateRules.Equal( noisy, echoed ), "merge_equal_ignores_float_noise_in_scene_data" );
        var before = new Dictionary<string, ObjectState> { [noisy.Id] = noisy };
        var after = new Dictionary<string, ObjectState> { [noisy.Id] = echoed };
        assert( StateRules.Diff( before, after ).Count == 0, "merge_diff_of_scenes_that_differ_only_by_float_noise_is_empty" );
        var really = echoed with { Data = (JsonObject)echoed.Data.DeepClone() };
        really.Data["Components"]!.AsArray()[0]!.AsObject()["Radius"] = JsonNode.Parse( "0.75" );
        assert( !StateRules.Equal( noisy, really ), "merge_equal_still_sees_one_real_property_change" );
        assert( StateRules.Diff( before, new Dictionary<string, ObjectState> { [noisy.Id] = really } ).Count == 1, "merge_diff_reports_the_one_real_change" );
        assert( !StateRules.Equal( noisy, noisy with { Parent = Guid.NewGuid().ToString() } ) && !StateRules.Equal( noisy, null ) && StateRules.Equal( null, null ), "merge_equal_still_compares_parent_and_null" );

        RuntimeFloatNoise( assert );
    }

    // The runtime authority keeps its own stale-edit check; it compares object data too, so an editor echo of
    // 0.6 as 0.600000024 must not read as somebody else's edit there either.
    static void RuntimeFloatNoise( Action<bool, string> assert )
    {
        Packet Runtime( string id, string operation, long sequence ) => new()
        {
            Kind = RuntimeAuthorityContract.OperationKind,
            Id = id,
            Peer = Guid.NewGuid().ToString(),
            Scene = "scenes/runtime.scene",
            ProtocolVersion = Protocol.CurrentVersion,
            Mode = CollaborationMode.RuntimeExperimental,
            RuntimeRunId = "run-float",
            RuntimeSequence = sequence,
            RuntimeTick = sequence,
            RuntimeOperationKind = operation,
        };
        var camera = new JsonObject { ["__guid"] = Guid.NewGuid().ToString(), ["__type"] = "Sandbox.CameraComponent", ["ZNear"] = 0.600000024 };
        var objectId = Guid.NewGuid().ToString();
        var seeded = new ObjectState( objectId, "", new JsonObject
        {
            ["__guid"] = objectId, ["Name"] = "Camera", ["Position"] = "0,0,0", ["Rotation"] = "0,0,0,1", ["Scale"] = "1,1,1",
            ["Enabled"] = true, ["Tags"] = "", ["Components"] = new JsonArray { camera },
        } );
        var authority = new RuntimeAuthority( _ => { } );
        var seed = Runtime( "seed", RuntimeAuthorityContract.SnapshotKind, 1 );
        seed.Objects = new List<ObjectState> { seeded };
        assert( authority.Apply( seed ).Kind == "accepted", "merge_runtime_float_fixture_seeds" );
        var sentBefore = new ObjectState( objectId, "", (JsonObject)seeded.Data.DeepClone() );
        sentBefore.Data["Components"]!.AsArray()[0]!.AsObject()["ZNear"] = 0.6;
        var sentAfter = With( sentBefore, "Name", "Camera 2" );
        var edit = Runtime( "edit", RuntimeAuthorityContract.EditKind, 2 );
        edit.Changes = new List<Change> { new Change( objectId, sentBefore, sentAfter ) };
        var receipt = authority.Apply( edit );
        assert( receipt.Kind == "accepted", "merge_runtime_authority_treats_float_noise_as_unchanged: " + receipt.Kind + " " + receipt.Error );
    }
}
