using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamCreate;

// Broad edit support: every object and component edit a person can make round-trips, and only structural safety rules
// (identity, shape, size, depth) refuse anything. The samples below are copied verbatim from scenes installed with s&box
// (the comment above each names the file), so the rules are exercised on what the engine really writes.
static class BroadEditTests
{
    const string Scene = "scenes/broad.scene";

    // download/assets/scenes/loadingscreen.21e679ff4bc8b3ae.scene
    const string PrefabInstanceJson = """
        {"__guid":"d0323837-bb0d-4c8f-a444-89f606281d1b","__version":2,"__Prefab":"prefabs/environment.prefab","__PrefabInstancePatch":{"AddedObjects":[],"RemovedObjects":[],"PropertyOverrides":[{"Target":{"Type":"GameObject","IdValue":"58f1e7d2-c4ee-4cf5-9ed3-fac1fb9da3f5"},"Property":"Position","Value":"162.731842,-72.2705917,59.8839417"},{"Target":{"Type":"GameObject","IdValue":"58f1e7d2-c4ee-4cf5-9ed3-fac1fb9da3f5"},"Property":"Enabled","Value":false},{"Target":{"Type":"GameObject","IdValue":"e195663e-ed91-49e0-89dd-7a2ddf8eef5e"},"Property":"Enabled","Value":false}],"MovedObjects":[]},"__PrefabIdToInstanceId":{"58f1e7d2-c4ee-4cf5-9ed3-fac1fb9da3f5":"d0323837-bb0d-4c8f-a444-89f606281d1b","104763ac-3304-499a-8f2f-00d5490ab792":"606c6416-ef2c-40d6-8e90-613d7d2a3f2e","f49ed6cc-d9d8-400e-bdac-3cb0f5030552":"b66ae8cd-9343-49b3-9cd0-e85b724b19e8","d9ed90e0-755a-4cfa-b2c5-597f48aebb36":"bdfdf474-f3eb-4e27-8652-bec4541e5760","c8601830-4072-4bd7-ad02-e8565c0a64cf":"0c698450-5b38-4365-9102-cb4dd20be08a","9bd41528-ea55-4952-8209-5e836dcf6937":"34924b90-5264-417d-abaa-4dae45fd2b10","675d48ae-d3e3-435b-a87e-36b76ae923aa":"cd6fb135-3386-42b0-aefc-349782591859","fd4c1eb9-1ecd-4eca-bda2-5755c9482c49":"9bebc97b-5e01-4ad1-a8bf-5f07967f91a1","a96e57d0-915c-44be-b45a-47f404a9baa9":"e7bd7f84-d268-41fb-83c1-7207883a9fd0","07e626de-64c0-43e9-b010-0ba6d08474cb":"cee7f8d8-c310-46af-b5a3-13949b7a9af6","e195663e-ed91-49e0-89dd-7a2ddf8eef5e":"14580d0b-9340-44a1-9a24-e4645e2f2f91","31556dca-d4e0-43c7-92b7-7e8582096caa":"7fbe2ea4-e83b-481d-aacc-89a012c49249","3e7a554e-179b-42dd-9a1d-694acd3638e8":"56e514f6-f8f7-4090-81f7-37d00813d697"}}
        """;

    // download/assets/scenes/pregamelobby.bff7fe0ef510ba14.scene
    const string MeshWithBlobJson = """
        {"__guid":"e02ff376-a832-4468-8271-ac166ebbb92e","__version":2,"Flags":0,"Name":"water","Position":"600.226501,549.214783,34.5750809","Rotation":"0,0,-0.382683516,0.923879504","Scale":"5,5,5","Tags":"world","Enabled":true,"NetworkMode":1,"NetworkFlags":0,"NetworkOrphaned":0,"NetworkTransmit":true,"OwnerTransfer":1,"Components":[{"__type":"Sandbox.MeshComponent","__guid":"2004dcd3-12a3-4338-a48e-d35c734d06ea","__enabled":true,"Flags":0,"ColliderFlags":0,"Collision":"Mesh","Color":"0.23688,0.2742,0.34884,1","Elasticity":null,"Friction":null,"HideInGame":false,"IsTrigger":false,"Mesh":{"Data":{"$blob":"3e85ca21-ade2-89a7-a712-dc95b056b8ed"},"Materials":["prefabs/city/water/water_map_t.vmat"]},"OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null,"OnObjectTriggerEnter":null,"OnObjectTriggerExit":null,"OnTriggerEnter":null,"OnTriggerExit":null,"RenderType":"On","RollingResistance":null,"SmoothingAngle":40,"Static":true,"Surface":null,"SurfaceVelocity":"0,0,0"}]}
        """;

    // download/assets/scenes/level2_blockout.6e3167983db15a7b.scene
    const string TerrainJson = """
        {"__guid":"2e80cacc-8b14-4f8d-98bc-6a37a7f9db79","__version":2,"Flags":32768,"Name":"middle","Position":"-4137.19824,-6321.30371,239","Rotation":"0,0,0,1","Scale":"1,1,1","Tags":"","Enabled":true,"NetworkMode":2,"NetworkFlags":0,"NetworkOrphaned":0,"NetworkTransmit":true,"OwnerTransfer":1,"Components":[{"__type":"Sandbox.Terrain","__guid":"4f5827ff-91b0-4cc5-8647-7735ba2dc562","__enabled":true,"Flags":0,"__version":1,"ClipMapLodExtentTexels":256,"ClipMapLodLevels":6,"ColliderFlags":0,"Elasticity":null,"EnableCollision":true,"Friction":null,"IsTrigger":false,"MaterialOverride":null,"OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null,"OnObjectTriggerEnter":null,"OnObjectTriggerExit":null,"OnTriggerEnter":null,"OnTriggerExit":null,"RenderType":"Off","RollingResistance":null,"Static":true,"Storage":"terrain/middle.terrain","SubdivisionFactor":1,"Surface":null,"SurfaceVelocity":"0,0,0","TerrainHeight":1000,"TerrainSize":9000}]}
        """;

    // download/assets/scenes/level2_blockout.6e3167983db15a7b.scene
    const string NetworkHelperJson = """
        {"__guid":"e315c5bd-9634-4a7a-8a61-71fd4e97502c","__version":2,"Flags":0,"Name":"Network","Position":"0,0,0","Rotation":"0,0,0,1","Scale":"1,1,1","Tags":"","Enabled":true,"NetworkMode":1,"NetworkFlags":0,"NetworkOrphaned":0,"NetworkTransmit":true,"OwnerTransfer":1,"Components":[{"__type":"Sandbox.NetworkHelper","__guid":"d955170c-6efe-4f22-8a26-7570bd44945c","__enabled":true,"Flags":0,"OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null,"PlayerPrefab":null,"SpawnPoints":[],"StartServer":true},{"__type":"CatchmeNetworkSpawner","__guid":"ca2c2ad4-f3b8-4bfe-9308-e74dc7a85c92","__enabled":true,"Flags":0,"OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null,"PlayerSpawnStartDelay":1.5,"PoliceHoldingSpawnPoint":{"_type":"gameobject","go":"bf22d761-d7dc-4a41-aa80-1f1bcf3b42a5"},"PolicePrefab":{"_type":"gameobject","prefab":"prefabs/player_police.prefab"},"PoliceSpawnPoints":[null],"ThiefHideoutRadarPoint":null,"ThiefHideoutSpawnPoints":[{"_type":"gameobject","go":"837203a6-2e62-41b6-9b58-99332f0f1c30"},{"_type":"gameobject","go":"40d3d9fb-6595-4a59-82b5-58a77abb1949"},{"_type":"gameobject","go":"cf2ba93a-6355-469e-bcf5-179dc1d05d2b"}],"ThiefOnlyCoopMode":true,"ThiefPrefab":{"_type":"gameobject","prefab":"prefabs/player_thief.prefab"},"ThiefSpawnPoints":[{"_type":"gameobject","go":"aa28f1fb-9e14-4cc9-b7fb-100189a3f91f"},{"_type":"gameobject","go":"187d1d2d-5120-4a29-9f6d-35cf0d01987c"},{"_type":"gameobject","go":"bfd2962d-ad84-4992-b5f8-c08c1e4a434c"}],"WaitForAllPlayersBeforeSpawning":true}]}
        """;

    // templates/game.playercontroller/Assets/scenes/basic_example.scene
    const string NetworkSettingsJson = """
        {"__guid":"633fa436-df01-42a2-8a30-6edbc7d3371b","__version":2,"Flags":0,"Name":"Main Camera","Position":"-300,0,100","Rotation":"0,0,0,1","Scale":"1,1,1","Tags":"","Enabled":true,"NetworkMode":2,"NetworkFlags":0,"NetworkOrphaned":0,"NetworkTransmit":true,"OwnerTransfer":1,"Components":[{"__type":"Sandbox.CameraComponent","__guid":"233da939-6378-40dd-af17-b2da0860f546","__enabled":true,"Flags":0,"BackgroundColor":"0.33333,0.46275,0.52157,1","ClearFlags":"All","EnablePostProcessing":true,"FieldOfView":60,"FovAxis":"Horizontal","IsMainCamera":true,"OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null,"Orthographic":false,"OrthographicHeight":1204,"PostProcessAnchor":null,"Priority":1,"RenderExcludeTags":"","RenderTags":"","RenderTexture":null,"TargetEye":"None","Viewport":"0,0,1,1","ZFar":10000,"ZNear":10}]}
        """;

    // templates/game.playercontroller/Assets/scenes/basic_example.scene
    const string AmbientLightJson = """
        {"__guid":"1b22c1a8-cca4-40c6-95bf-c6ba5588fc51","__version":2,"Flags":0,"Name":"Directional Light","Position":"0,0,200","Rotation":"-0.161728993,0.390448302,0.346828997,0.837319016","Scale":"1,1,1","Tags":"light_directional,light","Enabled":true,"NetworkMode":2,"NetworkFlags":0,"NetworkOrphaned":0,"NetworkTransmit":true,"OwnerTransfer":1,"Components":[{"__type":"Sandbox.DirectionalLight","__guid":"49c48e2d-e341-48f8-ac05-03d9f74bfe67","__enabled":true,"Flags":0,"FogMode":"Enabled","FogStrength":1,"LightColor":"0.91373,0.98039,1,1","OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null,"ShadowBias":0.0005,"ShadowCascadeCount":4,"ShadowCascadeSplitRatio":0.91,"ShadowHardness":0,"Shadows":true,"SkyColor":"0,0,0,0","Visualizer":{}},{"__type":"Sandbox.AmbientLight","__guid":"6ce3ad52-bcfd-4c3b-890e-53b35a83dd92","__enabled":true,"Flags":0,"Color":"0.23721,0.23721,0.23721,1","OnComponentDestroy":null,"OnComponentDisabled":null,"OnComponentEnabled":null,"OnComponentFixedUpdate":null,"OnComponentStart":null,"OnComponentUpdate":null}]}
        """;

    public static void Run( Action<bool, string> assert )
    {
        RealSamplesPassAsCreateEditDelete( assert );
        BroadEditsAreAccepted( assert );
        AuthorityKeepsConcurrentEditsToDifferentFields( assert );
        PrefabPatchIsOneMergeUnit( assert );
        StructuralRefusals( assert );
        LimitsAreInclusive( assert );
        ReservedKeys( assert );
        DepthLimitSurvivesEveryWrapper( assert );
        SiblingOrderRules( assert );
        SendableSplit( assert );
        Notices( assert );
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    static ObjectState Sample( string json )
    {
        var data = JsonNode.Parse( json )!.AsObject();
        return new ObjectState( data["__guid"]!.GetValue<string>(), "", data );
    }

    static ObjectState[] All() => new[] { PrefabInstanceJson, MeshWithBlobJson, TerrainJson, NetworkHelperJson, NetworkSettingsJson, AmbientLightJson }.Select( Sample ).ToArray();

    static ObjectState Edited( ObjectState state, Action<JsonObject> edit )
    {
        var data = (JsonObject)state.Data.DeepClone();
        edit( data );
        return state with { Data = data };
    }

    static JsonArray Components( JsonObject data ) => data["Components"]!.AsArray();

    static JsonObject Component( JsonObject data, int index ) => Components( data )[index]!.AsObject();

    static string Refusal( ObjectState before, ObjectState after ) => StateRules.RefusalOf( new Change( ( before ?? after ).Id, before, after ) );

    static Packet Edit( string peer, params Change[] changes ) => new Packet
    {
        Kind = "edit",
        Id = Guid.NewGuid().ToString(),
        Peer = peer,
        Scene = Scene,
        Changes = changes.ToList(),
        Final = true
    };

    static Authority Seeded( params ObjectState[] objects )
    {
        var authority = new Authority( _ => { } );
        authority.Subscribe( new Packet { Kind = "subscribe", Scene = Scene, Objects = objects.ToList() } );
        return authority;
    }

    static ObjectState Host( Authority authority, string id ) => authority.Scenes[Scene][id];

    static JsonObject Nested( int depth )
    {
        // Depth 1 is the data object itself; each nested object adds one level.
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

    // ---- Real samples --------------------------------------------------------------------------------

    static void RealSamplesPassAsCreateEditDelete( Action<bool, string> assert )
    {
        foreach ( var sample in All() )
        {
            string label = sample.Data["Name"]?.GetValue<string>() ?? "prefab instance";
            assert( Refusal( null, sample ) == null, "broad_real_sample_is_accepted_as_a_creation: " + label + " " + Refusal( null, sample ) );
            assert( Refusal( sample, null ) == null, "broad_real_sample_is_accepted_as_a_deletion: " + label + " " + Refusal( sample, null ) );
            var edited = Edited( sample, d =>
            {
                d[SiblingOrder.Key] = 3;
                if ( d["Components"] is JsonArray components && components.Count > 0 )
                {
                    components[0]!["__enabled"] = false;
                }
                else
                {
                    d["__PrefabInstancePatch"]!["PropertyOverrides"]!.AsArray().RemoveAt( 0 );
                }
            } );
            assert( Refusal( sample, edited ) == null, "broad_real_sample_is_accepted_as_an_edit: " + label + " " + Refusal( sample, edited ) );
        }
    }

    static void BroadEditsAreAccepted( Action<bool, string> assert )
    {
        var camera = Sample( NetworkSettingsJson );
        var light = Sample( AmbientLightJson );
        var mesh = Sample( MeshWithBlobJson );
        var terrain = Sample( TerrainJson );
        var helper = Sample( NetworkHelperJson );

        var added = Edited( camera, d =>
        {
            var ambient = (JsonObject)Component( light.Data, 1 ).DeepClone();
            ambient["__guid"] = Guid.NewGuid().ToString();
            Components( d ).Add( ambient );
        } );
        assert( Refusal( camera, added ) == null, "broad_component_addition_is_accepted: " + Refusal( camera, added ) );
        var removed = Edited( light, d => Components( d ).RemoveAt( 1 ) );
        assert( Refusal( light, removed ) == null, "broad_component_removal_is_accepted: " + Refusal( light, removed ) );
        var reordered = Edited( light, d =>
        {
            var first = Components( d )[0]!.DeepClone();
            Components( d ).RemoveAt( 0 );
            Components( d ).Add( first );
        } );
        assert( Refusal( light, reordered ) == null, "broad_component_reorder_is_accepted: " + Refusal( light, reordered ) );
        var network = Edited( camera, d =>
        {
            d["NetworkMode"] = 1;
            d["NetworkFlags"] = 3;
            d["NetworkOrphaned"] = 2;
            d["NetworkTransmit"] = false;
            d["OwnerTransfer"] = 0;
        } );
        assert( Refusal( camera, network ) == null, "broad_network_settings_edit_is_accepted: " + Refusal( camera, network ) );
        var materials = Edited( mesh, d =>
        {
            Component( d, 0 )["Mesh"]!["Materials"] = new JsonArray( "materials/dev/reflectivity_30.vmat", "materials/dev/reflectivity_50.vmat" );
            Component( d, 0 )["Mesh"]!["Data"]!["$blob"] = Guid.NewGuid().ToString();
        } );
        assert( Refusal( mesh, materials ) == null, "broad_nested_mesh_data_edit_is_accepted: " + Refusal( mesh, materials ) );
        var storage = Edited( terrain, d => Component( d, 0 )["Storage"] = "terrain/other.terrain" );
        assert( Refusal( terrain, storage ) == null, "broad_terrain_storage_edit_is_accepted: " + Refusal( terrain, storage ) );
        var script = Edited( helper, d =>
        {
            Component( d, 1 )["PlayerSpawnStartDelay"] = 2.5;
            Component( d, 1 )["ThiefSpawnPoints"]!.AsArray().RemoveAt( 0 );
            Component( d, 1 )["ThiefHideoutRadarPoint"] = new JsonObject { ["_type"] = "gameobject", ["go"] = Guid.NewGuid().ToString() };
        } );
        assert( Refusal( helper, script ) == null, "broad_script_component_property_edit_is_accepted: " + Refusal( helper, script ) );
        var anyField = Edited( camera, d => d["Mass"] = 0.61 );
        assert( Refusal( camera, anyField ) == null, "broad_any_object_property_is_accepted: " + Refusal( camera, anyField ) );
        var foreignType = Edited( camera, d => Components( d ).Add( new JsonObject { ["__type"] = "MyGame.SomethingNew", ["__guid"] = Guid.NewGuid().ToString(), ["Speed"] = 4 } ) );
        assert( Refusal( null, foreignType ) == null && Refusal( camera, foreignType ) == null, "broad_any_component_type_is_accepted: " + Refusal( camera, foreignType ) );
        var noComponents = Edited( camera, d => d.Remove( "Components" ) );
        assert( Refusal( camera, noComponents ) == null, "broad_removing_every_component_is_accepted: " + Refusal( camera, noComponents ) );
    }

    // ---- Authority round trips -----------------------------------------------------------------------

    static void AuthorityKeepsConcurrentEditsToDifferentFields( Action<bool, string> assert )
    {
        {
            var helper = Sample( NetworkHelperJson );
            var authority = Seeded( All() );
            var fromA = authority.Apply( Edit( "peer-a", new Change( helper.Id, helper, Edited( helper, d => Component( d, 0 )["StartServer"] = false ) ) ) );
            // B never saw A's edit: its Before is the seeded state.
            var fromB = authority.Apply( Edit( "peer-b", new Change( helper.Id, helper, Edited( helper, d => { Component( d, 1 )["PlayerSpawnStartDelay"] = 2.5; d["NetworkMode"] = 2; } ) ) ) );
            var held = Host( authority, helper.Id ).Data;
            assert( fromA.Kind == "accepted" && fromB.Kind == "accepted" && fromB.Merged, "broad_authority_accepts_both_concurrent_script_edits: " + fromA.Kind + " " + fromA.Error + " / " + fromB.Kind + " " + fromB.Error );
            assert( Component( held, 0 )["StartServer"]!.GetValue<bool>() == false && Component( held, 1 )["PlayerSpawnStartDelay"]!.GetValue<double>() == 2.5 && held["NetworkMode"]!.GetValue<int>() == 2, "broad_authority_keeps_both_different_field_edits: " + held.ToJsonString() );
            assert( fromB.Notice == null, "broad_different_fields_are_not_a_clash: " + fromB.Notice );
        }
        {
            var camera = Sample( NetworkSettingsJson );
            var light = Sample( AmbientLightJson );
            var authority = Seeded( All() );
            var ambient = (JsonObject)Component( light.Data, 1 ).DeepClone();
            ambient["__guid"] = Guid.NewGuid().ToString();
            authority.Apply( Edit( "peer-a", new Change( camera.Id, camera, Edited( camera, d => Components( d ).Add( ambient.DeepClone() ) ) ) ) );
            var fov = authority.Apply( Edit( "peer-b", new Change( camera.Id, camera, Edited( camera, d => Component( d, 0 )["FieldOfView"] = 80 ) ) ) );
            var held = Host( authority, camera.Id ).Data;
            assert( fov.Kind == "accepted" && Components( held ).Count == 2 && Component( held, 1 )["__guid"]!.GetValue<string>() == ambient["__guid"]!.GetValue<string>() && Component( held, 0 )["FieldOfView"]!.GetValue<int>() == 80, "broad_component_added_by_one_and_field_edited_by_another_both_survive: " + fov.Error + " " + held.ToJsonString() );

            var later = authority.Apply( Edit( "peer-c", new Change( camera.Id, camera, Edited( camera, d => Component( d, 0 )["FieldOfView"] = 90 ) ) ) );
            assert( later.Kind == "accepted" && Component( Host( authority, camera.Id ).Data, 0 )["FieldOfView"]!.GetValue<int>() == 90 && later.Notice != null && later.Notice.Contains( "CameraComponent.FieldOfView", StringComparison.Ordinal ), "broad_same_field_later_edit_wins_and_is_named: " + later.Notice );
        }
        {
            var light = Sample( AmbientLightJson );
            var authority = Seeded( light );
            var swapped = Edited( light, d =>
            {
                var first = Components( d )[0]!.DeepClone();
                Components( d ).RemoveAt( 0 );
                Components( d ).Add( first );
            } );
            authority.Apply( Edit( "peer-a", new Change( light.Id, light, swapped ) ) );
            var tint = authority.Apply( Edit( "peer-b", new Change( light.Id, light, Edited( light, d => Component( d, 1 )["Color"] = "1,0,0,1" ) ) ) );
            var held = Host( authority, light.Id ).Data;
            assert( tint.Kind == "accepted" && Component( held, 0 )["__type"]!.GetValue<string>() == "Sandbox.AmbientLight" && Component( held, 0 )["Color"]!.GetValue<string>() == "1,0,0,1", "broad_component_reorder_and_field_edit_both_survive: " + held.ToJsonString() );
        }
        {
            var camera = Sample( NetworkSettingsJson );
            var authority = Seeded( camera );
            authority.Apply( Edit( "peer-a", new Change( camera.Id, camera, Edited( camera, d => d[SiblingOrder.Key] = 4 ) ) ) );
            var moved = authority.Apply( Edit( "peer-b", new Change( camera.Id, camera, Edited( camera, d => d["Position"] = "0,0,64" ) ) ) );
            var held = Host( authority, camera.Id ).Data;
            assert( moved.Kind == "accepted" && held[SiblingOrder.Key]!.GetValue<int>() == 4 && held["Position"]!.GetValue<string>() == "0,0,64", "broad_sibling_order_merges_as_an_ordinary_field: " + held.ToJsonString() );
            var reorder = authority.Apply( Edit( "peer-c", new Change( camera.Id, camera, Edited( camera, d => d[SiblingOrder.Key] = 7 ) ) ) );
            assert( reorder.Kind == "accepted" && reorder.Notice != null && reorder.Notice.Contains( "Sibling order", StringComparison.Ordinal ) && !reorder.Notice.Contains( SiblingOrder.Key, StringComparison.Ordinal ), "broad_sibling_order_clash_is_named_for_people: " + reorder.Notice );
        }
    }

    static void PrefabPatchIsOneMergeUnit( Action<bool, string> assert )
    {
        var prefab = Sample( PrefabInstanceJson );
        var authority = Seeded( prefab );
        var fromA = Edited( prefab, d => d["__PrefabInstancePatch"]!["PropertyOverrides"]!.AsArray().RemoveAt( 0 ) );
        var fromB = Edited( prefab, d => d["__PrefabInstancePatch"]!["PropertyOverrides"]!.AsArray().RemoveAt( 2 ) );
        var first = authority.Apply( Edit( "peer-a", new Change( prefab.Id, prefab, fromA ) ) );
        var second = authority.Apply( Edit( "peer-b", new Change( prefab.Id, prefab, fromB ) ) );
        var held = Host( authority, prefab.Id ).Data;
        assert( first.Kind == "accepted" && second.Kind == "accepted", "broad_prefab_patch_edits_are_accepted: " + first.Error + " " + second.Error );
        // Not combined: B's patch replaces A's as a whole, because a patch is a list of overrides the engine reads together.
        assert( StateRules.ValuesEqual( held["__PrefabInstancePatch"], fromB.Data["__PrefabInstancePatch"] ) && held["__PrefabInstancePatch"]!["PropertyOverrides"]!.AsArray().Count == 2, "broad_prefab_patch_later_edit_wins_as_a_whole: " + held["__PrefabInstancePatch"]!.ToJsonString() );
        assert( second.Notice != null && second.Notice.Contains( "Prefab overrides", StringComparison.Ordinal ) && !second.Notice.Contains( "__PrefabInstancePatch", StringComparison.Ordinal ), "broad_prefab_patch_clash_is_named_for_people: " + second.Notice );
    }

    // ---- Structural refusals -------------------------------------------------------------------------

    static void StructuralRefusals( Action<bool, string> assert )
    {
        var camera = Sample( NetworkSettingsJson );
        string Name( string rest ) => "The object \"Main Camera\" " + rest;

        var otherGuid = Edited( camera, d => d["__guid"] = Guid.NewGuid().ToString() );
        assert( Refusal( camera, otherGuid ) != null, "broad_refuses_a_guid_change: " + Refusal( camera, otherGuid ) );
        var otherId = otherGuid with { Id = otherGuid.Data["__guid"]!.GetValue<string>() };
        assert( StateRules.RefusalOf( new Change( camera.Id, camera, otherId ) ) != null, "broad_refuses_an_edit_whose_before_and_after_ids_differ" );
        var notGuid = new ObjectState( "cube", "", new JsonObject { ["__guid"] = "cube" } );
        assert( Refusal( null, notGuid ) != null, "broad_refuses_an_id_that_is_not_a_guid" );
        var noData = camera with { Data = null };
        assert( Refusal( null, noData ) != null, "broad_refuses_an_object_without_data" );

        var duplicate = Edited( camera, d =>
        {
            var copy = (JsonObject)Component( d, 0 ).DeepClone();
            Components( d ).Add( copy );
        } );
        assert( Refusal( camera, duplicate ) == Name( "has two components with the identifier " + Component( camera.Data, 0 )["__guid"]!.GetValue<string>() + " and was not sent." ), "broad_refuses_a_duplicate_component_guid: " + Refusal( camera, duplicate ) );
        var untyped = Edited( camera, d => Component( d, 0 ).Remove( "__type" ) );
        assert( Refusal( camera, untyped ) == Name( "has a component without a type name and was not sent." ), "broad_refuses_a_component_without_type: " + Refusal( camera, untyped ) );
        var emptyType = Edited( camera, d => Component( d, 0 )["__type"] = "" );
        assert( Refusal( camera, emptyType ) == Name( "has a component without a type name and was not sent." ), "broad_refuses_an_empty_component_type: " + Refusal( camera, emptyType ) );
        var numberType = Edited( camera, d => Component( d, 0 )["__type"] = 5 );
        assert( Refusal( camera, numberType ) == Name( "has a component without a type name and was not sent." ), "broad_refuses_a_component_type_that_is_not_text: " + Refusal( camera, numberType ) );
        var longType = Edited( camera, d => Component( d, 0 )["__type"] = new string( 'T', StateRules.MaxTypeNameLength + 1 ) );
        assert( Refusal( camera, longType ) == Name( "has a component type name longer than 200 characters and was not sent." ), "broad_refuses_an_overlong_component_type: " + Refusal( camera, longType ) );
        var badGuid = Edited( camera, d => Component( d, 0 )["__guid"] = "not-a-guid" );
        assert( Refusal( camera, badGuid ) == Name( "has a component without a valid identifier and was not sent." ), "broad_refuses_a_component_without_a_valid_guid: " + Refusal( camera, badGuid ) );
        var notObject = Edited( camera, d => Components( d ).Add( 5 ) );
        assert( Refusal( camera, notObject ) == Name( "has a component entry that is not an object and was not sent." ), "broad_refuses_a_component_entry_that_is_not_an_object: " + Refusal( camera, notObject ) );
        var notArray = Edited( camera, d => d["Components"] = new JsonObject() );
        assert( Refusal( camera, notArray ) == Name( "has a component list that is not a list and was not sent." ), "broad_refuses_components_that_are_not_a_list: " + Refusal( camera, notArray ) );

        var crowded = Edited( camera, d =>
        {
            for ( int i = Components( d ).Count; i < StateRules.MaxComponents + 1; i++ )
            {
                Components( d ).Add( new JsonObject { ["__type"] = "Sandbox.Tag", ["__guid"] = Guid.NewGuid().ToString() } );
            }
        } );
        assert( Refusal( camera, crowded ) == Name( "has more than 256 components, beyond the collaboration limit, and was not sent." ), "broad_refuses_257_components: " + Refusal( camera, crowded ) );

        var huge = Edited( camera, d =>
        {
            for ( int i = 0; i < 5; i++ )
            {
                d["Blob" + i] = new string( 'x', 220 * 1024 );
            }
        } );
        assert( Refusal( camera, huge ) == Name( "is larger than the 1 MiB collaboration limit and was not sent." ), "broad_refuses_an_oversize_object: " + Refusal( camera, huge ) );
        assert( Refusal( null, huge ) != null, "broad_refuses_an_oversize_object_as_a_creation" );
        // Only what is sent is measured: the Before of an edit or a deletion is the state the host already holds, so an object
        // that is already too large (a seeded scene is not checked) can still be shrunk or deleted.
        assert( Refusal( huge, camera ) == null && Refusal( huge, null ) == null, "broad_an_oversize_before_does_not_block_shrinking_or_deleting: " + Refusal( huge, camera ) + " " + Refusal( huge, null ) );

        var deep = Edited( camera, d => d["Graph"] = Nested( StateRules.MaxDataDepth ) );
        assert( Refusal( camera, deep ) == Name( "is nested more than 56 levels deep, beyond the collaboration limit, and was not sent." ), "broad_refuses_overdepth_json: " + Refusal( camera, deep ) );
        var deepArray = Edited( camera, d =>
        {
            JsonNode inner = new JsonArray();
            // The data object is level 1 and Lists' outer array level 2, so 56 nested arrays reach level 57.
            for ( int i = 1; i < StateRules.MaxDataDepth; i++ )
            {
                inner = new JsonArray( inner );
            }
            d["Lists"] = inner;
        } );
        assert( Refusal( camera, deepArray ) != null, "broad_refuses_overdepth_json_made_of_arrays: " + Refusal( camera, deepArray ) );

        var longText = Edited( camera, d => d["Notes"] = new string( 'x', StateRules.MaxStringBytes + 1 ) );
        assert( Refusal( camera, longText ) == Name( "contains a text value larger than the 256 KiB collaboration limit and was not sent." ), "broad_refuses_an_oversize_string: " + Refusal( camera, longText ) );
        // UTF-8 bytes, not characters: a string of 3-byte characters is measured by what it costs on the wire.
        var wideText = Edited( camera, d => d["Notes"] = new string( '\u20AC', StateRules.MaxStringBytes / 3 + 1 ) );
        assert( Refusal( camera, wideText ) != null, "broad_string_limit_counts_utf8_bytes: " + Refusal( camera, wideText ) );
        var nestedText = Edited( camera, d => Component( d, 0 )["Notes"] = new JsonArray( new string( 'x', StateRules.MaxStringBytes + 1 ) ) );
        assert( Refusal( camera, nestedText ) != null, "broad_refuses_an_oversize_string_inside_a_component" );

        var emptyKey = Edited( camera, d => Component( d, 0 )[""] = 1 );
        assert( Refusal( camera, emptyKey ) == Name( "contains an empty property name and was not sent." ), "broad_refuses_an_empty_key: " + Refusal( camera, emptyKey ) );
        var longKey = Edited( camera, d => d[new string( 'k', StateRules.MaxKeyLength + 1 )] = 1 );
        assert( Refusal( camera, longKey ) == Name( "contains a property name longer than 200 characters and was not sent." ), "broad_refuses_an_overlong_key: " + Refusal( camera, longKey ) );

        try
        {
            StateRules.ValidateChange( new Change( camera.Id, camera, huge ) );
            assert( false, "broad_validate_change_throws_the_refusal" );
        }
        catch ( InvalidOperationException ex )
        {
            assert( ex.Message == Refusal( camera, huge ), "broad_validate_change_throws_the_refusal: " + ex.Message );
        }
        bool emptyThrows = false;
        try { StateRules.ValidateChange( new Change( camera.Id, null, null ) ); } catch ( InvalidOperationException ) { emptyThrows = true; }
        assert( emptyThrows, "broad_change_without_before_or_after_is_still_refused" );

        // The host applies the same rules: a refused change leaves its scene untouched.
        var authority = Seeded( camera );
        var receipt = authority.Apply( Edit( "peer-a", new Change( camera.Id, camera, huge ) ) );
        assert( receipt.Kind == "reject" && receipt.Error == Refusal( camera, huge ) && StateRules.Equal( Host( authority, camera.Id ), camera ), "broad_host_rejects_a_structural_refusal_and_keeps_its_state: " + receipt.Kind + " " + receipt.Error );

        // A name that is not text, or none at all, falls back to the id.
        var unnamed = Edited( camera, d => { d.Remove( "Name" ); d["Notes"] = new string( 'x', StateRules.MaxStringBytes + 1 ); } );
        assert( Refusal( camera, unnamed ) == "The object \"" + camera.Id + "\" contains a text value larger than the 256 KiB collaboration limit and was not sent.", "broad_refusal_names_the_id_when_there_is_no_name: " + Refusal( camera, unnamed ) );
    }

    static void LimitsAreInclusive( Action<bool, string> assert )
    {
        var camera = Sample( NetworkSettingsJson );
        assert( StateRules.MaxObjectBytes == 1024 * 1024 && StateRules.MaxComponents == 256 && StateRules.MaxStringBytes == 256 * 1024 && StateRules.MaxKeyLength == 200 && StateRules.MaxTypeNameLength == 200 && StateRules.MaxDataDepth == 56, "broad_limits_have_the_documented_values" );
        var full = Edited( camera, d =>
        {
            for ( int i = Components( d ).Count; i < StateRules.MaxComponents; i++ )
            {
                Components( d ).Add( new JsonObject { ["__type"] = "Sandbox.Tag", ["__guid"] = Guid.NewGuid().ToString() } );
            }
        } );
        assert( Refusal( camera, full ) == null, "broad_accepts_exactly_256_components: " + Refusal( camera, full ) );
        var deep = Edited( camera, d => d["Graph"] = Nested( StateRules.MaxDataDepth - 1 ) );
        assert( Refusal( camera, deep ) == null, "broad_accepts_json_at_exactly_the_depth_limit: " + Refusal( camera, deep ) );
        var text = Edited( camera, d => d["Notes"] = new string( 'x', StateRules.MaxStringBytes ) );
        assert( Refusal( camera, text ) == null, "broad_accepts_a_string_of_exactly_256_kib: " + Refusal( camera, text ) );
        var key = Edited( camera, d => d[new string( 'k', StateRules.MaxKeyLength )] = 1 );
        assert( Refusal( camera, key ) == null, "broad_accepts_a_key_of_exactly_200_characters: " + Refusal( camera, key ) );
        var type = Edited( camera, d => Component( d, 0 )["__type"] = new string( 'T', StateRules.MaxTypeNameLength ) );
        assert( Refusal( camera, type ) == null, "broad_accepts_a_type_name_of_exactly_200_characters: " + Refusal( camera, type ) );
        // Just under one MiB once serialised as the wire writes it.
        var big = Edited( camera, d =>
        {
            for ( int i = 0; i < 3; i++ )
            {
                d["Blob" + i] = new string( 'x', 256 * 1024 );
            }
        } );
        int bytes = JsonSerializer.SerializeToUtf8Bytes( big ).Length;
        assert( bytes < StateRules.MaxObjectBytes && Refusal( camera, big ) == null, "broad_accepts_an_object_just_under_one_mib: " + bytes + " " + Refusal( camera, big ) );
    }

    static void ReservedKeys( Action<bool, string> assert )
    {
        foreach ( var sample in All() )
        {
            var unknown = StateRules.FindUnknownReservedKeys( sample.Data );
            assert( unknown.Count == 0, "broad_real_samples_use_only_known_reserved_keys: " + string.Join( ",", unknown ) );
        }
        foreach ( var key in new[] { "__guid", "__type", "__enabled", "__version", "__Prefab", "__PrefabInstancePatch", "__PrefabIdToInstanceId", "__PrefabVariant", "__PrefabVariables", "__properties", "__variables", "__references", "__snapshot", "__NetworkedPrefaInstance", "__EditorPrefabNestedInstance", "__EditorSkipPrefabBreakOnRefresh", SiblingOrder.Key } )
        {
            assert( StateRules.KnownReservedKeys.Contains( key ), "broad_known_reserved_key_is_listed: " + key );
        }
        var camera = Sample( NetworkSettingsJson );
        var future = Edited( camera, d =>
        {
            d["__futureEngineKey"] = 1;
            Component( d, 0 )["__anotherOne"] = new JsonObject { ["__futureEngineKey"] = true };
        } );
        assert( Refusal( camera, future ) == null, "broad_unknown_reserved_keys_are_allowed" );
        var found = StateRules.FindUnknownReservedKeys( future.Data );
        assert( found.SequenceEqual( new[] { "__anotherOne", "__futureEngineKey" } ), "broad_unknown_reserved_keys_are_counted_once_each_in_order: " + string.Join( ",", found ) );
    }

    // System.Text.Json refuses to write or read deeper than 64 levels. An object is sent inside a packet and stored inside the
    // outbox wrapper and the failure report, so its own data must leave room for every one of those envelopes.
    static void DepthLimitSurvivesEveryWrapper( Action<bool, string> assert )
    {
        var data = Nested( StateRules.MaxDataDepth );
        var id = Guid.NewGuid().ToString();
        data["__guid"] = id;
        var state = new ObjectState( id, "", data );
        assert( Refusal( null, state ) == null, "broad_depth_probe_is_at_the_limit" );
        var packet = new Packet { Kind = "edit", Peer = "p", Scene = Scene, Changes = new List<Change> { new Change( id, null, state ) } };
        bool ok = true;
        try
        {
            var wire = JsonSerializer.SerializeToUtf8Bytes( packet );
            ok &= JsonSerializer.Deserialize<Packet>( wire ) != null;
            var outbox = JsonSerializer.Serialize( new { project = "p", baseline = "b", packet } );
            ok &= JsonDocument.Parse( outbox ) != null;
            var report = JsonSerializer.Serialize( new { reason = "r", captures = new Dictionary<string, Dictionary<string, ObjectState>> { [Scene] = new Dictionary<string, ObjectState> { [id] = state } }, pending = new List<Packet> { packet } } );
            ok &= JsonDocument.Parse( report ) != null;
        }
        catch ( Exception ex )
        {
            ok = false;
            assert( false, "broad_depth_limit_survives_every_wrapper: " + ex.Message );
        }
        assert( ok, "broad_depth_limit_survives_every_wrapper" );
    }

    // ---- Sibling order -------------------------------------------------------------------------------

    static void SiblingOrderRules( Action<bool, string> assert )
    {
        var data = new JsonObject { ["__guid"] = "g", [SiblingOrder.Key] = 2, ["Children"] = new JsonArray(), ["Name"] = "N" };
        var engine = SiblingOrder.ForEngine( data );
        assert( !engine.ContainsKey( SiblingOrder.Key ) && !engine.ContainsKey( "Children" ) && engine["Name"]!.GetValue<string>() == "N", "broad_order_key_and_children_are_stripped_for_the_engine" );
        assert( data.ContainsKey( SiblingOrder.Key ) && data.ContainsKey( "Children" ), "broad_stripping_does_not_touch_the_tracked_state" );
        assert( SiblingOrder.Read( data ) == 2 && SiblingOrder.Read( new JsonObject() ) == null && SiblingOrder.Read( new JsonObject { [SiblingOrder.Key] = "2" } ) == null && SiblingOrder.Read( new JsonObject { [SiblingOrder.Key] = 1.5 } ) == null, "broad_order_is_read_only_from_a_whole_number" );

        var orders = new Dictionary<string, int?> { ["a"] = 2, ["b"] = 0, ["c"] = 1 };
        assert( SiblingOrder.Arrange( new[] { "a", "b", "c" }, orders ).SequenceEqual( new[] { "b", "c", "a" } ), "broad_siblings_are_arranged_by_their_order" );
        var tie = new Dictionary<string, int?> { ["y"] = 0, ["x"] = 0 };
        assert( SiblingOrder.Arrange( new[] { "y", "x" }, tie ).SequenceEqual( new[] { "x", "y" } ) && SiblingOrder.Arrange( new[] { "x", "y" }, tie ).SequenceEqual( new[] { "x", "y" } ), "broad_equal_orders_are_broken_by_id_the_same_on_every_machine" );
        var partial = new Dictionary<string, int?> { ["a"] = null, ["b"] = 0 };
        assert( SiblingOrder.Arrange( new[] { "a", "b" }, partial ).SequenceEqual( new[] { "b", "a" } ) && SiblingOrder.Arrange( new[] { "b", "a" }, new Dictionary<string, int?>() ).SequenceEqual( new[] { "b", "a" } ), "broad_siblings_without_an_order_follow_the_ordered_ones_in_their_current_order" );
    }

    // ---- What the scan loop sends ------------------------------------------------------------------

    static void SendableSplit( Action<bool, string> assert )
    {
        var parent = Sample( NetworkSettingsJson );
        var child = Edited( Sample( AmbientLightJson ), d => { } ) with { Parent = parent.Id };
        var baseline = new Dictionary<string, ObjectState> { [parent.Id] = parent, [child.Id] = child };

        var bigId = Guid.NewGuid().ToString();
        var big = new ObjectState( bigId, "", new JsonObject { ["__guid"] = bigId, ["Name"] = "Big", ["Blob"] = new string( 'x', StateRules.MaxStringBytes + 1 ) } );
        var underId = Guid.NewGuid().ToString();
        var under = new ObjectState( underId, bigId, new JsonObject { ["__guid"] = underId, ["Name"] = "Under" } );
        var deeperId = Guid.NewGuid().ToString();
        var deeper = new ObjectState( deeperId, underId, new JsonObject { ["__guid"] = deeperId, ["Name"] = "Deeper" } );
        var moved = Edited( child, d => d["Position"] = "1,2,3" );
        var changes = new List<Change>
        {
            new Change( bigId, null, big ),
            new Change( child.Id, child, moved ),
            new Change( deeperId, null, deeper ),
            new Change( underId, null, under ),
            new Change( parent.Id, parent, Edited( parent, d => d["Name"] = "Renamed" ) )
        };
        var held = new List<HeldChange>();
        var sendable = StateRules.SendableChanges( baseline, changes, held );
        assert( sendable.Select( x => x.Id ).SequenceEqual( new[] { child.Id, parent.Id } ), "broad_valid_changes_are_still_sent_in_order: " + string.Join( ",", sendable.Select( x => x.Id ) ) );
        assert( held.Count == 3 && held[0].Change.Id == bigId && held[0].Reason == "The object \"Big\" contains a text value larger than the 256 KiB collaboration limit and was not sent.", "broad_a_refused_change_is_held_with_its_reason: " + string.Join( " | ", held.Select( x => x.Reason ) ) );
        assert( held.Any( x => x.Change.Id == underId && x.Reason == "The object \"Under\" was not sent because its parent \"Big\" was not sent." ) && held.Any( x => x.Change.Id == deeperId && x.Reason == "The object \"Deeper\" was not sent because its parent \"Under\" was not sent." ), "broad_children_of_a_held_object_are_held_too: " + string.Join( " | ", held.Select( x => x.Reason ) ) );
        var result = new Dictionary<string, ObjectState>( baseline );
        foreach ( var change in sendable )
        {
            result[change.Id] = change.After;
        }
        bool graphOk = true;
        try { StateRules.ValidateGraph( result ); } catch ( InvalidOperationException ) { graphOk = false; }
        assert( graphOk, "broad_what_is_sent_keeps_the_graph_valid" );

        // A deletion is always sent, and nothing is held when everything is valid.
        var none = new List<HeldChange>();
        var deletion = StateRules.SendableChanges( baseline, new[] { new Change( child.Id, child, null ) }, none );
        assert( deletion.Count == 1 && none.Count == 0, "broad_a_deletion_is_sent" );
        // Moving a child under an object whose creation is held keeps the child where the session has it.
        var underBig = new List<HeldChange>();
        var reparent = StateRules.SendableChanges( baseline, new[] { new Change( bigId, null, big ), new Change( child.Id, child, child with { Parent = bigId } ) }, underBig );
        assert( reparent.Count == 0 && underBig.Count == 2, "broad_a_move_under_a_held_object_is_held: " + reparent.Count + " " + underBig.Count );
        // Deleting a parent whose child's move away was held would leave that child pointing at nothing on the host.
        var orphaning = new List<HeldChange>();
        var oversizeMove = child with { Parent = "", Data = Edited( child, d => d["Notes"] = new string( 'x', StateRules.MaxStringBytes + 1 ) ).Data };
        var orphan = StateRules.SendableChanges( baseline, new[] { new Change( child.Id, child, oversizeMove ), new Change( parent.Id, parent, null ) }, orphaning );
        assert( orphan.Count == 0 && orphaning.Count == 2 && orphaning[1].Reason == "The deletion of \"Main Camera\" was not sent because its child \"Directional Light\" was not sent.", "broad_a_deletion_that_would_orphan_a_held_child_is_held: " + string.Join( " | ", orphaning.Select( x => x.Reason ) ) );
    }

    static void Notices( Action<bool, string> assert )
    {
        assert( StateRules.MissingComponentTypeNotice( "MyGame.Spinner" ) == "The component type \"MyGame.Spinner\" is not available in your project. Update your code to see it.", "broad_missing_component_type_notice_is_formal" );
        assert( StateRules.NoLongerSavedNotice( "Cube" ) == "The object \"Cube\" can no longer be saved with the scene, so it was removed from the shared scene.", "broad_no_longer_saved_notice_is_formal" );
    }
}
