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
        ValidationFloatNoise( assert );
        ThreeWayMerge( assert );
        AuthorityMerge( assert );
        PacketFields( assert );
        JournalReplay( assert );
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

    // ValidateChange once refused every property outside a short allowlist; it now checks structure only, so an editor echo
    // (0.6 written back as 0.600000024) and a real change of any property are both accepted.
    static void ValidationFloatNoise( Action<bool, string> assert )
    {
        var before = Edited( Cube( "Cube", "0,0,0", "", Renderer() ), d => { d["Mass"] = 0.6; d["Components"]!.AsArray()[0]!["Opacity"] = 0.6; } );
        // The component is also really edited (Tint is supported): that is the only way the per-field component check runs at all,
        // because components that are equal under the tolerant comparison are skipped as a whole.
        var echoed = Edited( With( before, "Position", "4,0,0" ), d => { d["Mass"] = JsonNode.Parse( "0.600000024" ); d["Components"]!.AsArray()[0]!["Tint"] = "0,1,0,1"; d["Components"]!.AsArray()[0]!["Opacity"] = JsonNode.Parse( "0.600000024" ); } );
        string Refusal( ObjectState from, ObjectState to )
        {
            try { StateRules.ValidateChange( new Change( from.Id, from, to ) ); return null; }
            catch ( InvalidOperationException ex ) { return ex.Message; }
        }
        assert( Refusal( before, echoed ) == null, "merge_validate_float_echo_of_unsupported_properties_is_not_a_change: " + Refusal( before, echoed ) );
        var realTop = Edited( before, d => d["Mass"] = 0.61 );
        assert( Refusal( before, realTop ) == null, "merge_validate_real_change_of_any_object_property_is_accepted: " + Refusal( before, realTop ) );
        var realComponent = Edited( before, d => { d["Components"]!.AsArray()[0]!["Tint"] = "0,1,0,1"; d["Components"]!.AsArray()[0]!["Opacity"] = 0.61; } );
        assert( Refusal( before, realComponent ) == null, "merge_validate_real_change_of_any_component_field_is_accepted: " + Refusal( before, realComponent ) );
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

    // ---- Part 2: PropertyMerge.TryMerge -----------------------------------------------------------

    static ObjectState Edited( ObjectState state, Action<JsonObject> edit, string parent = null )
    {
        var data = (JsonObject)state.Data.DeepClone();
        edit( data );
        return state with { Data = data, Parent = parent ?? state.Parent };
    }

    static JsonObject Component( ObjectState state, int index ) => state.Data["Components"]!.AsArray()[index]!.AsObject();

    static JsonObject Light( string radius = "100", string guid = null ) => new()
    {
        ["__guid"] = guid ?? Guid.NewGuid().ToString(),
        ["__type"] = "Sandbox.PointLight",
        ["Radius"] = radius,
    };

    static bool Merge( ObjectState current, ObjectState before, ObjectState after, out ObjectState merged, out List<string> clashed ) =>
        PropertyMerge.TryMerge( current, before, after, out merged, out clashed );

    static string Text( ObjectState state, string field ) => state.Data[field]?.GetValue<string>();

    static string Joined( List<string> clashed ) => string.Join( ", ", clashed );

    static void ThreeWayMerge( Action<bool, string> assert )
    {
        // Different fields of one object: the other person's change must survive the incoming one.
        {
            var before = Cube( "Cube", "0,0,0" );
            var current = With( before, "Name", "Renamed" );
            var after = With( before, "Position", "10,0,0" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && merged != null, "merge_different_fields_merge" );
            assert( Text( merged, "Name" ) == "Renamed" && Text( merged, "Position" ) == "10,0,0", "merge_different_fields_both_survive" );
            assert( clashed.Count == 0, "merge_different_fields_report_no_clash" );
            assert( merged.Id == before.Id && StateRules.Equal( merged, Edited( before, d => { d["Name"] = "Renamed"; d["Position"] = "10,0,0"; } ) ), "merge_different_fields_result_is_exactly_the_union" );
        }

        // The same field: the later (incoming) edit wins and the field is named exactly.
        {
            var before = Cube( "Cube", "0,0,0" );
            var current = With( before, "Position", "5,0,0" );
            var after = With( before, "Position", "10,0,0" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && Text( merged, "Position" ) == "10,0,0", "merge_same_field_incoming_value_wins" );
            assert( clashed.Count == 1 && clashed[0] == "Position", "merge_same_field_clash_names_the_field_exactly: " + Joined( clashed ) );
        }

        // Both people made the identical change: nothing to report.
        {
            var before = Cube( "Cube", "0,0,0" );
            var current = With( before, "Position", "10,0,0" );
            var after = With( before, "Position", "10,0,0" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && Text( merged, "Position" ) == "10,0,0" && clashed.Count == 0, "merge_identical_simultaneous_change_is_not_a_clash: " + Joined( clashed ) );
        }

        // A field the incoming edit did not change keeps the host's value even if it differs from both sides.
        {
            var before = Cube( "Cube", "0,0,0" );
            var current = With( With( before, "Position", "5,0,0" ), "Name", "Renamed" );
            var after = With( before, "Name", "Other" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && Text( merged, "Position" ) == "5,0,0" && Text( merged, "Name" ) == "Other" && Joined( clashed ) == "Name", "merge_untouched_field_keeps_the_hosts_value_and_only_the_touched_clash_is_named: " + Joined( clashed ) );
        }

        // Engine float noise is not a change by the other person: the incoming edit is a clean one.
        {
            var withRadius = Cube( "Lamp", "0,0,0", "", Light( "100" ) );
            var before = Edited( withRadius, d => d["Intensity"] = 0.6 );
            var current = Edited( before, d => d["Intensity"] = JsonNode.Parse( "0.600000024" ) );
            var after = Edited( before, d => d["Intensity"] = 0.75 );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && clashed.Count == 0 && merged.Data["Intensity"]!.GetValue<double>() == 0.75, "merge_float_noise_on_the_host_side_is_not_a_clash: " + Joined( clashed ) );
        }

        // Parent is a field like any other.
        {
            var parent = Cube( "Parent" );
            var before = Cube( "Child" );
            var viaName = With( before, "Name", "Renamed" );
            var reparent = before with { Parent = parent.Id };
            assert( Merge( viaName, before, reparent, out var merged, out var clashed ) && merged.Parent == parent.Id && Text( merged, "Name" ) == "Renamed" && clashed.Count == 0, "merge_incoming_parent_change_survives_a_current_name_change" );
            assert( Merge( before with { Parent = parent.Id }, before, With( before, "Name", "Renamed" ), out merged, out clashed ) && merged.Parent == parent.Id && clashed.Count == 0, "merge_current_parent_change_survives_an_incoming_name_change" );
            var other = Cube( "Other" );
            assert( Merge( before with { Parent = other.Id }, before, reparent, out merged, out clashed ) && merged.Parent == parent.Id && Joined( clashed ) == "Parent", "merge_parent_clash_is_named_Parent_and_incoming_wins: " + Joined( clashed ) );
            assert( Merge( before with { Parent = parent.Id }, before, reparent, out merged, out clashed ) && clashed.Count == 0, "merge_identical_reparent_is_not_a_clash" );
            var nullParent = before with { Parent = null };
            assert( Merge( before with { Parent = parent.Id }, before, nullParent, out merged, out clashed ) && merged.Parent == parent.Id && clashed.Count == 0, "merge_null_and_empty_parent_are_the_same_root: " + merged?.Parent );
        }

        // A key present before and absent after is a removal; a key only the host holds stays.
        {
            var before = Edited( Cube( "Cube" ), d => d["Extra"] = "x" );
            var after = Edited( before, d => d.Remove( "Extra" ) );
            var host = Edited( before, d => d["HostOnly"] = "kept" );
            assert( Merge( host, before, after, out var merged, out var clashed ) && !merged.Data.ContainsKey( "Extra" ) && Text( merged, "HostOnly" ) == "kept" && clashed.Count == 0, "merge_incoming_key_removal_applies_and_host_only_keys_stay" );
            var hostChanged = Edited( before, d => d["Extra"] = "y" );
            assert( Merge( hostChanged, before, after, out merged, out clashed ) && !merged.Data.ContainsKey( "Extra" ) && Joined( clashed ) == "Extra", "merge_removing_a_key_the_host_changed_is_a_clash_and_removes: " + Joined( clashed ) );
            var addedAfter = Edited( Cube( "Cube" ), d => d["Fresh"] = "n" );
            var baseOnly = addedAfter with { Data = (JsonObject)addedAfter.Data.DeepClone() };
            baseOnly.Data.Remove( "Fresh" );
            assert( Merge( baseOnly, baseOnly, addedAfter, out merged, out clashed ) && Text( merged, "Fresh" ) == "n", "merge_incoming_key_addition_applies" );
        }

        // Components are matched by guid. Different fields of one component both survive; the same field is named TypeShort.Field.
        {
            var renderer = Renderer( "1,1,1,1" );
            renderer["Model"] = "models/a.vmdl";
            var before = Cube( "Cube", "0,0,0", "", renderer );
            var current = Edited( before, d => d["Components"]!.AsArray()[0]!["Tint"] = "1,0,0,1" );
            var after = Edited( before, d => d["Components"]!.AsArray()[0]!["Model"] = "models/b.vmdl" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && Component( merged, 0 )["Tint"]!.GetValue<string>() == "1,0,0,1" && Component( merged, 0 )["Model"]!.GetValue<string>() == "models/b.vmdl" && clashed.Count == 0, "merge_component_edits_to_different_fields_both_survive" );

            var afterTint = Edited( before, d => d["Components"]!.AsArray()[0]!["Tint"] = "0,0,1,1" );
            ok = Merge( current, before, afterTint, out merged, out clashed );
            assert( ok && Component( merged, 0 )["Tint"]!.GetValue<string>() == "0,0,1,1" && clashed.Count == 1 && clashed[0] == "ModelRenderer.Tint", "merge_component_same_field_clash_is_named_ModelRenderer_Tint: " + Joined( clashed ) );

            var sameTint = Edited( before, d => d["Components"]!.AsArray()[0]!["Tint"] = "1,0,0,1" );
            assert( Merge( current, before, sameTint, out merged, out clashed ) && clashed.Count == 0, "merge_component_identical_field_change_is_not_a_clash" );
        }

        // A component the incoming edit adds survives next to a field edit made on the host.
        {
            var before = Cube( "Cube", "0,0,0", "", Renderer() );
            var light = Light( "250" );
            var current = With( before, "Name", "Renamed" );
            var after = Edited( before, d => d["Components"]!.AsArray().Add( light.DeepClone() ) );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            var components = merged?.Data["Components"]!.AsArray();
            assert( ok && components.Count == 2 && components[1]!["__guid"]!.GetValue<string>() == light["__guid"]!.GetValue<string>() && Text( merged, "Name" ) == "Renamed" && clashed.Count == 0, "merge_incoming_added_component_survives_a_current_field_edit" );

            // Both added the same guid with the same content: one copy, no clash. Different content cannot be merged.
            var bothSame = Edited( before, d => d["Components"]!.AsArray().Add( light.DeepClone() ) );
            ok = Merge( bothSame, before, after, out merged, out clashed );
            assert( ok && merged.Data["Components"]!.AsArray().Count == 2 && clashed.Count == 0, "merge_identical_component_added_on_both_sides_stays_single" );
            var bothDifferent = Edited( before, d => { var theirs = (JsonObject)light.DeepClone(); theirs["Radius"] = "999"; d["Components"]!.AsArray().Add( theirs ); } );
            ok = Merge( bothDifferent, before, after, out merged, out clashed );
            assert( !ok && merged == null, "merge_same_guid_added_on_both_sides_with_different_content_is_unmergeable" );
        }

        // Order: the host's order is kept, new components are appended in the incoming order.
        {
            var a = Renderer(); var b = Light( "1" );
            var before = Cube( "Cube", "0,0,0", "", a, b );
            var c = Light( "2" ); var d2 = Light( "3" );
            var current = Edited( before, d => d["Components"]!.AsArray().Add( Light( "9" ) ) );
            var after = Edited( before, d => { d["Components"]!.AsArray().Add( c.DeepClone() ); d["Components"]!.AsArray().Add( d2.DeepClone() ); } );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            var guids = merged.Data["Components"]!.AsArray().Select( x => x["__guid"]!.GetValue<string>() ).ToList();
            var expected = new List<string> { a["__guid"]!.GetValue<string>(), b["__guid"]!.GetValue<string>(), Component( current, 2 )["__guid"]!.GetValue<string>(), c["__guid"]!.GetValue<string>(), d2["__guid"]!.GetValue<string>() };
            assert( ok && guids.SequenceEqual( expected ), "merge_component_order_keeps_the_hosts_and_appends_new_ones_in_incoming_order" );
        }

        // An incoming removal removes the component; a host edit to that component is reported as a clash.
        {
            var renderer = Renderer();
            var light = Light( "100" );
            var before = Cube( "Cube", "0,0,0", "", renderer, light );
            var after = Edited( before, d => d["Components"]!.AsArray().RemoveAt( 1 ) );
            var current = With( before, "Name", "Renamed" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && merged.Data["Components"]!.AsArray().Count == 1 && clashed.Count == 0 && Text( merged, "Name" ) == "Renamed", "merge_incoming_component_removal_removes_it" );
            var hostEdited = Edited( before, d => d["Components"]!.AsArray()[1]!["Radius"] = "400" );
            ok = Merge( hostEdited, before, after, out merged, out clashed );
            assert( ok && merged.Data["Components"]!.AsArray().Count == 1 && Joined( clashed ) == "PointLight (removed)", "merge_removing_a_component_the_host_edited_is_a_clash: " + Joined( clashed ) );
            var hostAlreadyRemoved = after;
            ok = Merge( hostAlreadyRemoved, before, after, out merged, out clashed );
            assert( ok && merged.Data["Components"]!.AsArray().Count == 1 && clashed.Count == 0, "merge_component_removed_on_both_sides_is_not_a_clash" );
        }

        // A component the host removed stays removed when the incoming edit never touched it.
        {
            var renderer = Renderer();
            var light = Light( "100" );
            var before = Cube( "Cube", "0,0,0", "", renderer, light );
            var current = Edited( before, d => d["Components"]!.AsArray().RemoveAt( 1 ) );
            var after = With( before, "Name", "Renamed" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && merged.Data["Components"]!.AsArray().Count == 1 && clashed.Count == 0 && Text( merged, "Name" ) == "Renamed", "merge_component_removed_by_the_host_stays_removed_when_the_incoming_edit_ignored_it: " + Joined( clashed ) );
        }

        // An incoming edit of a component the host already removed: the later edit wins and the component comes back.
        {
            var renderer = Renderer( "1,1,1,1" );
            var before = Cube( "Cube", "0,0,0", "", renderer );
            var current = Edited( before, d => d["Components"]!.AsArray().Clear() );
            var after = Edited( before, d => d["Components"]!.AsArray()[0]!["Tint"] = "0,1,0,1" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && merged.Data["Components"]!.AsArray().Count == 1 && Component( merged, 0 )["Tint"]!.GetValue<string>() == "0,1,0,1" && Joined( clashed ) == "ModelRenderer", "merge_edit_of_a_component_the_host_removed_re_adds_it_as_a_clash: " + Joined( clashed ) );
        }

        // Several clashes arrive in a stable order: object fields first, then components.
        {
            var renderer = Renderer( "1,1,1,1" );
            var before = Cube( "Cube", "0,0,0", "", renderer );
            var current = Edited( With( before, "Position", "5,0,0" ), d => d["Components"]!.AsArray()[0]!["Tint"] = "1,0,0,1" );
            var after = Edited( With( before, "Position", "9,0,0" ), d => d["Components"]!.AsArray()[0]!["Tint"] = "0,0,1,1" );
            Merge( current, before, after, out var merged, out var clashed );
            assert( Joined( clashed ) == "Position, ModelRenderer.Tint", "merge_clashes_list_fields_before_components: " + Joined( clashed ) );
        }

        // No Components key appears from nothing.
        {
            var bare = new ObjectState( Guid.NewGuid().ToString(), "", new JsonObject { ["Name"] = "Bare" } );
            bare.Data["__guid"] = bare.Id;
            var renamed = With( bare, "Name", "Bare 2" );
            var ok = Merge( bare, bare, renamed, out var merged, out var clashed );
            assert( ok && !merged.Data.ContainsKey( "Components" ) && Text( merged, "Name" ) == "Bare 2", "merge_does_not_invent_a_Components_key" );
        }

        // Inputs that cannot be merged safely fail closed.
        {
            var good = Cube( "Cube", "0,0,0", "", Renderer() );
            var edited = With( good, "Name", "Renamed" );
            assert( !Merge( null, good, edited, out var merged, out var clashed ) && merged == null, "merge_null_current_is_unmergeable" );
            assert( !Merge( good, null, edited, out merged, out clashed ) && merged == null, "merge_null_before_is_unmergeable" );
            assert( !Merge( good, good, null, out merged, out clashed ) && merged == null, "merge_null_after_is_unmergeable" );
            assert( !Merge( good, good, edited with { Id = Guid.NewGuid().ToString() }, out merged, out clashed ) && merged == null, "merge_differing_ids_are_unmergeable" );
            assert( !Merge( good with { Id = Guid.NewGuid().ToString() }, good, edited, out merged, out clashed ) && merged == null, "merge_differing_current_id_is_unmergeable" );
            var noGuid = Edited( good, d => d["Components"]!.AsArray()[0]!.AsObject().Remove( "__guid" ) );
            assert( !Merge( noGuid, good, edited, out merged, out clashed ) && merged == null, "merge_component_without_guid_on_the_host_side_is_unmergeable" );
            assert( !Merge( good, noGuid, edited, out merged, out clashed ) && merged == null, "merge_component_without_guid_in_before_is_unmergeable" );
            assert( !Merge( good, good, noGuid, out merged, out clashed ) && merged == null, "merge_component_without_guid_in_after_is_unmergeable" );
            var numericGuid = Edited( good, d => d["Components"]!.AsArray()[0]!["__guid"] = 7 );
            assert( !Merge( numericGuid, good, edited, out merged, out clashed ) && merged == null, "merge_component_with_a_non_string_guid_is_unmergeable" );
            var duplicate = Edited( good, d => d["Components"]!.AsArray().Add( d["Components"]!.AsArray()[0]!.DeepClone() ) );
            assert( !Merge( duplicate, good, edited, out merged, out clashed ) && merged == null, "merge_duplicate_component_guid_in_current_is_unmergeable" );
            assert( !Merge( good, duplicate, edited, out merged, out clashed ) && merged == null, "merge_duplicate_component_guid_in_before_is_unmergeable" );
            assert( !Merge( good, good, duplicate, out merged, out clashed ) && merged == null, "merge_duplicate_component_guid_in_after_is_unmergeable" );
            var notAnArray = Edited( good, d => d["Components"] = "oops" );
            assert( !Merge( notAnArray, good, edited, out merged, out clashed ) && merged == null, "merge_components_that_are_not_an_array_are_unmergeable" );
            var notAnObject = Edited( good, d => d["Components"]!.AsArray().Add( 5 ) );
            assert( !Merge( good, good, notAnObject, out merged, out clashed ) && merged == null, "merge_a_component_that_is_not_an_object_is_unmergeable" );
        }

        // Nothing handed in is changed.
        {
            var renderer = Renderer( "1,1,1,1" );
            var before = Cube( "Cube", "0,0,0", "", renderer, Light( "10" ) );
            var current = Edited( With( before, "Position", "5,0,0" ), d => d["Components"]!.AsArray()[0]!["Tint"] = "1,0,0,1" );
            var after = Edited( With( before, "Position", "9,0,0" ), d => { d["Components"]!.AsArray()[0]!["Tint"] = "0,0,1,1"; d["Components"]!.AsArray().Add( Light( "77" ) ); } );
            var snapshots = new[] { current, before, after }.Select( x => (State: x, Data: x.Data.DeepClone(), Parent: x.Parent) ).ToList();
            var ok = Merge( current, before, after, out var merged, out var clashed );
            assert( ok && snapshots.All( x => JsonNode.DeepEquals( x.State.Data, x.Data ) && x.State.Parent == x.Parent ), "merge_does_not_mutate_its_inputs" );
            // The result must not share nodes with the inputs either, or editing it later would edit them.
            merged.Data["Position"] = "changed after the merge";
            Component( merged, 0 )["Tint"] = "changed after the merge";
            merged.Data["Components"]!.AsArray()[2]!["Radius"] = "changed after the merge";
            assert( snapshots.All( x => JsonNode.DeepEquals( x.State.Data, x.Data ) ), "merge_result_shares_no_nodes_with_its_inputs" );
        }

        // Every merged result must pass the graph rules when placed in a scene.
        {
            var parent = Cube( "Parent" );
            var before = Cube( "Child", "0,0,0", parent.Id, Renderer() );
            var current = With( before, "Name", "Renamed" );
            var after = Edited( before, d => d["Position"] = "3,0,0" );
            var ok = Merge( current, before, after, out var merged, out var clashed );
            var scene = new Dictionary<string, ObjectState> { [parent.Id] = parent, [merged.Id] = merged };
            var accepted = true;
            try { StateRules.ValidateGraph( scene ); } catch ( InvalidOperationException ) { accepted = false; }
            assert( ok && accepted, "merge_result_is_accepted_by_ValidateGraph" );
            var applied = new Change( merged.Id, current, merged );
            var valid = true;
            try { StateRules.ValidateChange( applied ); } catch ( Exception ) { valid = false; }
            assert( valid, "merge_result_is_accepted_by_ValidateChange" );
        }
    }

    // ---- Part 3: the host authority -----------------------------------------------------------------

    static ObjectState HostState( Authority authority, string id ) => authority.Scenes[Scene].GetValueOrDefault( id );

    static string Sentence( string name, string fields ) => "Concurrent edits to \"" + name + "\" were combined; the later edit was kept for: " + fields + ".";

    static void AuthorityMerge( Action<bool, string> assert )
    {
        // The bug as the user met it: peer A renames the cube, peer B (still holding the original) moves it.
        {
            var authority = new Authority( _ => { } );
            var original = Cube( "Cube", "0,0,0" );
            authority.Subscribe( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState> { original } } );
            var rename = authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Name", "Renamed" ) ) ) );
            assert( rename.Kind == "accepted", "merge_repro_first_edit_accepted: " + rename.Error );
            var move = authority.Apply( Edit( "peer-b", new Change( original.Id, original, With( original, "Position", "10,0,0" ) ) ) );
            assert( move.Kind == "accepted", "merge_repro: a second person's edit to a different property is accepted, not a conflict: " + move.Kind + " " + move.Error );
        }

        // Both edits' effects are present, the receipt carries what the host applied, and the combination is flagged.
        {
            var original = Cube( "Cube", "0,0,0", "", Renderer() );
            var authority = Seeded( out var journal, original );
            var renamed = With( original, "Name", "Renamed" );
            var first = authority.Apply( Edit( "peer-a", new Change( original.Id, original, renamed ) ) );
            assert( first.Kind == "accepted" && !first.Merged && first.Notice == null && ReferenceEquals( first.Changes[0].After, renamed ), "merge_fast_path_is_untouched_and_not_flagged" );
            var second = authority.Apply( Edit( "peer-b", new Change( original.Id, original, With( original, "Position", "10,0,0" ) ) ) );
            var host = HostState( authority, original.Id );
            assert( second.Kind == "accepted" && Text( host, "Name" ) == "Renamed" && Text( host, "Position" ) == "10,0,0", "merge_both_edits_are_present_in_the_hosts_scene: " + second.Error );
            assert( second.Merged && second.Notice == null, "merge_clean_merge_is_flagged_merged_without_a_notice: " + second.Merged + " " + second.Notice );
            assert( StateRules.Equal( second.Changes[0].Before, renamed ) && StateRules.Equal( second.Changes[0].After, host ), "merge_receipt_carries_the_applied_change_so_followers_before_matches_what_they_hold" );
            assert( second.Revision == 2 && authority.Revision == 2 && journal.Count == 3 && ReferenceEquals( journal[2], second ), "merge_revision_advances_and_the_applied_packet_is_the_one_persisted" );
        }

        // Same property: the later edit wins, the clash is reported, not refused.
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out _, original );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Position", "5,0,0" ) ) ) );
            var later = authority.Apply( Edit( "peer-b", new Change( original.Id, original, With( original, "Position", "10,0,0" ) ) ) );
            assert( later.Kind == "accepted" && later.Merged && Text( HostState( authority, original.Id ), "Position" ) == "10,0,0", "merge_same_field_clash_is_accepted_and_the_later_edit_wins: " + later.Error );
            assert( later.Notice == Sentence( "Cube", "Position" ), "merge_same_field_clash_notice_names_position: " + later.Notice );
        }

        // Component clash and field clash together, in one sentence.
        {
            var original = Cube( "Cube", "0,0,0", "", Renderer( "1,1,1,1" ) );
            var authority = Seeded( out _, original );
            var aliceEdit = Edited( With( original, "Position", "5,0,0" ), d => d["Components"]!.AsArray()[0]!["Tint"] = "1,0,0,1" );
            var bobEdit = Edited( With( original, "Position", "9,0,0" ), d => d["Components"]!.AsArray()[0]!["Tint"] = "0,0,1,1" );
            authority.Apply( Edit( "alice", new Change( original.Id, original, aliceEdit ) ) );
            var later = authority.Apply( Edit( "bob", new Change( original.Id, original, bobEdit ) ) );
            assert( later.Kind == "accepted" && later.Notice == Sentence( "Cube", "Position, ModelRenderer.Tint" ), "merge_field_and_component_clashes_are_listed_together: " + later.Kind + " " + later.Error + " " + later.Notice );
            assert( StateRules.Equal( HostState( authority, original.Id ), bobEdit ), "merge_later_edit_wins_every_clashing_property" );
        }

        // Both people made the identical edit: accepted, nothing to report, nothing different applied.
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out _, original );
            var edit = With( original, "Position", "10,0,0" );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, edit ) ) );
            var duplicate = authority.Apply( Edit( "peer-b", new Change( original.Id, original, edit ) ) );
            assert( duplicate.Kind == "accepted" && !duplicate.Merged && duplicate.Notice == null, "merge_identical_simultaneous_edits_are_accepted_without_a_notice: " + duplicate.Kind + " " + duplicate.Error + " " + duplicate.Notice );
        }

        // The notice names the object by its id when it has no readable name.
        {
            var original = Cube( "x", "0,0,0" );
            original.Data.Remove( "Name" );
            var authority = Seeded( out _, original );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Position", "5,0,0" ) ) ) );
            var later = authority.Apply( Edit( "peer-b", new Change( original.Id, original, With( original, "Position", "10,0,0" ) ) ) );
            assert( later.Notice == Sentence( original.Id, "Position" ), "merge_notice_falls_back_to_the_object_id: " + later.Notice );
        }

        // Delete against edit, both ways.
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out _, original );
            var renamed = With( original, "Name", "Renamed" );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, renamed ) ) );
            var deletion = authority.Apply( Edit( "peer-b", new Change( original.Id, original, null ) ) );
            assert( deletion.Kind == "accepted" && !authority.Scenes[Scene].ContainsKey( original.Id ), "merge_deletion_wins_over_a_concurrent_edit: " + deletion.Kind + " " + deletion.Error );
            assert( StateRules.Equal( deletion.Changes[0].Before, renamed ) && deletion.Changes[0].After == null, "merge_deletion_receipt_carries_the_hosts_state_as_before" );
            assert( deletion.Notice == "The later deletion of \"Renamed\" was kept; a concurrent edit to the same object was discarded.", "merge_deletion_notice_says_the_deletion_replaced_an_edit: " + deletion.Notice );
        }
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out var journal, original );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, null ) ) );
            var edited = With( original, "Name", "Edited" );
            var restored = authority.Apply( Edit( "peer-b", new Change( original.Id, original, edited ) ) );
            assert( restored.Kind == "accepted" && StateRules.Equal( HostState( authority, original.Id ), edited ), "merge_edit_of_an_object_deleted_meanwhile_restores_it_with_the_later_edit: " + restored.Kind + " " + restored.Error );
            assert( restored.Changes[0].Before == null && StateRules.Equal( restored.Changes[0].After, edited ), "merge_restore_is_journaled_as_a_creation" );
            assert( restored.Notice == "A concurrent deletion of \"Edited\" was overridden; the later edit was kept and the object was restored.", "merge_restore_notice: " + restored.Notice );
            var gone = Cube( "Gone", "0,0,0" );
            var second = Seeded( out _, gone );
            second.Apply( Edit( "peer-a", new Change( gone.Id, gone, null ) ) );
            var again = second.Apply( Edit( "peer-b", new Change( gone.Id, gone, null ) ) );
            assert( again.Kind == "reject" && again.Error.StartsWith( "Conflict on " + gone.Id, StringComparison.Ordinal ), "merge_deleting_an_already_deleted_object_stays_a_conflict: " + again.Kind + " " + again.Error );
        }

        // The restore route must pass the structural rules: a sender cannot bring an object back (or into being) around them by
        // claiming a stale Before that already carries a malformed component (here, one without a type name).
        {
            var original = Cube( "Cube", "0,0,0", "", Renderer() );
            var authority = Seeded( out var journal, original );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, null ) ) );
            var revision = authority.Revision;
            var count = journal.Count;
            var foreign = new JsonObject { ["__guid"] = Guid.NewGuid().ToString(), ["__type"] = "" };
            var claimedBefore = Edited( original, d => d["Components"]!.AsArray().Add( foreign.DeepClone() ) );
            var claimedAfter = With( claimedBefore, "Name", "Smuggled" );
            var smuggle = authority.Apply( Edit( "peer-b", new Change( original.Id, claimedBefore, claimedAfter ) ) );
            assert( smuggle.Kind == "reject" && smuggle.Error.Contains( "has a component without a type name", StringComparison.Ordinal ), "merge_restore_applies_the_structural_rules_to_what_it_brings_back: " + smuggle.Kind + " " + smuggle.Error );
            assert( authority.Revision == revision && journal.Count == count && !authority.Scenes[Scene].ContainsKey( original.Id ), "merge_refused_restore_changes_nothing" );
        }

        // Creation of an id that already exists keeps the conflict error, and changes nothing.
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out var journal, original );
            var revision = authority.Revision;
            var duplicate = authority.Apply( Edit( "peer-b", new Change( original.Id, null, With( original, "Name", "Again" ) ) ) );
            assert( duplicate.Kind == "reject" && duplicate.Error.StartsWith( "Conflict on " + original.Id, StringComparison.Ordinal ), "merge_creation_of_an_existing_id_is_still_a_conflict: " + duplicate.Kind + " " + duplicate.Error );
            assert( authority.Revision == revision && journal.Count == 1 && StateRules.Equal( HostState( authority, original.Id ), original ), "merge_refused_creation_changes_nothing" );
        }

        // An edit that cannot be merged safely keeps the conflict error as well.
        {
            var renderer = Renderer( "1,1,1,1" );
            renderer.Remove( "__guid" );
            var original = Cube( "Cube", "0,0,0", "", Renderer( "1,1,1,1" ) );
            var authority = Seeded( out var journal, original );
            // The sender's copy of the object is one whose component carries no guid (a damaged or foreign copy).
            var staleBefore = Cube( "Cube", "0,0,0", "", renderer ) with { Id = original.Id };
            staleBefore.Data["__guid"] = original.Id;
            // Only the Before is damaged: the After the sender would send passes the structural rules, so the merge is what refuses.
            var staleAfter = Edited( staleBefore, d => { d["Components"]!.AsArray()[0]!["Tint"] = "0,0,1,1"; d["Components"]!.AsArray()[0]!["__guid"] = Guid.NewGuid().ToString(); } );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Name", "Renamed" ) ) ) );
            var revision = authority.Revision;
            var count = journal.Count;
            var refused = authority.Apply( Edit( "peer-b", new Change( original.Id, staleBefore, staleAfter ) ) );
            assert( refused.Kind == "reject" && refused.Error.StartsWith( "Conflict on " + original.Id, StringComparison.Ordinal ), "merge_unmergeable_change_falls_back_to_the_conflict_error: " + refused.Kind + " " + refused.Error );
            assert( authority.Revision == revision && journal.Count == count && Text( HostState( authority, original.Id ), "Name" ) == "Renamed", "merge_unmergeable_change_leaves_the_scene_and_revision_unchanged" );
        }

        // A merged result that breaks the graph is a reject with the graph error and changes nothing.
        {
            var parent = Cube( "Parent", "0,0,0" );
            var child = Cube( "Child", "0,0,0" );
            var authority = Seeded( out var journal, parent, child );
            authority.Apply( Edit( "peer-a", new Change( child.Id, child, With( child, "Name", "Renamed" ) ) ) );
            authority.Apply( Edit( "peer-c", new Change( parent.Id, parent, null ) ) );
            var revision = authority.Revision;
            var count = journal.Count;
            var before = HostState( authority, child.Id );
            var reparent = authority.Apply( Edit( "peer-b", new Change( child.Id, child, child with { Parent = parent.Id } ) ) );
            assert( reparent.Kind == "reject" && reparent.Error == "Parent does not exist", "merge_reparent_under_a_deleted_object_is_rejected_with_the_graph_error: " + reparent.Kind + " " + reparent.Error );
            assert( authority.Revision == revision && journal.Count == count && StateRules.Equal( HostState( authority, child.Id ), before ) && authority.Scenes[Scene].Count == 1, "merge_graph_rejection_leaves_scenes_and_revision_unchanged" );
        }

        // Several objects in one operation: all are applied or none, and notices are joined with a space.
        {
            var one = Cube( "One", "0,0,0" );
            var two = Cube( "Two", "0,0,0" );
            var authority = Seeded( out _, one, two );
            authority.Apply( Edit( "peer-a", new Change( one.Id, one, With( one, "Position", "1,0,0" ) ), new Change( two.Id, two, With( two, "Position", "2,0,0" ) ) ) );
            var receipt = authority.Apply( Edit( "peer-b", new Change( one.Id, one, With( one, "Position", "7,0,0" ) ), new Change( two.Id, two, With( two, "Position", "8,0,0" ) ) ) );
            assert( receipt.Kind == "accepted" && receipt.Notice == Sentence( "One", "Position" ) + " " + Sentence( "Two", "Position" ), "merge_notices_of_several_objects_are_joined_with_a_space: " + receipt.Notice );

            var other = Cube( "Other", "0,0,0" );
            var atomic = Seeded( out _, one, other );
            atomic.Apply( Edit( "peer-a", new Change( one.Id, one, With( one, "Name", "Renamed" ) ) ) );
            var mixed = atomic.Apply( Edit( "peer-b", new Change( one.Id, one, With( one, "Position", "3,0,0" ) ), new Change( other.Id, null, other ) ) );
            assert( mixed.Kind == "reject" && Text( HostState( atomic, one.Id ), "Position" ) == "0,0,0" && Text( HostState( atomic, one.Id ), "Name" ) == "Renamed", "merge_one_unmergeable_change_rejects_the_whole_operation_including_merges: " + mixed.Kind );
        }

        // Any property merges now, network settings included: a stale edit of NetworkMode is combined with the concurrent rename.
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out _, original );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Name", "Renamed" ) ) ) );
            var network = authority.Apply( Edit( "peer-b", new Change( original.Id, original, With( original, "NetworkMode", 3 ) ) ) );
            assert( network.Kind == "accepted" && HostState( authority, original.Id ).Data["NetworkMode"]!.GetValue<int>() == 3 && Text( HostState( authority, original.Id ), "Name" ) == "Renamed", "merge_network_mode_on_a_stale_edit_is_combined_with_the_concurrent_rename: " + network.Kind + " " + network.Error );
        }

        // Resending the same packet id returns the identical receipt and does nothing twice.
        {
            var original = Cube( "Cube", "0,0,0" );
            var authority = Seeded( out var journal, original );
            authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Position", "5,0,0" ) ) ) );
            var packet = Edit( "peer-b", new Change( original.Id, original, With( original, "Position", "10,0,0" ) ) );
            var receipt = authority.Apply( packet );
            var revision = authority.Revision;
            var count = journal.Count;
            var again = authority.Apply( packet );
            assert( ReferenceEquals( receipt, again ) && again.Merged && again.Notice != null && authority.Revision == revision && journal.Count == count, "merge_resent_packet_returns_the_identical_receipt" );
            var refused = authority.Apply( Edit( "peer-c", new Change( original.Id, null, original ) ) );
            assert( ReferenceEquals( refused, authority.Apply( new Packet { Kind = "edit", Id = refused.Id, Peer = "peer-c", Scene = Scene, Changes = new List<Change>() } ) ), "merge_resent_reject_returns_the_identical_receipt" );
        }
    }

    // ---- Packet fields -----------------------------------------------------------------------------

    static void PacketFields( Action<bool, string> assert )
    {
        // Older peers never wrote Notice or Merged: a packet without them reads as an unmerged one.
        const string legacy = "{\"Kind\":\"accepted\",\"Id\":\"op\",\"Peer\":\"p\",\"Scene\":\"scenes/test.scene\",\"Revision\":3,\"Final\":true,\"Changes\":[]}";
        var parsed = JsonSerializer.Deserialize<Packet>( System.Text.Encoding.UTF8.GetBytes( legacy ) );
        assert( parsed != null && parsed.Kind == "accepted" && !parsed.Merged && parsed.Notice == null && parsed.Revision == 3, "merge_packet_without_the_new_fields_reads_as_unmerged" );

        // Unset, they are not on the wire at all, so a peer that rejects unknown fields never sees them.
        var plain = new Packet { Kind = "accepted", Id = "op", Peer = "p", Scene = Scene, Changes = new List<Change>() };
        var plainJson = System.Text.Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes( plain ) );
        assert( !plainJson.Contains( "Notice", StringComparison.Ordinal ) && !plainJson.Contains( "Merged", StringComparison.Ordinal ), "merge_unset_fields_are_omitted_from_the_wire: " + plainJson );

        // Set, they survive the very serializer calls the transport and the journal use.
        var original = Cube( "Cube", "0,0,0" );
        var authority = Seeded( out _, original );
        authority.Apply( Edit( "peer-a", new Change( original.Id, original, With( original, "Position", "5,0,0" ) ) ) );
        var receipt = authority.Apply( Edit( "peer-b", new Change( original.Id, original, With( original, "Position", "10,0,0" ) ) ) );
        var bytes = JsonSerializer.SerializeToUtf8Bytes( receipt );
        var text = System.Text.Encoding.UTF8.GetString( bytes );
        var round = JsonSerializer.Deserialize<Packet>( bytes );
        assert( text.Contains( "\"Merged\":true", StringComparison.Ordinal ) && text.Contains( "\"Notice\":", StringComparison.Ordinal ), "merge_set_fields_are_written_to_the_wire: " + text );
        assert( round.Merged && round.Notice == receipt.Notice && round.Kind == "accepted" && round.Changes.Count == 1 && StateRules.Equal( round.Changes[0].After, receipt.Changes[0].After ) && StateRules.Equal( round.Changes[0].Before, receipt.Changes[0].Before ), "merge_set_fields_round_trip_through_the_transport_serializer" );
        var journalLine = JsonSerializer.Serialize( receipt );
        assert( JsonSerializer.Deserialize<Packet>( journalLine ).Notice == receipt.Notice, "merge_set_fields_round_trip_through_the_journal_serializer" );
        assert( Protocol.CurrentVersion == 4, "merge_protocol_version_is_unchanged" );
    }

    // ---- Journal replay ----------------------------------------------------------------------------

    // The host's journal is one JSON packet per line: a "seed" for a scene's first state and an "accepted" packet per
    // operation. A recovery that replays it applies each accepted change's After (or removes the object) in order;
    // every Before must equal the state the replay holds at that point, or the journal contradicts itself.
    static Dictionary<string, ObjectState> Replay( IEnumerable<Packet> journal, out bool consistent )
    {
        consistent = true;
        var scene = new Dictionary<string, ObjectState>();
        foreach ( var line in journal.Select( x => JsonSerializer.Serialize( x ) ) )
        {
            var packet = JsonSerializer.Deserialize<Packet>( line );
            if ( packet.Kind == "seed" )
            {
                scene = StateRules.Map( packet.Objects );
            }
            else if ( packet.Kind == "accepted" )
            {
                foreach ( var change in packet.Changes )
                {
                    consistent &= StateRules.Equal( scene.GetValueOrDefault( change.Id ), change.Before );
                    if ( change.After == null )
                    {
                        scene.Remove( change.Id );
                    }
                    else
                    {
                        scene[change.Id] = change.After;
                    }
                }
            }
        }
        return scene;
    }

    static void JournalReplay( Action<bool, string> assert )
    {
        var x0 = Cube( "X", "0,0,0", "", Renderer( "1,1,1,1" ) );
        var y0 = Cube( "Y", "0,0,0" );
        var z0 = Cube( "Z", "0,0,0" );
        var authority = Seeded( out var journal, x0, y0, z0 );

        // X: a rename, then a stale move (clean merge), then a stale move of the same property (clash).
        var rename = authority.Apply( Edit( "a", new Change( x0.Id, x0, With( x0, "Name", "X renamed" ) ) ) );
        var move = authority.Apply( Edit( "b", new Change( x0.Id, x0, With( x0, "Position", "4,0,0" ) ) ) );
        var clash = authority.Apply( Edit( "c", new Change( x0.Id, x0, Edited( With( x0, "Position", "9,0,0" ), d => d["Components"]!.AsArray()[0]!["Tint"] = "0,1,0,1" ) ) ) );
        // Y: renamed, then deleted by somebody still holding the original (the deletion wins).
        var yRename = authority.Apply( Edit( "d", new Change( y0.Id, y0, With( y0, "Name", "Y renamed" ) ) ) );
        var yDelete = authority.Apply( Edit( "e", new Change( y0.Id, y0, null ) ) );
        // Z: deleted, then edited by somebody still holding the original (the edit restores it).
        var zDelete = authority.Apply( Edit( "f", new Change( z0.Id, z0, null ) ) );
        var zRestore = authority.Apply( Edit( "g", new Change( z0.Id, z0, With( z0, "Name", "Z restored" ) ) ) );
        var receipts = new[] { rename, move, clash, yRename, yDelete, zDelete, zRestore };
        assert( receipts.All( x => x.Kind == "accepted" ), "merge_replay_scenario_is_all_accepted: " + string.Join( ",", receipts.Select( x => x.Kind + ":" + x.Error ) ) );
        assert( !yRename.Merged && move.Merged && clash.Merged && yDelete.Notice != null && zRestore.Notice != null, "merge_replay_scenario_exercises_merge_clash_delete_and_restore" );

        var replayed = Replay( journal, out var consistent );
        assert( consistent, "merge_replaying_the_journal_finds_every_before_equal_to_the_state_it_holds" );
        assert( StateRules.Diff( replayed, authority.Scenes[Scene] ).Count == 0 && replayed.Count == authority.Scenes[Scene].Count, "merge_replaying_the_persisted_packets_reproduces_the_hosts_scene" );
        assert( replayed.ContainsKey( x0.Id ) && !replayed.ContainsKey( y0.Id ) && replayed.ContainsKey( z0.Id ), "merge_replayed_scene_has_the_surviving_objects_only" );
        var replayedX = replayed[x0.Id];
        assert( Text( replayedX, "Name" ) == "X renamed" && Text( replayedX, "Position" ) == "9,0,0" && Component( replayedX, 0 )["Tint"]!.GetValue<string>() == "0,1,0,1", "merge_replayed_object_carries_every_combined_edit" );
    }
}
