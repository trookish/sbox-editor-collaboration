using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// The visual whitelist. A spectator mirrors a run it must never execute, so only a short list of engine renderer and light types is ever created,
/// each with an explicit list of properties; the sender strips everything else before it is sent and the receiver refuses it again, because the
/// sender is another person's editor and cannot be trusted. The shapes below are copied from the engine's own sample scenes.
/// </summary>
static partial class SharedPlayTests
{
    const string Guid1 = "9012693a-9b4d-4dd3-becd-ebc8aa93c4e6";

    // From templates/game.playercontroller/Assets/scenes/basic_example.scene: a ModelRenderer exactly as the engine writes it.
    static JsonObject RealModelRenderer() => JsonNode.Parse( """
        {
          "__type": "Sandbox.ModelRenderer",
          "__guid": "9012693a-9b4d-4dd3-becd-ebc8aa93c4e6",
          "__enabled": true,
          "Flags": 0,
          "BodyGroups": 18446744073709551615,
          "CreateAttachments": false,
          "LodOverride": null,
          "MaterialGroup": null,
          "MaterialOverride": "materials/dev/reflectivity_50b.vmat",
          "Materials": null,
          "Model": "models/dev/plane.vmdl",
          "OnComponentDestroy": null,
          "OnComponentStart": null,
          "OnComponentUpdate": null,
          "RenderOptions": { "GameLayer": true, "OverlayLayer": false, "BloomLayer": true, "AfterUILayer": false },
          "RenderType": "On",
          "Tint": "1,1,1,1"
        }
        """ )!.AsObject();

    static void Whitelist( Action<bool, string> assert )
    {
        // Exactly the audited list: adding a type is a deliberate edit of both the list and this test.
        string[] expected =
        {
            "Sandbox.AmbientLight", "Sandbox.DirectionalLight", "Sandbox.MeshComponent", "Sandbox.ModelRenderer", "Sandbox.PointLight",
            "Sandbox.SkinnedModelRenderer", "Sandbox.SkyBox2D", "Sandbox.SpotLight", "Sandbox.Terrain",
        };
        assert( SpectateWhitelist.AllowedTypes.OrderBy( x => x, StringComparer.Ordinal ).SequenceEqual( expected ), "play_whitelist_is_exactly_the_audited_list" );
        assert( expected.All( SpectateWhitelist.IsAllowedType ), "play_whitelist_allows_every_listed_type" );

        // Everything else is refused: scripts, physics, audio, networking helpers, UI, particles (which spawn prefabs), cameras.
        string[] refused =
        {
            "Sandbox.CameraComponent", "Sandbox.ParticleEffect", "Sandbox.ParticleSpriteRenderer", "Sandbox.Rigidbody", "Sandbox.BoxCollider", "Sandbox.SphereCollider",
            "Sandbox.ModelCollider", "Sandbox.HullCollider", "Sandbox.SoundPointComponent", "Sandbox.SoundBoxComponent", "Sandbox.NetworkHelper", "Sandbox.PlayerController",
            "Sandbox.Citizen.CitizenAnimationHelper", "Sandbox.ScreenPanel", "Sandbox.WorldPanel", "Sandbox.NavMeshAgent", "Sandbox.PrefabSpawner", "Sandbox.ModelPhysics",
            "MyGame.PlayerMovement", "MyGame.Spawner", "Sandbox.Light", "Sandbox.Renderer", "Sandbox.Collider", "Sandbox.Component",
        };
        foreach ( string type in refused )
        {
            assert( !SpectateWhitelist.IsAllowedType( type ), "play_whitelist_refuses_" + type );
        }
        assert( !SpectateWhitelist.IsAllowedType( "Sandbox.CameraComponent" ) && SpectateWhitelist.NeverTypes.Contains( "Sandbox.CameraComponent" ), "play_whitelist_camera_is_never_instantiated" );

        // Hostile type strings: the match is exact and ordinal, so no spelling variant, path, padding or oversized name gets through.
        string[] hostile =
        {
            null, "", " ", "sandbox.modelrenderer", "SANDBOX.MODELRENDERER", " Sandbox.ModelRenderer", "Sandbox.ModelRenderer ", "Sandbox.ModelRenderer\0", "Sandbox.ModelRenderer\n",
            "../../Code/Evil", "Code/Evil.cs", "C:\\Windows\\evil.dll", "Sandbox.ModelRenderer, Evil.Assembly", "Sandbox.ModelRenderer/../../Evil", new string( 'A', 100000 ),
            "Sandbox.ModelRenderer" + new string( 'x', 5000 ), "Sandbox.\u200BModelRenderer",
        };
        foreach ( string type in hostile )
        {
            assert( !SpectateWhitelist.IsAllowedType( type ), "play_whitelist_refuses_hostile_type_" + ( type == null ? "null" : type.Length > 40 ? "long_" + type.Length : type.Replace( "\0", "NUL" ).Replace( "\n", "NL" ) ) );
        }

        // A real component survives with its visual properties; hooks and flags the whitelist does not name are dropped.
        var real = RealModelRenderer();
        assert( SpectateWhitelist.TrySanitize( real, out JsonObject kept, out string why ) && why == null, "play_whitelist_real_model_renderer_is_kept: " + why );
        assert( (string)kept["__type"] == "Sandbox.ModelRenderer" && (string)kept["__guid"] == Guid1 && (bool)kept["__enabled"] && (string)kept["Model"] == "models/dev/plane.vmdl", "play_whitelist_keeps_identity_and_model" );
        assert( (string)kept["MaterialOverride"] == "materials/dev/reflectivity_50b.vmat" && kept["RenderOptions"] is JsonObject && (string)kept["RenderType"] == "On", "play_whitelist_keeps_visual_properties" );
        assert( !kept.ContainsKey( "OnComponentStart" ) && !kept.ContainsKey( "OnComponentUpdate" ) && !kept.ContainsKey( "Flags" ) && !kept.ContainsKey( "CreateAttachments" ), "play_whitelist_drops_hooks_and_flags" );

        // Property names outside the list are dropped even when the type is allowed: a hostile sender cannot attach a callback or a prefab.
        var tampered = RealModelRenderer();
        tampered["OnTriggerEnter"] = "{\"__type\":\"Evil\"}";
        tampered["Prefab"] = "prefabs/evil.prefab";
        tampered["__Prefab"] = "prefabs/evil.prefab";
        tampered["__PrefabInstancePatch"] = new JsonObject();
        tampered["Components"] = new JsonArray( new JsonObject { ["__type"] = "MyGame.Evil" } );
        assert( SpectateWhitelist.TrySanitize( tampered, out kept, out _ ) && kept.All( p => p.Key is "__type" or "__guid" or "__enabled" || SpectateWhitelist.AllowedProperties( "Sandbox.ModelRenderer" ).Contains( p.Key ) ), "play_whitelist_drops_unlisted_properties" );
        assert( !kept.ContainsKey( "__Prefab" ) && !kept.ContainsKey( "__PrefabInstancePatch" ) && !kept.ContainsKey( "Prefab" ) && !kept.ContainsKey( "Components" ), "play_whitelist_never_carries_a_prefab_patch" );

        // A property VALUE can smuggle the same thing: nested type discriminators and prefab patches are how the engine builds objects of other types,
        // so a value that contains one is dropped whole, wherever it sits.
        foreach ( string poison in new[] { "{\"__type\":\"MyGame.Evil\"}", "{\"a\":{\"b\":[{\"__Prefab\":\"x\"}]}}", "{\"$compiler\":1}", "{\"__PrefabInstancePatch\":{}}", "[{\"__type\":\"Sandbox.ModelRenderer\"}]" } )
        {
            var o = RealModelRenderer();
            o["MaterialGroup"] = JsonNode.Parse( poison );
            assert( SpectateWhitelist.TrySanitize( o, out kept, out _ ) && !kept.ContainsKey( "MaterialGroup" ) && kept.ContainsKey( "Model" ), "play_whitelist_poisoned_value_is_dropped_" + poison.Length );
        }
        var deep = RealModelRenderer();
        JsonNode nest = "x";
        for ( int i = 0; i < 40; i++ ) nest = new JsonArray( nest );
        deep["Materials"] = nest;
        assert( SpectateWhitelist.TrySanitize( deep, out kept, out _ ) && !kept.ContainsKey( "Materials" ), "play_whitelist_deeply_nested_value_is_dropped" );
        var longText = RealModelRenderer();
        longText["Model"] = new string( 'm', SpectateWhitelist.MaxStringLength + 1 );
        assert( SpectateWhitelist.TrySanitize( longText, out kept, out _ ) && !kept.ContainsKey( "Model" ), "play_whitelist_oversized_string_is_dropped" );
        var wide = RealModelRenderer();
        var many = new JsonArray();
        for ( int i = 0; i < SpectateWhitelist.MaxValueNodes + 10; i++ ) many.Add( i );
        wide["Materials"] = many;
        assert( SpectateWhitelist.TrySanitize( wide, out kept, out _ ) && !kept.ContainsKey( "Materials" ), "play_whitelist_too_many_nodes_is_dropped" );

        // A component whose identity is unusable is refused whole; the reason is specific.
        var noGuid = RealModelRenderer();
        noGuid["__guid"] = "not-a-guid";
        assert( !SpectateWhitelist.TrySanitize( noGuid, out _, out why ) && why.Contains( "__guid" ), "play_whitelist_bad_guid_is_refused: " + why );
        assert( !SpectateWhitelist.TrySanitize( JsonNode.Parse( "{\"__type\":\"MyGame.Evil\",\"__guid\":\"" + Guid1 + "\"}" ), out _, out why ) && why.Contains( "not on the spectator whitelist" ), "play_whitelist_refusal_names_the_reason: " + why );
        assert( !SpectateWhitelist.TrySanitize( JsonNode.Parse( "{\"__type\":42}" ), out _, out _ ) && !SpectateWhitelist.TrySanitize( JsonNode.Parse( "\"Sandbox.ModelRenderer\"" ), out _, out _ ) && !SpectateWhitelist.TrySanitize( null, out _, out _ ), "play_whitelist_non_object_and_non_string_type_are_refused" );

        // Physics-capable visual types are forced to the non-colliding values whatever the sender wrote.
        var mesh = JsonNode.Parse( "{\"__type\":\"Sandbox.MeshComponent\",\"__guid\":\"" + Guid1 + "\",\"__enabled\":true,\"Collision\":\"Mesh\",\"Color\":\"1,1,1,1\",\"IsTrigger\":true,\"Static\":false,\"OnTriggerEnter\":\"x\"}" )!.AsObject();
        assert( SpectateWhitelist.TrySanitize( mesh, out kept, out _ ) && (string)kept["Collision"] == "None" && !kept.ContainsKey( "IsTrigger" ) && !kept.ContainsKey( "OnTriggerEnter" ) && (string)kept["Color"] == "1,1,1,1", "play_whitelist_mesh_collision_forced_off" );
        var terrain = JsonNode.Parse( "{\"__type\":\"Sandbox.Terrain\",\"__guid\":\"" + Guid1 + "\",\"EnableCollision\":true,\"TerrainSize\":4096}" )!.AsObject();
        assert( SpectateWhitelist.TrySanitize( terrain, out kept, out _ ) && !(bool)kept["EnableCollision"] && (double)kept["TerrainSize"] == 4096, "play_whitelist_terrain_collision_forced_off" );

        // A list of components: refused ones are removed and counted, kept ones come back sanitised, and a non-array is nothing at all.
        var list = new JsonArray( RealModelRenderer(), new JsonObject { ["__type"] = "MyGame.Evil", ["__guid"] = Guid1 }, JsonValue.Create( "junk" ), JsonNode.Parse( "{\"__type\":\"Sandbox.CameraComponent\",\"__guid\":\"" + Guid1 + "\"}" ) );
        var cleaned = SpectateWhitelist.SanitizeComponents( list, out int stripped );
        assert( cleaned.Count == 1 && stripped == 3 && (string)cleaned[0]["__type"] == "Sandbox.ModelRenderer", "play_whitelist_component_list_strips_and_counts" );
        assert( SpectateWhitelist.SanitizeComponents( null, out int none ).Count == 0 && none == 0 && SpectateWhitelist.SanitizeComponents( JsonNode.Parse( "{}" ), out int notArray ).Count == 0 && notArray == 0, "play_whitelist_component_list_non_array_is_empty" );
        var flood = new JsonArray();
        for ( int i = 0; i < SpectateWhitelist.MaxComponentsPerObject + 20; i++ ) flood.Add( RealModelRenderer() );
        assert( SpectateWhitelist.SanitizeComponents( flood, out _ ).Count == SpectateWhitelist.MaxComponentsPerObject, "play_whitelist_component_count_is_bounded" );
    }
}
