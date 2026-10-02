using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// SceneDigest is the drift fingerprint carried by pings. Two editors that hold the same scene must
/// always compute the same text however their dictionaries were filled, whichever JSON number
/// representation the engine handed them and however many 32-bit float round trips the values took;
/// two scenes that really differ must not collide by construction.
/// </summary>
static class SceneDigestTests
{
    public static void Run( Action<bool, string> assert )
    {
        Format( assert );
        CanonicalText( assert );
        ObjectOrder( assert );
        KeyOrder( assert );
        Changes( assert );
        FloatNoise( assert );
        LargeIntegers( assert );
        NonFiniteNumbers( assert );
        FieldBoundaries( assert );
        ValueTypes( assert );
        Arrays( assert );
        Refusals( assert );
        Speed( assert );
    }

    // -- helpers ---------------------------------------------------------------------------------

    // Parsed on purpose: values built this way are JsonElement-backed, exactly like scene data that
    // arrived over the wire, as opposed to the CLR-backed values the editor builds in memory.
    static ObjectState State( string id, string parent, string json ) => new( id, parent, (JsonObject)JsonNode.Parse( json ) );

    static Dictionary<string, ObjectState> Scene( params ObjectState[] objects )
    {
        // A plain Dictionary enumerates in insertion order, so the order of the arguments is the
        // order the digest would see without its own sort.
        var scene = new Dictionary<string, ObjectState>();
        foreach ( var o in objects ) scene[o.Id] = o;
        return scene;
    }

    static string One( string json ) => SceneDigest.Compute( Scene( State( "o", null, json ) ) );

    static string OneData( JsonObject data ) => SceneDigest.Compute( Scene( new ObjectState( "o", null, data ) ) );

    static string Hex16( string text ) => Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( text ) ) ).ToLowerInvariant().Substring( 0, 16 );

    static bool Refuses( Action action )
    {
        try { action(); return false; }
        catch ( ArgumentException ) { return true; }
    }

    static string RefusalMessage( Action action )
    {
        try { action(); return null; }
        catch ( ArgumentException ex ) { return ex.Message; }
    }

    // -- sections --------------------------------------------------------------------------------

    static void Format( Action<bool, string> assert )
    {
        var digest = One( "{\"Name\":\"Cube\"}" );
        assert( SceneDigest.Length == 16, "digest_length_constant_is_16" );
        assert( digest.Length == 16, "digest_is_16_characters" );
        assert( digest.All( c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ), "digest_is_lowercase_hex" );

        var empty = SceneDigest.Compute( new Dictionary<string, ObjectState>() );
        assert( empty.Length == 16 && empty == empty.ToLowerInvariant(), "digest_empty_scene_has_the_same_format" );
        assert( empty == SceneDigest.Compute( new Dictionary<string, ObjectState>() ), "digest_empty_scene_is_stable" );
        assert( empty != digest, "digest_empty_scene_differs_from_a_one_object_scene" );
    }

    static void CanonicalText( Action<bool, string> assert )
    {
        // Pins the exact canonical text, because peers on different library builds must keep
        // agreeing on it: sorted keys, no whitespace, JSON escaping, null parent as "".
        var scene = Scene( State( "a", null, "{\"b\":[1,true,null,\"x\\\"y\"],\"a\":0.5}" ) );
        const string text = "[{\"id\":\"a\",\"parent\":\"\",\"data\":{\"a\":0.5,\"b\":[1,true,null,\"x\\\"y\"]}}]";
        assert( SceneDigest.Compute( scene ) == Hex16( text ), "digest_canonical_text_is_pinned" );
        assert( SceneDigest.Compute( new Dictionary<string, ObjectState>() ) == Hex16( "[]" ), "digest_empty_scene_canonical_text_is_pinned" );

        var withParent = Scene( State( "c", "p", "{}" ), State( "b", "", "{}" ) );
        const string two = "[{\"id\":\"b\",\"parent\":\"\",\"data\":{}},{\"id\":\"c\",\"parent\":\"p\",\"data\":{}}]";
        assert( SceneDigest.Compute( withParent ) == Hex16( two ), "digest_canonical_text_sorts_objects_and_keeps_parent" );

        assert( One( "{}" ) == SceneDigest.Compute( Scene( State( "o", "", "{}" ) ) ), "digest_null_parent_is_an_empty_parent" );
        assert( SceneDigest.Compute( Scene( State( "o", "p", "{}" ) ) ) != One( "{}" ), "digest_a_real_parent_differs_from_none" );
    }

    static void ObjectOrder( Action<bool, string> assert )
    {
        var objects = Enumerable.Range( 0, 30 )
            .Select( i => State( "obj-" + i.ToString( "D2" ), i == 0 ? null : "obj-00", "{\"Name\":\"N" + i + "\",\"Index\":" + i + "}" ) )
            .ToArray();

        var forward = SceneDigest.Compute( Scene( objects ) );
        var backward = SceneDigest.Compute( Scene( objects.Reverse().ToArray() ) );
        var shuffled = SceneDigest.Compute( Scene( objects.OrderBy( o => int.Parse( o.Id.Substring( 4 ) ) * 7 % 30 ).ToArray() ) );
        assert( forward == backward, "digest_object_insertion_order_reversed_is_equal" );
        assert( forward == shuffled, "digest_object_insertion_order_shuffled_is_equal" );

        // Mixed-case ids sort by ordinal value, not by culture: "B" precedes "a".
        var upperFirst = SceneDigest.Compute( Scene( State( "B", null, "{}" ), State( "a", null, "{}" ) ) );
        var lowerFirst = SceneDigest.Compute( Scene( State( "a", null, "{}" ), State( "B", null, "{}" ) ) );
        assert( upperFirst == lowerFirst, "digest_object_order_independent_for_mixed_case_ids" );
        const string ordinal = "[{\"id\":\"B\",\"parent\":\"\",\"data\":{}},{\"id\":\"a\",\"parent\":\"\",\"data\":{}}]";
        assert( lowerFirst == Hex16( ordinal ), "digest_object_ids_sort_ordinally" );

        var readOnly = new ReadOnlyDictionary<string, ObjectState>( Scene( objects ) );
        assert( SceneDigest.Compute( readOnly ) == forward, "digest_accepts_a_read_only_dictionary" );
    }

    static void KeyOrder( Action<bool, string> assert )
    {
        var a = One( "{\"Name\":\"Cube\",\"Position\":{\"x\":1,\"y\":2,\"z\":3},\"Components\":[{\"__type\":\"T\",\"Tint\":\"1\",\"Mass\":4}]}" );
        var b = One( "{\"Components\":[{\"Mass\":4,\"Tint\":\"1\",\"__type\":\"T\"}],\"Position\":{\"z\":3,\"y\":2,\"x\":1},\"Name\":\"Cube\"}" );
        assert( a == b, "digest_property_order_is_ignored_at_every_depth" );

        // Same again for CLR-built objects, whose insertion order is also preserved by JsonObject.
        var first = new JsonObject { ["Name"] = "Cube", ["Position"] = new JsonObject { ["x"] = 1, ["y"] = 2 } };
        var second = new JsonObject { ["Position"] = new JsonObject { ["y"] = 2, ["x"] = 1 }, ["Name"] = "Cube" };
        assert( OneData( first ) == OneData( second ), "digest_property_order_is_ignored_for_in_memory_objects" );
        assert( OneData( first ) == One( "{\"Position\":{\"x\":1,\"y\":2},\"Name\":\"Cube\"}" ), "digest_in_memory_and_parsed_objects_agree" );

        // Ordinal, not culture, ordering of keys: an uppercase key precedes a lowercase one.
        const string text = "[{\"id\":\"o\",\"parent\":\"\",\"data\":{\"B\":1,\"a\":2}}]";
        assert( One( "{\"a\":2,\"B\":1}" ) == Hex16( text ), "digest_property_keys_sort_ordinally" );
    }

    static void Changes( Action<bool, string> assert )
    {
        const string cube = "{\"Name\":\"Cube\",\"Position\":{\"x\":1,\"y\":2,\"z\":3},\"Components\":[{\"__type\":\"Sandbox.ModelRenderer\"}]}";
        ObjectState Root() => State( "root", null, "{\"Name\":\"Root\"}" );
        ObjectState Cube( string parent = "root", string json = cube ) => State( "cube", parent, json );
        ObjectState Light() => State( "light", "root", "{\"Name\":\"Light\"}" );
        var baseline = SceneDigest.Compute( Scene( Root(), Cube(), Light() ) );

        assert( baseline == SceneDigest.Compute( Scene( Root(), Cube(), Light() ) ), "digest_same_scene_rebuilt_is_equal" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube( json: cube.Replace( "\"Cube\"", "\"Cuba\"" ) ), Light() ) ), "digest_changed_name_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube( json: cube.Replace( "\"x\":1", "\"x\":2" ) ), Light() ) ), "digest_changed_position_component_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube( parent: "light" ), Light() ) ), "digest_changed_parent_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube( json: cube.Replace( "}]}", "},{\"__type\":\"Sandbox.BoxCollider\"}]}" ) ), Light() ) ), "digest_added_component_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube( json: cube.Replace( "[{\"__type\":\"Sandbox.ModelRenderer\"}]", "[]" ) ), Light() ) ), "digest_removed_component_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube() ) ), "digest_removed_object_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube(), Light(), State( "extra", "root", "{}" ) ) ), "digest_added_object_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), State( "cube2", "root", cube ), Light() ) ), "digest_renamed_id_flips" );
        assert( baseline != SceneDigest.Compute( Scene( Root(), Cube( json: cube.Replace( "\"Name\":\"Cube\",", "\"Name\":\"Cube\",\"Enabled\":false," ) ), Light() ) ), "digest_added_property_flips" );
    }

    static void FloatNoise( Action<bool, string> assert )
    {
        // The engine stores 32-bit floats, so 0.6 returns as 0.600000024 and 1 as 1.0.
        string Pos( string x, string y = "1" ) => "{\"Position\":{\"x\":" + x + ",\"y\":" + y + "}}";
        assert( One( Pos( "0.6" ) ) == One( Pos( "0.600000024" ) ), "digest_float_round_trip_noise_is_ignored_when_parsed" );
        assert( One( Pos( "0.6", "1" ) ) == One( Pos( "0.6", "1.0" ) ), "digest_one_and_one_point_zero_agree_when_parsed" );
        assert( One( Pos( "0.6", "1" ) ) == One( Pos( "0.6", "1.00000001" ) ), "digest_one_and_one_plus_epsilon_agree_when_parsed" );
        assert( One( Pos( "0.6", "1" ) ) == One( Pos( "0.6", "1e0" ) ), "digest_exponent_form_agrees_when_parsed" );

        JsonObject Clr( JsonNode x, JsonNode y ) => new() { ["Position"] = new JsonObject { ["x"] = x, ["y"] = y } };
        var parsed = One( Pos( "0.6" ) );
        assert( OneData( Clr( JsonValue.Create( 0.6 ), JsonValue.Create( 1 ) ) ) == parsed, "digest_in_memory_double_and_int_match_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.600000024 ), JsonValue.Create( 1.0 ) ) ) == parsed, "digest_in_memory_noisy_double_and_double_one_match_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6f ), JsonValue.Create( 1f ) ) ) == parsed, "digest_in_memory_float_matches_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6 ), JsonValue.Create( 1L ) ) ) == parsed, "digest_in_memory_long_one_matches_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6m ), JsonValue.Create( 1m ) ) ) == parsed, "digest_in_memory_decimal_matches_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6 ), JsonValue.Create( 1.00000001 ) ) ) == parsed, "digest_in_memory_one_plus_epsilon_matches_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6 ), JsonValue.Create( (byte)1 ) ) ) == parsed, "digest_in_memory_byte_matches_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6 ), JsonValue.Create( 1UL ) ) ) == parsed, "digest_in_memory_small_ulong_matches_parsed" );

        // Real changes still register: the rounding is to 32-bit precision, no coarser.
        assert( One( Pos( "0.6" ) ) != One( Pos( "0.61" ) ), "digest_a_real_small_change_flips_when_parsed" );
        assert( OneData( Clr( JsonValue.Create( 0.6 ), JsonValue.Create( 1 ) ) ) != OneData( Clr( JsonValue.Create( 0.61 ), JsonValue.Create( 1 ) ) ), "digest_a_real_small_change_flips_in_memory" );
        assert( One( Pos( "0.6" ) ) != One( Pos( "0.6000001" ) ), "digest_a_one_ulp_float_change_flips" );
        assert( One( Pos( "1" ) ) != One( Pos( "1.0001" ) ), "digest_one_vs_one_point_zero_zero_zero_one_flips" );
        assert( One( Pos( "0" ) ) == One( Pos( "-0.0" ) ), "digest_negative_zero_equals_zero" );
        assert( One( Pos( "0" ) ) != One( Pos( "0.000001" ) ), "digest_zero_and_a_small_value_differ" );

        // The 2^24 boundary: whole numbers up to it are exact floats, above it they are not.
        assert( One( Pos( "16777216" ) ) == One( Pos( "16777216.0" ) ), "digest_two_to_the_24_agrees_across_forms" );
        assert( One( Pos( "16777216" ) ) != One( Pos( "16777217" ) ), "digest_two_to_the_24_plus_one_is_distinct" );
        assert( One( Pos( "-16777216" ) ) != One( Pos( "-16777217" ) ), "digest_negative_two_to_the_24_plus_one_is_distinct" );
    }

    static void LargeIntegers( Action<bool, string> assert )
    {
        string Id( string v ) => "{\"Seed\":" + v + "}";

        // Both of these are the same double, so a digest that read them as doubles would collide.
        assert( One( Id( "9007199254740993" ) ) != One( Id( "9007199254740992" ) ), "digest_longs_beyond_double_precision_differ" );
        assert( One( Id( "9223372036854775807" ) ) != One( Id( "9223372036854775806" ) ), "digest_longs_differing_in_the_last_digit_differ" );
        assert( One( Id( "-9223372036854775808" ) ) != One( Id( "-9223372036854775807" ) ), "digest_long_min_value_is_handled_and_distinct" );
        assert( One( Id( "123456789012345678" ) ) != One( Id( "123456789012345679" ) ), "digest_mid_sized_longs_differing_in_the_last_digit_differ" );
        assert( One( Id( "18446744073709551615" ) ) != One( Id( "18446744073709551614" ) ), "digest_unsigned_longs_differing_in_the_last_digit_differ" );

        var clr = new JsonObject { ["Seed"] = 9007199254740993L };
        assert( OneData( clr ) == One( Id( "9007199254740993" ) ), "digest_in_memory_long_matches_parsed_long" );
        var clrMin = new JsonObject { ["Seed"] = long.MinValue };
        assert( OneData( clrMin ) == One( Id( "-9223372036854775808" ) ), "digest_in_memory_long_min_value_matches_parsed" );
        var clrBig = new JsonObject { ["Seed"] = ulong.MaxValue };
        assert( OneData( clrBig ) == One( Id( "18446744073709551615" ) ), "digest_in_memory_ulong_matches_parsed_ulong" );
        var clrInt = new JsonObject { ["Seed"] = 2000000000 };
        assert( OneData( clrInt ) == One( Id( "2000000000" ) ), "digest_in_memory_int_beyond_two_to_the_24_matches_parsed" );
    }

    static void NonFiniteNumbers( Action<bool, string> assert )
    {
        // JSON text cannot hold these, but JsonValue.Create( double.NaN ) can, and a scene that
        // carries one must still digest deterministically rather than throw or vary.
        JsonObject Value( JsonNode v ) => new() { ["v"] = v };
        var nan = OneData( Value( JsonValue.Create( double.NaN ) ) );
        var positive = OneData( Value( JsonValue.Create( double.PositiveInfinity ) ) );
        var negative = OneData( Value( JsonValue.Create( double.NegativeInfinity ) ) );
        assert( nan == OneData( Value( JsonValue.Create( double.NaN ) ) ), "digest_nan_is_stable" );
        assert( nan == OneData( Value( JsonValue.Create( float.NaN ) ) ), "digest_float_and_double_nan_agree" );
        assert( nan != positive && nan != negative && positive != negative, "digest_nan_and_infinities_are_distinct" );
        assert( positive == One( "{\"v\":1e999}" ), "digest_parsed_overflow_equals_positive_infinity" );
        assert( nan != One( "{\"v\":0}" ) && positive != One( "{\"v\":0}" ), "digest_non_finite_values_differ_from_zero" );
        assert( nan != One( "{\"v\":\"NaN\"}" ) && positive != One( "{\"v\":\"Infinity\"}" ), "digest_non_finite_tokens_differ_from_strings" );
    }

    static void FieldBoundaries( Action<bool, string> assert )
    {
        // Characters moved between neighbouring fields must change the digest.
        var a = SceneDigest.Compute( Scene( State( "o", "c", "{\"Name\":\"ab\"}" ) ) );
        var b = SceneDigest.Compute( Scene( State( "o", "bc", "{\"Name\":\"a\"}" ) ) );
        assert( a != b, "digest_name_and_parent_shift_differs" );

        assert( SceneDigest.Compute( Scene( State( "ab", "c", "{}" ) ) ) != SceneDigest.Compute( Scene( State( "a", "bc", "{}" ) ) ), "digest_id_and_parent_shift_differs" );
        assert( SceneDigest.Compute( Scene( State( "a", "b", "{}" ) ) ) != SceneDigest.Compute( Scene( State( "ab", "", "{}" ) ) ), "digest_parent_folded_into_id_differs" );
        assert( One( "{\"a\":\"bc\"}" ) != One( "{\"ab\":\"c\"}" ), "digest_key_and_value_shift_differs" );
        assert( One( "{\"a\":\"b\",\"c\":\"d\"}" ) != One( "{\"a\":\"bc\",\"d\":\"\"}" ), "digest_value_and_next_key_shift_differs" );
        assert( One( "{\"a\":[\"b\",\"c\"]}" ) != One( "{\"a\":[\"bc\"]}" ), "digest_array_elements_merged_differ" );
        assert( SceneDigest.Compute( Scene( State( "a", null, "{\"x\":\"1\"}" ), State( "b", null, "{}" ) ) )
            != SceneDigest.Compute( Scene( State( "a", null, "{}" ), State( "b", null, "{\"x\":\"1\"}" ) ) ), "digest_same_data_on_a_different_object_differs" );

        // A value that contains the separators of the canonical text must not be able to forge
        // a second property.
        var forged = One( "{\"a\":\"1\\\",\\\"b\\\":\\\"2\"}" );
        assert( forged != One( "{\"a\":\"1\",\"b\":\"2\"}" ), "digest_string_containing_json_syntax_cannot_forge_a_property" );
        var forgedObject = SceneDigest.Compute( Scene( State( "a\",\"parent\":\"x", null, "{}" ) ) );
        assert( forgedObject != SceneDigest.Compute( Scene( State( "a", "x", "{}" ) ) ), "digest_id_containing_json_syntax_cannot_forge_a_parent" );

        assert( One( "{\"a\":\"\\n\"}" ) != One( "{\"a\":\"\\\\n\"}" ), "digest_newline_and_backslash_n_differ" );
        assert( One( "{\"a\":\"\\u0001\"}" ) != One( "{\"a\":\"\\u0002\"}" ), "digest_control_characters_differ" );
        assert( One( "{\"a\":\"\\\"\"}" ) != One( "{\"a\":\"\\\\\"}" ), "digest_quote_and_backslash_differ" );
        assert( One( "{\"a\":\"\"}" ) != One( "{\"a\":null}" ), "digest_empty_string_and_null_differ" );

        // Unpaired surrogates would all encode to U+FFFD in UTF-8 and collide if left unescaped.
        JsonObject Text( string s ) => new() { ["a"] = s };
        var d800 = OneData( Text( "\ud800" ) );
        var d801 = OneData( Text( "\ud801" ) );
        var dc00 = OneData( Text( "\udc00" ) );
        var replacement = OneData( Text( "\ufffd" ) );
        assert( new[] { d800, d801, dc00, replacement }.Distinct().Count() == 4, "digest_lone_surrogates_do_not_collide_with_each_other_or_the_replacement_character" );
        assert( OneData( Text( "\ud83d\ude00" ) ) == One( "{\"a\":\"\\ud83d\\ude00\"}" ), "digest_surrogate_pair_matches_its_parsed_form" );
        assert( OneData( Text( "\u00e9" ) ) == One( "{\"a\":\"\u00e9\"}" ) && OneData( Text( "\u00e9" ) ) == One( "{\"a\":\"\\u00e9\"}" ), "digest_non_ascii_text_matches_its_escaped_form" );
    }

    static void ValueTypes( Action<bool, string> assert )
    {
        var values = new[] { "\"1\"", "1", "true", "\"true\"", "null", "[]", "{}", "[null]", "false", "0" };
        var digests = values.Select( v => One( "{\"v\":" + v + "}" ) ).ToArray();
        assert( digests.Distinct().Count() == values.Length, "digest_each_json_value_type_has_its_own_text" );
        assert( One( "{\"v\":null}" ) != One( "{}" ), "digest_null_property_differs_from_a_missing_property" );
        assert( One( "{\"v\":true}" ) == OneData( new JsonObject { ["v"] = true } ), "digest_in_memory_bool_matches_parsed" );
        assert( One( "{\"v\":\"s\"}" ) == OneData( new JsonObject { ["v"] = "s" } ), "digest_in_memory_string_matches_parsed" );
        assert( One( "{\"v\":null}" ) == OneData( new JsonObject { ["v"] = null } ), "digest_in_memory_null_matches_parsed" );
    }

    static void Arrays( Action<bool, string> assert )
    {
        assert( One( "{\"a\":[1,2,3]}" ) != One( "{\"a\":[3,2,1]}" ), "digest_array_order_matters" );
        assert( One( "{\"a\":[1,2]}" ) != One( "{\"a\":[2,1]}" ), "digest_two_element_array_order_matters" );
        assert( One( "{\"a\":[{\"x\":1},{\"x\":2}]}" ) != One( "{\"a\":[{\"x\":2},{\"x\":1}]}" ), "digest_array_of_objects_order_matters" );
        assert( One( "{\"a\":[{\"x\":1,\"y\":2}]}" ) == One( "{\"a\":[{\"y\":2,\"x\":1}]}" ), "digest_keys_inside_array_elements_are_sorted" );
        assert( One( "{\"a\":[[1],[2]]}" ) != One( "{\"a\":[[1,2]]}" ), "digest_nested_array_boundaries_matter" );
        assert( One( "{\"a\":[]}" ) != One( "{\"a\":{}}" ), "digest_empty_array_and_empty_object_differ" );
    }

    static void Refusals( Action<bool, string> assert )
    {
        assert( Refuses( () => SceneDigest.Compute( null ) ), "digest_null_scene_throws_argument_exception" );
        assert( Refuses( () => SceneDigest.Compute( Scene( new ObjectState( "o", null, null ) ) ) ), "digest_null_data_throws_argument_exception" );
        var message = RefusalMessage( () => SceneDigest.Compute( Scene( new ObjectState( "lost", null, null ) ) ) );
        assert( message != null && message.Contains( "lost" ), "digest_null_data_message_names_the_object" );
        assert( Refuses( () => SceneDigest.Compute( new Dictionary<string, ObjectState> { ["o"] = null } ) ), "digest_null_object_throws_argument_exception" );
        assert( Refuses( () => SceneDigest.Compute( new Dictionary<string, ObjectState> { ["key"] = new ObjectState( "other", null, new JsonObject() ) } ) ), "digest_key_that_disagrees_with_the_object_id_throws" );
        assert( Refuses( () => SceneDigest.Compute( new Dictionary<string, ObjectState> { ["o"] = new ObjectState( null, null, new JsonObject() ) } ) ), "digest_null_object_id_throws" );

        // A malformed object must refuse even when a valid object precedes it in the scene.
        assert( Refuses( () => SceneDigest.Compute( Scene( State( "a", null, "{}" ), new ObjectState( "b", null, null ) ) ) ), "digest_malformed_object_among_valid_ones_throws" );

        // A value the canonical text cannot express is refused rather than guessed at.
        assert( Refuses( () => OneData( new JsonObject { ["g"] = JsonValue.Create( Guid.Empty ) } ) ), "digest_unsupported_value_type_throws" );

        // Nesting beyond what any parsed scene can reach is refused instead of overflowing the stack.
        JsonObject Nest( int depth )
        {
            var root = new JsonObject();
            var cursor = root;
            for ( int i = 0; i < depth; i++ )
            {
                var next = new JsonObject();
                cursor["n"] = next;
                cursor = next;
            }
            return root;
        }
        assert( Refuses( () => OneData( Nest( 200 ) ) ), "digest_excessive_nesting_throws" );
        assert( !Refuses( () => OneData( Nest( 50 ) ) ), "digest_realistic_nesting_is_accepted" );
        JsonObject NestArrays( int depth )
        {
            JsonNode inner = new JsonArray();
            for ( int i = 0; i < depth; i++ ) inner = new JsonArray( inner );
            return new JsonObject { ["a"] = inner };
        }
        assert( Refuses( () => OneData( NestArrays( 200 ) ) ), "digest_excessive_array_nesting_throws" );
    }

    static void Speed( Action<bool, string> assert )
    {
        var objects = Enumerable.Range( 0, 2000 )
            .Select( i => State( "object-" + i, i == 0 ? null : "object-0",
                "{\"Name\":\"Object " + i + "\",\"Position\":{\"x\":" + i + ".5,\"y\":" + (i % 7) + ",\"z\":0.6},\"Components\":["
                + "{\"__type\":\"Sandbox.ModelRenderer\",\"Tint\":\"1,1,1,1\",\"Model\":\"models/dev/box.vmdl\"},"
                + "{\"__type\":\"Sandbox.BoxCollider\",\"Scale\":\"50,50,50\",\"IsTrigger\":false}],\"Tags\":\"a b c\"}" ) )
            .ToArray();
        var scene = Scene( objects );
        var watch = Stopwatch.StartNew();
        var first = SceneDigest.Compute( scene );
        watch.Stop();
        assert( watch.ElapsedMilliseconds < 2000, "digest_2000_object_scene_computes_well_under_two_seconds" );
        assert( first == SceneDigest.Compute( scene ), "digest_2000_object_scene_is_stable" );
        assert( first.Length == SceneDigest.Length, "digest_2000_object_scene_keeps_the_fixed_length" );
        Console.WriteLine( "  (digest of 2000 objects: " + watch.ElapsedMilliseconds + " ms)" );
    }
}
