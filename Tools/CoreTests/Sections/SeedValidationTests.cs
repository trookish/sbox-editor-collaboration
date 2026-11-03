using System.Text.Json.Nodes;
using TeamCreate;

// What the host keeps and hands on must be something every editor can take: a scene's first copy (the subscribe seed) is held to the
// same structural rules as an edit, and no number in an object may be one that is not finite (1e999 reads as infinity).
static class SeedValidationTests
{
    private const string Scene = "scenes/seed.scene";

    private static ObjectState Identified( string json )
    {
        string id = Guid.NewGuid().ToString();
        var data = (JsonObject)JsonNode.Parse( json );
        data["__guid"] = id;
        return new ObjectState( id, null, data );
    }

    private static JsonObject Nested( int depth )
    {
        var root = new JsonObject();
        var current = root;
        for ( int i = 1; i < depth; i++ )
        {
            var next = new JsonObject();
            current["Inner"] = next;
            current = next;
        }
        return root;
    }

    private static Packet Subscribe( params ObjectState[] objects ) => new Packet { Kind = "subscribe", Id = "s1", Scene = Scene, Objects = objects.ToList() };

    public static void Run( Action<bool, string> assert )
    {
        // Numbers in an edit.
        var infinite = Identified( "{\"Name\":\"Lamp\",\"Position\":\"0,0,0\",\"Scale\":1e999}" );
        string refusal = StateRules.RefusalOf( new Change( infinite.Id, null, infinite ) );
        assert( refusal != null && refusal.Contains( "finite" ) && refusal.Contains( "Lamp" ), "seed_an_edit_holding_a_number_that_is_not_finite_is_refused_by_name: " + refusal );
        var nestedInfinite = Identified( "{\"Name\":\"Deep\",\"Components\":[{\"__type\":\"Light\",\"__guid\":\"" + Guid.NewGuid() + "\",\"Range\":[1,-1e400]}]}" );
        assert( StateRules.RefusalOf( new Change( nestedInfinite.Id, null, nestedInfinite ) ) != null, "seed_a_number_that_is_not_finite_deep_in_a_component_is_refused" );
        var ordinary = Identified( "{\"Name\":\"Ok\",\"Scale\":1.5e300,\"Count\":9223372036854775807,\"Tiny\":-4.9e-324}" );
        assert( StateRules.RefusalOf( new Change( ordinary.Id, null, ordinary ) ) == null, "seed_large_and_small_finite_numbers_are_allowed" );
        var built = Identified( "{\"Name\":\"Built\"}" );
        built.Data["Mode"] = 1;
        built.Data["Big"] = 9_000_000_000L;
        built.Data["Money"] = 1.5m;
        built.Data["Half"] = 0.5f;
        assert( StateRules.RefusalOf( new Change( built.Id, null, built ) ) == null, "seed_numbers_built_in_code_as_int_long_decimal_or_float_are_allowed" );
        built.Data["Broken"] = double.PositiveInfinity;
        assert( StateRules.RefusalOf( new Change( built.Id, null, built ) ) != null, "seed_an_infinite_double_built_in_code_is_refused" );
        built.Data["Broken"] = float.NaN;
        assert( StateRules.RefusalOf( new Change( built.Id, null, built ) ) != null, "seed_a_float_that_is_not_a_number_built_in_code_is_refused" );

        // The first copy of a scene.
        var authority = new Authority( _ => { } );
        var deep = new ObjectState( "deep", null, Nested( StateRules.MaxDataDepth + 1 ) );
        Packet answer = authority.Subscribe( Subscribe( deep ) );
        assert( answer.Kind == "reject" && answer.Id == "s1" && answer.Scene == null && answer.Error != null && answer.Error.StartsWith( "The scene " + Scene + " could not be shared. The object", StringComparison.Ordinal ) && answer.Error.Contains( "nested" ),
            "seed_a_first_copy_breaking_the_structural_rules_is_refused_for_its_sender: " + answer.Kind + " " + answer.Error );
        assert( !authority.Scenes.ContainsKey( Scene ), "seed_a_refused_first_copy_is_never_kept" );

        answer = authority.Subscribe( Subscribe( Identified( "{\"Name\":\"Bad\",\"Scale\":1e999}" ) ) );
        assert( answer.Kind == "reject" && !authority.Scenes.ContainsKey( Scene ), "seed_a_first_copy_holding_a_number_that_is_not_finite_is_refused" );

        answer = authority.Subscribe( Subscribe( new ObjectState( "nodata", null, null ) ) );
        assert( answer.Kind == "reject" && !authority.Scenes.ContainsKey( Scene ), "seed_a_first_copy_holding_an_object_without_data_is_refused" );

        var good = Identified( "{\"Name\":\"Good\"}" );
        answer = authority.Subscribe( Subscribe( good ) );
        assert( answer.Kind == "snapshot" && authority.Scenes[Scene].ContainsKey( good.Id ), "seed_a_valid_first_copy_is_kept" );

        // Once the host holds the scene, a later subscribe only reads it: what that subscribe carries is neither checked nor kept.
        answer = authority.Subscribe( Subscribe( deep ) );
        assert( answer.Kind == "snapshot" && answer.Objects.Count == 1 && answer.Objects[0].Id == good.Id, "seed_a_later_subscribe_gets_the_held_scene_whatever_it_carries" );
    }
}
