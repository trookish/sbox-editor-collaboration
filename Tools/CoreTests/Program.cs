using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TeamCreate;

int passed = 0;
void Assert(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
ObjectState Obj(string name, string parent = "") { var id = Guid.NewGuid().ToString(); return new(id, parent, new JsonObject { ["__guid"] = id, ["Name"] = name, ["Components"] = new JsonArray() }); }
ObjectState Rename(ObjectState obj, string name) { var data = (JsonObject)obj.Data.DeepClone(); data["Name"] = name; return obj with { Data = data }; }
Packet Edit(string peer, params Change[] changes) => new() { Kind="edit", Peer=peer, Scene="scenes/test.scene", Changes=changes.ToList(), Final=true };
ObjectState RuntimeObj(string name, string parent = "", params JsonObject[] components) =>
    RuntimeObjWithId(name, Guid.NewGuid().ToString(), parent, components);
ObjectState RuntimeObjWithId(string name, string id, string parent = "", params JsonObject[] components)
{
    var componentArray = new JsonArray();
    foreach (var component in components) componentArray.Add(component);
    return new ObjectState(id, parent, new JsonObject
    {
        ["__guid"] = id,
        ["Name"] = name,
        ["Components"] = componentArray
    });
}
JsonObject RuntimeComponent(string type, params (string Name, JsonNode Value)[] fields) =>
    RuntimeComponentWithId(type, Guid.NewGuid().ToString(), fields);
Packet RuntimeSnapshotPacket(
    string id,
    string peer,
    IEnumerable<ObjectState> objects,
    long sequence,
    long tick,
    string runId = "run-1",
    string scene = "scenes/runtime.scene",
    int? version = Protocol.CurrentVersion,
    CollaborationMode? mode = CollaborationMode.RuntimeExperimental ) => new()
{
    Kind = RuntimeAuthorityContract.OperationKind,
    Id = id,
    Peer = peer,
    Scene = scene,
    ProtocolVersion = version,
    Mode = mode,
    RuntimeRunId = runId,
    RuntimeSequence = sequence,
    RuntimeTick = tick,
    RuntimeOperationKind = RuntimeAuthorityContract.SnapshotKind,
    Objects = objects.ToList()
};
Packet RuntimeEditPacket(
    string id,
    string peer,
    long sequence,
    long tick,
    IEnumerable<Change> changes,
    string runId = "run-1",
    string scene = "scenes/runtime.scene",
    int? version = Protocol.CurrentVersion,
    CollaborationMode? mode = CollaborationMode.RuntimeExperimental ) => new()
{
    Kind = RuntimeAuthorityContract.OperationKind,
    Id = id,
    Peer = peer,
    Scene = scene,
    ProtocolVersion = version,
    Mode = mode,
    RuntimeRunId = runId,
    RuntimeSequence = sequence,
    RuntimeTick = tick,
    RuntimeOperationKind = RuntimeAuthorityContract.EditKind,
    Changes = changes.ToList()
};
ObjectState RuntimeValues(
    ObjectState obj,
    string name = null,
    string position = null,
    string rotation = null,
    string scale = null,
    bool? enabled = null,
    string tags = null,
    string tint = null )
{
    var data = (JsonObject)obj.Data.DeepClone();
    if ( name is not null ) data["Name"] = name;
    if ( position is not null ) data["Position"] = position;
    if ( rotation is not null ) data["Rotation"] = rotation;
    if ( scale is not null ) data["Scale"] = scale;
    if ( enabled.HasValue ) data["Enabled"] = enabled.Value;
    if ( tags is not null ) data["Tags"] = tags;
    if ( tint is not null && data["Components"] is JsonArray components && components.Count > 0 )
        components[0]!["Tint"] = tint;
    return obj with { Data = data };
}
JsonValue JsonValueFromJson(string json)
{
    using var document = JsonDocument.Parse(json);
    var options = new JsonSerializerOptions
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    var typeInfo = JsonMetadataServices.CreateValueInfo<JsonElement>(
        options,
        new JsonElementPassThroughConverter());
    return JsonValue.Create(document.RootElement.Clone(), typeInfo);
}
JsonObject RuntimeComponentWithId(string type, string id, params (string Name, JsonNode Value)[] fields)
{
    var component = new JsonObject
    {
        ["__type"] = type,
        ["__guid"] = id
    };
    foreach (var field in fields) component[field.Name] = field.Value;
    return component;
}
void AssertRuntimeAccepted(Action validation, string name)
{
    validation();
    Assert(true, name);
}
void AssertRuntimeRejected(Action validation, string prefix, string name)
{
    try { validation(); }
    catch (InvalidOperationException error)
    {
        Assert(error.Message.StartsWith(prefix, StringComparison.Ordinal), name);
        return;
    }
    Assert(false, name);
}
bool HasMissingNamespaceError(Action query)
{
    try { query(); }
    catch (InvalidOperationException error)
    {
        return error.Message.StartsWith("Runtime authority namespace does not exist", StringComparison.Ordinal);
    }
    return false;
}

static int GetFreeLoopbackPort()
{
    using var reservation = new TcpListener( IPAddress.Loopback, 0 );
    reservation.Start();
    return ( (IPEndPoint)reservation.LocalEndpoint ).Port;
}

static string NewServerJournalPath()
{
    var root = Environment.GetEnvironmentVariable( "TMPDIR" );
    if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
    var directory = Path.Combine( root, "teamcreate-coretests", Guid.NewGuid().ToString( "N" ) );
    return Path.Combine( directory, "journal.jsonl" );
}

static (SessionServer Server, int Port) StartServer( string journalPath, Func<Packet, Task<SteamTokenVerdict>> asyncVerifier = null )
{
    Exception lastError = null;
    var directory = Path.GetDirectoryName( journalPath )!;
    for ( var attempt = 0; attempt < 10; attempt++ )
    {
        var port = GetFreeLoopbackPort();
        var attemptPath = attempt == 0 ? journalPath : Path.Combine( directory, $"journal-{attempt}.jsonl" );
        try { return ( new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", attemptPath, null, asyncVerifier ), port ); }
        catch ( SocketException error ) { lastError = error; }
    }
    throw new IOException( "Unable to reserve a disposable SessionServer port", lastError );
}

// A peer refused at the handshake must not be able to hold a connection slot forever.
// The server used to send "fatal" and leave the socket open, so 16 such peers filled the
// roster: any traffic refreshed LastSeen, the 15s timeout never reclaimed them, and every
// legitimate peer was then dropped with a bare disconnect.
async Task RefusedHandshakeDoesNotHoldSlotAsync()
{
    var journalPath = NewServerJournalPath();
    var directory = Path.GetDirectoryName( journalPath )!;
    SessionServer server = null;
    var hostile = new List<Wire>();
    Wire legit = null;
    try
    {
        (server, var port) = StartServer( journalPath );
        server.AllowUnverifiedPeers = true;

        // 20 peers, each refused: they claim a different project than the host's.
        for ( var i = 0; i < 20; i++ )
        {
            var c = await Wire.Connect( "127.0.0.1", port );
            c.Send( new Packet
            {
                Kind = "hello",
                Peer = Guid.NewGuid().ToString(),
                ProtocolVersion = Protocol.CurrentVersion,
                Mode = CollaborationMode.Source,
                Name = "hostile",
                Token = "test-token",
                Project = "a-different-project",
                Baseline = "test-baseline",
                SteamId = "76561198000000001",
                SteamToken = "test-steam-token"
            } );
            hostile.Add( c );
        }

        var deadline = DateTime.UtcNow.AddSeconds( 5 );
        while ( DateTime.UtcNow < deadline )
        {
            server.Pump();
            if ( !hostile.Any( w => w.Incoming.TryDequeue( out _ ) ) ) break;
            await Task.Delay( 10 );
        }

        // A legitimate peer must still be admitted, proving the slots were reclaimed.
        legit = await Wire.Connect( "127.0.0.1", port );
        legit.Send( ValidServerHello() );
        Packet reply = null;
        deadline = DateTime.UtcNow.AddSeconds( 5 );
        while ( DateTime.UtcNow < deadline )
        {
            server.Pump();
            if ( legit.Incoming.TryDequeue( out reply ) ) break;
            await Task.Delay( 10 );
        }
        Assert( reply?.Kind == "welcome",
            "a legitimate peer still connects after 20 peers were refused at the handshake" );
    }
    finally
    {
        legit?.Dispose();
        foreach ( var w in hostile ) w.Dispose();
        server?.Dispose();
        try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); }
        catch ( IOException ) { }
    }
}

static async Task<Packet> ExchangeHelloAsync( Packet hello )
{
    var journalPath = NewServerJournalPath();
    var directory = Path.GetDirectoryName( journalPath )!;
    SessionServer server = null;
    Wire client = null;
    try
    {
        (server, var port) = StartServer( journalPath );
        // These tests exercise the PROTOCOL, not the identity policy: there is no verifier
        // here, so a verified-only host would refuse every hello for the wrong reason.
        server.AllowUnverifiedPeers = true;
        Console.WriteLine( $"INFO: SessionServer handshake test used loopback port {port}" );
        client = await Wire.Connect( "127.0.0.1", port );
        client.Send( hello );
        var deadline = DateTime.UtcNow.AddSeconds( 5 );
        while ( DateTime.UtcNow < deadline )
        {
            server.Pump();
            if ( client.Incoming.TryDequeue( out var response ) ) return response;
            await Task.Delay( 10 );
        }
        throw new TimeoutException( "SessionServer did not respond to hello" );
    }
    finally
    {
        client?.Dispose();
        server?.Dispose();
        try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); }
        catch ( IOException ) { }
    }
}

// Every v4 hello fixture carries the three protocol-4 fields. They are part of the
// handshake contract now, so a fixture without them would be exercising the "older
// collaboration library" refusal instead of the check it is named after.
static Packet SteamHello( string steamId = "76561198000000001", string steamToken = "a-realistic-session-token" ) => new()
{
    Kind = "hello",
    Peer = Guid.NewGuid().ToString(),
    Name = "Tester",
    Token = "test-token",
    Project = "test-project",
    Baseline = "test-baseline",
    ProtocolVersion = Protocol.CurrentVersion,
    Mode = CollaborationMode.Source,
    SteamId = steamId,
    SteamToken = steamToken,
    OrgIdent = "cyn",
    LibraryVersion = "1.1.1",
    ProjectManifest = "a1b2c3d4"
};

static Packet ValidServerHello() => new()
{
    Kind = "hello",
    Peer = Guid.NewGuid().ToString(),
    Name = "CoreTests",
    Token = "test-token",
    Project = "test-project",
    Baseline = "test-baseline",
    ProtocolVersion = Protocol.CurrentVersion,
    Mode = CollaborationMode.Source,
    SteamId = "76561198000000001",
    SteamToken = "test-steam-token",
    OrgIdent = "cyn",
    LibraryVersion = "1.1.1",
    ProjectManifest = "a1b2c3d4"
};

// Task 2 protocol contract: tests exercise the pure wire envelope before any server is involved.
var hello = new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source };
var helloJson = JsonSerializer.SerializeToUtf8Bytes( hello );
var helloCopy = JsonSerializer.Deserialize<Packet>( helloJson )!;
Assert( helloCopy.ProtocolVersion == Protocol.CurrentVersion, "protocol version survives wire round trip" );
Assert( helloCopy.Mode == CollaborationMode.Source, "collaboration mode survives wire round trip" );
var runtimeEnvelope = new Packet
{
    Kind = "runtime-sample",
    ProtocolVersion = Protocol.CurrentVersion,
    Mode = CollaborationMode.RuntimeExperimental,
    RuntimeRunId = "run-1",
    RuntimeTick = 42,
    RuntimeSequence = 7,
    RuntimeOperationKind = "sample",
    Scene = "scenes/test.scene"
};
var runtimeJson = JsonSerializer.SerializeToUtf8Bytes( runtimeEnvelope );
var runtimeCopy = JsonSerializer.Deserialize<Packet>( runtimeJson )!;
Assert( runtimeCopy.RuntimeRunId == "run-1" && runtimeCopy.RuntimeTick == 42 && runtimeCopy.RuntimeSequence == 7, "runtime envelope fields survive wire round trip" );
Assert( runtimeCopy.RuntimeOperationKind == "sample", "runtime operation kind survives wire round trip" );
Assert( Protocol.ValidateRuntimeEnvelope( runtimeCopy, "run-1" ), "valid current runtime envelope accepted" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", ProtocolVersion = 1, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = "run-1", RuntimeSequence = 1, RuntimeOperationKind = "sample" }, "run-1" ), "old runtime protocol rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = "run-1", RuntimeSequence = 1, RuntimeOperationKind = "sample" }, "run-1" ), "missing runtime protocol version rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", ProtocolVersion = Protocol.CurrentVersion, RuntimeRunId = "run-1", RuntimeSequence = 1, RuntimeOperationKind = "sample" }, "run-1" ), "missing runtime mode rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source, RuntimeSequence = 1, RuntimeOperationKind = "sample" }, "run-1" ), "wrong runtime mode rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = " ", RuntimeSequence = 1, RuntimeOperationKind = "sample" }, "run-1" ), "missing runtime run id rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = "other-run", RuntimeSequence = 1, RuntimeOperationKind = "sample" }, "run-1" ), "runtime run mismatch rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "runtime-sample", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = "run-1", RuntimeSequence = 0, RuntimeOperationKind = "sample" }, "run-1" ), "non-positive runtime sequence rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( runtimeCopy, "run-1", "scenes/other.scene" ), "runtime scene mismatch rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( runtimeCopy, "run-1", null, 7 ), "stale runtime sequence rejected" );
Assert( !Protocol.ValidateRuntimeEnvelope( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source }, "run-1" ), "source packet rejected by runtime validator" );
Assert( Protocol.ValidateSourceHandshake( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source, SteamId = "76561198000000001", SteamToken = "test-steam-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4" } ), "current source handshake accepted" );
Assert( Protocol.ValidateHandshake( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source, SteamId = "76561198000000001", SteamToken = "test-steam-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4" } ), "current handshake accepted" );
Assert( !Protocol.ValidateHandshake( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental } ), "runtime handshake requires run id" );
Assert( !Protocol.ValidateSourceHandshake( new Packet { Kind = "hello", ProtocolVersion = 1, Mode = CollaborationMode.Source } ), "source handshake rejects old protocol" );
Assert( !Protocol.ValidateSourceHandshake( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion } ), "source handshake rejects missing mode" );
Assert( Protocol.IsValidMode( CollaborationMode.Source ) && Protocol.IsValidMode( CollaborationMode.RuntimeExperimental ) && !Protocol.IsValidMode( (CollaborationMode)99 ), "mode validity is explicit" );
// Fixture note: the protocol version-name literal follows the current version bump. The
// assertion's subject is unchanged — the state-serialization baseline name stays
// "teamcreate-1" and remains distinct from the wire protocol name.
Assert( StateRules.Version == "teamcreate-1" && Protocol.VersionName == "teamcreate-4" && Protocol.VersionName != StateRules.Version, "protocol and source baseline names remain distinct" );
Assert( Protocol.GetHandshakeError( new Packet { Kind = "hello", Mode = CollaborationMode.Source } ).Contains( "protocol version" ), "shared handshake reports missing protocol version" );
Assert( Protocol.GetHandshakeError( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion } ).Contains( "mode" ), "shared handshake reports missing mode" );
Assert( RuntimeStateRules.IsValidRunId( "coexist-run" ) && !RuntimeStateRules.IsValidRunId( " run-1" ) &&
    !RuntimeStateRules.IsValidRunId( "run-1 " ) && !RuntimeStateRules.IsValidRunId( "run\n1" ) &&
    !RuntimeStateRules.IsValidRunId( new string( 'r', RuntimeStateRules.MaxRuntimeRunIdLength + 1 ) ) &&
    !Protocol.ValidateRuntimeEnvelope( runtimeCopy, " run-1" ) &&
    !Protocol.ValidateHandshake( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = " run-1" } ),
    "shared runtime run-ID validation rejects padded, control-character, and oversized IDs at lifecycle/protocol boundaries" );
Assert( Protocol.ValidateHandshake( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = "coexist-run", SteamId = "76561198000000001", SteamToken = "test-steam-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4" } ),
    "short opaque non-GUID runtime run IDs remain valid at the handshake boundary" );
Assert( Protocol.GetHandshakeError( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = (CollaborationMode)99 } ).Contains( "mode" ), "shared handshake reports invalid mode" );
Assert( Protocol.GetHandshakeError( new Packet { Kind = "hello", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.RuntimeExperimental, RuntimeRunId = " " } ).Contains( "runtime run ID" ), "shared handshake reports blank runtime run ID" );
Assert( Protocol.GetHandshakeError( new Packet { Kind = "subscribe" } ) == "Invalid collaboration handshake", "shared handshake rejects non-hello packet" );
Assert( Protocol.GetHandshakeError( new Packet { Kind = "hello", Peer = "not-a-guid", ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source }, requireValidPeer: true ) == "Invalid collaboration handshake", "shared handshake rejects invalid peer when required" );

// Task 2 (handshake v4): the organization, library, and project-revision fields are part
// of the contract. A peer that omits one is running an older library, so each absence
// names that remedy rather than a credential problem.
static Packet V4Hello( string orgIdent = "cyn", string libraryVersion = "1.1.1", string projectManifest = "a1b2c3d4", int? protocolVersion = null ) => new()
{
    Kind = "hello",
    ProtocolVersion = protocolVersion ?? Protocol.CurrentVersion,
    Mode = CollaborationMode.Source,
    SteamId = "76561198000000001",
    SteamToken = "test-steam-token",
    OrgIdent = orgIdent,
    LibraryVersion = libraryVersion,
    ProjectManifest = projectManifest
};
Assert( Protocol.GetHandshakeError( V4Hello() ) is null, "a v4 hello carrying every field is accepted" );
Assert( Protocol.GetHandshakeError( V4Hello( orgIdent: null ) ) ==
        "This peer runs an older collaboration library. Update the collaboration library on both machines and reconnect.",
    "a hello with no organization ident reports an older collaboration library" );
Assert( Protocol.GetHandshakeError( V4Hello( orgIdent: "   " ) ) == Protocol.OlderLibraryMessage,
    "a hello with a blank organization ident reports the same older-library refusal" );
Assert( Protocol.GetHandshakeError( V4Hello( libraryVersion: null ) ) == Protocol.OlderLibraryMessage,
    "a hello with no library version reports the older-library refusal" );
Assert( Protocol.GetHandshakeError( V4Hello( libraryVersion: "not-a-version" ) ) == Protocol.OlderLibraryMessage,
    "a hello with a malformed library version reports the older-library refusal" );
Assert( Protocol.GetHandshakeError( V4Hello( projectManifest: null ) ) ==
        "This peer did not report its project revision. Update the collaboration library on both machines and reconnect.",
    "a hello with no project revision is refused with its own message" );
Assert( Protocol.GetHandshakeError( V4Hello( projectManifest: "   " ) ) == Protocol.MissingProjectRevisionMessage,
    "a blank project revision is refused as missing rather than as a mismatch" );
Assert( Protocol.GetHandshakeError( V4Hello( protocolVersion: 3 ) )
        is { } olderVersion && olderVersion.StartsWith( "Unsupported collaboration protocol version", StringComparison.Ordinal ),
    "a v3 hello still reports the unchanged unsupported-protocol-version message" );
Assert( System.Version.TryParse( LibraryInfo.LibraryVersion, out _ ),
    "the shipped library version parses as a version" );

// Task 3 runtime schema contract. These tests remain pure and engine-independent.
var runtimeEmptyObject = RuntimeObj("empty");
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeEmptyObject), "runtime accepts empty GameObject with required fields");

var runtimeFullObject = RuntimeObj("full", "", RuntimeComponent("Sandbox.DirectionalLight", ("LightColor", "1,1,1,1"), ("SkyColor", "0,0,0,0"), ("Shadows", true)));
runtimeFullObject.Data["Position"] = "1,2,3";
runtimeFullObject.Data["Rotation"] = "0,0,0,1";
runtimeFullObject.Data["Scale"] = "1,1,1";
runtimeFullObject.Data["Enabled"] = true;
runtimeFullObject.Data["Tags"] = "runtime";
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeFullObject), "runtime accepts all six initial GameObject fields and all directional light fields");

var runtimeGameObjectReference = (JsonObject)runtimeFullObject.Data.DeepClone();
runtimeGameObjectReference["Position"] = new JsonObject { ["x"] = 1 };
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeFullObject with { Data = runtimeGameObjectReference }), "Runtime state is not allowlisted", "runtime rejects object value on GameObject field");

var runtimeEmptyObjectReference = (JsonObject)runtimeFullObject.Data.DeepClone();
runtimeEmptyObjectReference["Position"] = new JsonObject();
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeFullObject with { Data = runtimeEmptyObjectReference }), "Runtime state is not allowlisted", "runtime rejects empty object GameObject value");

var runtimeEmptyArrayReference = (JsonObject)runtimeFullObject.Data.DeepClone();
runtimeEmptyArrayReference["Position"] = new JsonArray();
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeFullObject with { Data = runtimeEmptyArrayReference }), "Runtime state is not allowlisted", "runtime rejects empty array GameObject value");

var runtimeObjectBackedGameObjectValue = (JsonObject)runtimeFullObject.Data.DeepClone();
runtimeObjectBackedGameObjectValue["Position"] = JsonValueFromJson("{\"x\":1}");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeFullObject with { Data = runtimeObjectBackedGameObjectValue }), "Runtime state is not allowlisted", "runtime rejects object-backed JsonValue on GameObject field");

var runtimeArrayBackedGameObjectValue = (JsonObject)runtimeFullObject.Data.DeepClone();
runtimeArrayBackedGameObjectValue["Position"] = JsonValueFromJson("[1,2,3]");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeFullObject with { Data = runtimeArrayBackedGameObjectValue }), "Runtime state is not allowlisted", "runtime rejects array-backed JsonValue on GameObject field");

var runtimeNullGameObjectValue = (JsonObject)runtimeFullObject.Data.DeepClone();
runtimeNullGameObjectValue["Position"] = JsonValueFromJson("null");
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeFullObject with { Data = runtimeNullGameObjectValue }), "runtime accepts null-backed JsonValue on GameObject field");

var runtimeMissingComponents = RuntimeObj("missing-components");
runtimeMissingComponents.Data.Remove("Components");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeMissingComponents), "Runtime state shape is invalid", "runtime rejects missing required Components field");

var runtimeInvalidParent = RuntimeObj("invalid-parent") with { Parent = "not-a-guid" };
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeInvalidParent), "Runtime object identity is invalid", "runtime rejects malformed parent guid");

var runtimeInvalidEnabled = RuntimeObj("invalid-enabled");
runtimeInvalidEnabled.Data["Enabled"] = "yes";
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeInvalidEnabled), "Runtime state shape is invalid", "runtime rejects non-boolean Enabled field");

var runtimeInvalidTags = RuntimeObj("invalid-tags");
runtimeInvalidTags.Data["Tags"] = 1;
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeInvalidTags), "Runtime state shape is invalid", "runtime rejects non-string Tags field");

var runtimeModel = RuntimeObj("model", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", "1,0,0,1")));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeModel), "runtime accepts allowlisted ModelRenderer Tint");

var runtimeCamera = RuntimeObj("camera", "", RuntimeComponent("Sandbox.CameraComponent",
    ("FieldOfView", 70), ("Orthographic", false), ("OrthographicHeight", 10),
    ("ZNear", 0.1), ("ZFar", 1000), ("BackgroundColor", "0,0,0,1")));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeCamera), "runtime accepts all allowlisted CameraComponent properties");

var runtimePointLight = RuntimeObj("point-light", "", RuntimeComponent("Sandbox.PointLight",
    ("LightColor", "1,1,1,1"), ("Radius", 12.5), ("Shadows", true)));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimePointLight), "runtime accepts all allowlisted PointLight properties");

var runtimeBoxCollider = RuntimeObj("box-collider", "", RuntimeComponent("Sandbox.BoxCollider",
    ("Center", "0,0,0"), ("Scale", "1,1,1"), ("IsTrigger", false)));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeBoxCollider), "runtime accepts all allowlisted BoxCollider properties");

var runtimeCustom = RuntimeObj("custom", "", RuntimeComponent("Game.ScriptComponent", ("Value", 1)));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeCustom), "Runtime component type is not allowlisted", "runtime rejects custom component type");

var runtimeObjectReference = RuntimeObj("reference", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", new JsonObject { ["x"] = 1 })));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeObjectReference), "Runtime state is not allowlisted", "runtime rejects nonempty object inspector value");

var runtimeArrayReference = RuntimeObj("array", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", new JsonArray(1))));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeArrayReference), "Runtime state is not allowlisted", "runtime rejects nonempty array inspector value");

var runtimeEmptyObjectInspector = RuntimeObj("empty-object", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", new JsonObject())));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeEmptyObjectInspector), "Runtime state is not allowlisted", "runtime rejects empty object inspector value");

var runtimeEmptyArrayInspector = RuntimeObj("empty-array", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", new JsonArray())));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeEmptyArrayInspector), "Runtime state is not allowlisted", "runtime rejects empty array inspector value");

var runtimeObjectBackedInspectorValue = RuntimeObj("object-backed-inspector", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", JsonValueFromJson("{\"x\":1}"))));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeObjectBackedInspectorValue), "Runtime state is not allowlisted", "runtime rejects object-backed JsonValue inspector value");

var runtimeArrayBackedInspectorValue = RuntimeObj("array-backed-inspector", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", JsonValueFromJson("[1,2,3]"))));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeArrayBackedInspectorValue), "Runtime state is not allowlisted", "runtime rejects array-backed JsonValue inspector value");

var runtimeNullInspectorValue = RuntimeObj("null-inspector", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", JsonValueFromJson("null"))));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(runtimeNullInspectorValue), "runtime accepts null-backed JsonValue inspector value");

var runtimeRenderOptions = RuntimeObj("render-options", "", RuntimeComponent("Sandbox.ModelRenderer", ("RenderOptions", new JsonObject { ["GameLayer"] = true, ["OverlayLayer"] = false, ["BloomLayer"] = true, ["AfterUILayer"] = false })));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeRenderOptions), "Runtime state is not allowlisted", "runtime rejects source-only RenderOptions object");

var runtimePrefab = RuntimeObj("prefab", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", "1,0,0,1")));
runtimePrefab.Data["Prefab"] = "scenes/source.prefab";
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimePrefab), "Runtime state is not allowlisted", "runtime rejects prefab marker");

var runtimeUnknownObjectField = RuntimeObj("networked");
runtimeUnknownObjectField.Data["NetworkMode"] = 2;
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeUnknownObjectField), "Runtime state is not allowlisted", "runtime rejects unknown GameObject field");

var runtimeUnknownComponentField = RuntimeObj("unknown", "", RuntimeComponent("Sandbox.CameraComponent", ("Clip", "near")));
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeUnknownComponentField), "Runtime state is not allowlisted", "runtime rejects unknown component field");

var runtimeMismatchedGuid = RuntimeObj("mismatch");
runtimeMismatchedGuid.Data["__guid"] = Guid.NewGuid().ToString();
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeMismatchedGuid), "Runtime object identity is invalid", "runtime rejects mismatched object guid");

var runtimeInvalidObjectGuid = RuntimeObj("invalid-object") with { Id = "not-a-guid" };
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeInvalidObjectGuid), "Runtime object identity is invalid", "runtime rejects malformed object guid");

var runtimeInvalidComponentGuid = RuntimeObj("component", "", new JsonObject { ["__type"] = "Sandbox.ModelRenderer", ["__guid"] = "not-a-guid" });
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeInvalidComponentGuid), "Runtime object identity is invalid", "runtime rejects malformed component guid");

var runtimeComponentUsesObjectId = RuntimeObj("component-object-id", "", new JsonObject { ["__type"] = "Sandbox.ModelRenderer", ["__guid"] = "" });
runtimeComponentUsesObjectId.Data["Components"]![0]!["__guid"] = runtimeComponentUsesObjectId.Id;
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeComponentUsesObjectId), "Runtime object identity is invalid", "runtime rejects component id reused by its object");

var alternateComponentRepresentation = RuntimeObj("alternate-component-representation", "",
    RuntimeComponentWithId("Sandbox.ModelRenderer", Guid.NewGuid().ToString("D")));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(alternateComponentRepresentation), "runtime accepts component ids in canonical format");

var alternateComponentSerializedId = alternateComponentRepresentation.Data["Components"]![0]!["__guid"]!.GetValue<string>();
alternateComponentRepresentation.Data["Components"]![0]!["__guid"] = Guid.Parse(alternateComponentSerializedId).ToString("N");
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(alternateComponentRepresentation), "runtime accepts equivalent alternate-format component ids");

var runtimeDuplicateComponent = RuntimeObj("duplicate", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", "1,0,0,1")));
var runtimeDuplicateComponentId = runtimeDuplicateComponent.Data["Components"]![0]!["__guid"]!.GetValue<string>();
runtimeDuplicateComponent.Data["Components"]!.AsArray().Add(new JsonObject
{
    ["__type"] = "Sandbox.ModelRenderer",
    ["__guid"] = Guid.Parse(runtimeDuplicateComponentId).ToString("N")
});
AssertRuntimeRejected(() => RuntimeStateRules.ValidateObject(runtimeDuplicateComponent), "Runtime object identity is invalid", "runtime rejects duplicate component ids within one object");

var runtimeParent = RuntimeObj("parent");
var runtimeChild = RuntimeObj("child", runtimeParent.Id);
var runtimeSnapshot = new Dictionary<string, ObjectState>
{
    [runtimeParent.Id] = runtimeParent,
    [runtimeChild.Id] = runtimeChild
};
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeSnapshot), "runtime accepts valid complete snapshot graph");

var runtimeDuplicateKey = new Dictionary<string, ObjectState> { [runtimeParent.Id] = runtimeChild };
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeDuplicateKey), "Runtime object identity is invalid", "runtime rejects mismatched duplicate dictionary key");

var alternateObjectGuid = Guid.NewGuid();
var runtimeDuplicateObjectD = RuntimeObjWithId("duplicate-object-d", alternateObjectGuid.ToString("D"));
var runtimeDuplicateObjectN = RuntimeObjWithId("duplicate-object-n", alternateObjectGuid.ToString("N"));
var runtimeDuplicateObjectSnapshot = new Dictionary<string, ObjectState>
{
    [runtimeDuplicateObjectD.Id] = runtimeDuplicateObjectD,
    [runtimeDuplicateObjectN.Id] = runtimeDuplicateObjectN
};
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeDuplicateObjectSnapshot), "Runtime object identity is invalid", "runtime rejects equivalent alternate-format object ids");

var alternateObjectRepresentation = RuntimeObjWithId("alternate-object-representation", Guid.NewGuid().ToString("D"));
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(alternateObjectRepresentation), "runtime accepts object ids in canonical format");
alternateObjectRepresentation.Data["__guid"] = Guid.Parse(alternateObjectRepresentation.Id).ToString("N");
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateObject(alternateObjectRepresentation), "runtime accepts equivalent alternate-format object and serialized ids");

var alternateComponentGuid = Guid.NewGuid();
var runtimeDuplicateComponentOwnerD = RuntimeObj("component-owner-d", "", RuntimeComponentWithId("Sandbox.ModelRenderer", alternateComponentGuid.ToString("D")));
var runtimeDuplicateComponentOwnerN = RuntimeObj("component-owner-n", "", RuntimeComponentWithId("Sandbox.ModelRenderer", alternateComponentGuid.ToString("N")));
var runtimeDuplicateComponentSnapshot = new Dictionary<string, ObjectState>
{
    [runtimeDuplicateComponentOwnerD.Id] = runtimeDuplicateComponentOwnerD,
    [runtimeDuplicateComponentOwnerN.Id] = runtimeDuplicateComponentOwnerN
};
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeDuplicateComponentSnapshot), "Runtime object identity is invalid", "runtime rejects equivalent alternate-format component ids across objects");

var alternateParentGuid = Guid.NewGuid();
var runtimeAlternateParent = RuntimeObjWithId("alternate-parent", alternateParentGuid.ToString("D"));
var runtimeAlternateChild = RuntimeObjWithId("alternate-child", Guid.NewGuid().ToString("D"), alternateParentGuid.ToString("N"));
var runtimeAlternateParentSnapshot = new Dictionary<string, ObjectState>
{
    [alternateParentGuid.ToString("N")] = runtimeAlternateParent,
    [runtimeAlternateChild.Id] = runtimeAlternateChild
};
AssertRuntimeAccepted(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeAlternateParentSnapshot), "runtime accepts a parent reference with equivalent alternate-format snapshot key and id");

var runtimeSharedComponent = RuntimeObj("component-owner", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", "1,0,0,1")));
var runtimeSharedComponentSnapshot = new Dictionary<string, ObjectState>
{
    [runtimeParent.Id] = runtimeParent,
    [runtimeSharedComponent.Id] = runtimeSharedComponent
};
var sharedComponentId = runtimeSharedComponent.Data["Components"]![0]!["__guid"]!.GetValue<string>();
runtimeParent.Data["Components"]!.AsArray().Add(new JsonObject { ["__type"] = "Sandbox.ModelRenderer", ["__guid"] = sharedComponentId });
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeSharedComponentSnapshot), "Runtime object identity is invalid", "runtime rejects component id reused by another object");

var runtimeOrphan = new Dictionary<string, ObjectState> { [runtimeChild.Id] = runtimeChild };
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeOrphan), "Runtime parent does not exist", "runtime rejects missing parent");

var runtimeCycle = new Dictionary<string, ObjectState> { [runtimeParent.Id] = runtimeParent, [runtimeChild.Id] = runtimeChild };
var cycleParentId = runtimeParent.Id;
var cycleChildId = runtimeChild.Id;
runtimeCycle[cycleParentId] = runtimeCycle[cycleParentId] with { Parent = cycleChildId };
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.scene", runtimeCycle), "Runtime parent cycle detected", "runtime rejects parent cycle");

Assert(RuntimeStateRules.IsSafeScenePath("scenes/runtime.scene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes/../secrets.scene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes/./runtime.scene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes/./../runtime.scene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes/runtime\u0001.scene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes/runtime.\u007fscene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes\\runtime.scene") &&
    !RuntimeStateRules.IsSafeScenePath("") &&
    !RuntimeStateRules.IsSafeScenePath("scenes//runtime.scene") &&
    !RuntimeStateRules.IsSafeScenePath("scenes/runtime.json"),
    "runtime scene path helper is bounded and rejects separators, empty/double-slash, dot/control segments, and invalid extensions");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/../secrets.scene", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects invalid scene path");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/./runtime.scene", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects dot segment scene path");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime\u0001.scene", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects control-character scene path");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("assets/runtime.scene", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects scene path outside scenes root");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes\\runtime.scene", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects backslash scene path");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects empty scene path");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes//runtime.scene", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects double-slash scene path");
AssertRuntimeRejected(() => RuntimeStateRules.ValidateSnapshot("scenes/runtime.json", runtimeSnapshot), "Runtime scene path is invalid", "runtime rejects invalid scene extension");

// Task 4 pure runtime authority contract. The tests invoke one authority synchronously;
// no socket, editor API, or concurrent caller is involved.
var runtimeJournal = new List<Packet>();
var runtimeAuthority = new RuntimeAuthority(packet => runtimeJournal.Add(packet));
var runtimeA = RuntimeObj("runtime-a", "", RuntimeComponent("Sandbox.ModelRenderer", ("Tint", "1,0,0,1")));
var runtimeB = RuntimeObj("runtime-b", "", RuntimeComponent("Sandbox.CameraComponent", ("FieldOfView", 60)));
var runtimeSeed = RuntimeSnapshotPacket("seed", Guid.NewGuid().ToString(), new[] { runtimeA, runtimeB }, 1, 10);
var runtimeSeedReceipt = runtimeAuthority.Apply(runtimeSeed);
Assert(runtimeSeedReceipt.Kind == "accepted" && runtimeSeedReceipt.Revision == 1, "runtime authority accepts the initial complete snapshot in sequence");
Assert(runtimeJournal.Count == 1 && runtimeJournal[0].RuntimeSequence == 1, "runtime authority journals an accepted snapshot before acknowledgement");

// Regression: validation catches a null edit entry, but receipt construction must also
// fail closed when it copies the malformed operation. No exception may escape Apply.
var malformedChangeAuthority = new RuntimeAuthority( _ => { } );
var malformedChangeSeed = RuntimeSnapshotPacket( "malformed-change-seed", Guid.NewGuid().ToString(),
    new[] { runtimeA }, 1, 1, runId: "run-malformed-change" );
Assert( malformedChangeAuthority.Apply( malformedChangeSeed ).Kind == "accepted",
    "malformed change regression fixture seeds a separate authority" );
var malformedChangeReceipt = malformedChangeAuthority.Apply( RuntimeEditPacket(
    "malformed-null-change", Guid.NewGuid().ToString(), 2, 2,
    new Change[] { null }, runId: "run-malformed-change" ) );
Assert( malformedChangeReceipt is not null && malformedChangeReceipt.Kind == "reject" &&
    !string.IsNullOrWhiteSpace( malformedChangeReceipt.Error ),
    "malformed null change returns an actionable reject instead of escaping" );

// Regression: invalid object payloads can fail validation and still contain null data or
// object entries. Reject receipt construction must not dereference those malformed values.
var malformedSnapshotAuthority = new RuntimeAuthority( _ => { } );
var malformedSnapshotDataId = Guid.NewGuid();
var malformedSnapshotReceipt = malformedSnapshotAuthority.Apply( RuntimeSnapshotPacket(
    "malformed-null-data", Guid.NewGuid().ToString(),
    new[] { new ObjectState( malformedSnapshotDataId.ToString( "D" ), "", null ) },
    1, 1, runId: "run-malformed-snapshot" ) );
Assert( malformedSnapshotReceipt is not null && malformedSnapshotReceipt.Kind == "reject" &&
    malformedSnapshotReceipt.Error.Contains( "Runtime object identity is invalid", StringComparison.Ordinal ) &&
    malformedSnapshotReceipt.Error.Contains( "Payload could not be copied", StringComparison.Ordinal ),
    "malformed snapshot data returns an actionable reject instead of escaping" );
var malformedSnapshotNullObjectReceipt = malformedSnapshotAuthority.Apply( RuntimeSnapshotPacket(
    "malformed-null-object", Guid.NewGuid().ToString(), new ObjectState[] { null },
    2, 2, runId: "run-malformed-snapshot" ) );
Assert( malformedSnapshotNullObjectReceipt is not null && malformedSnapshotNullObjectReceipt.Kind == "reject" &&
    malformedSnapshotNullObjectReceipt.Error.Contains( "Runtime object identity is invalid", StringComparison.Ordinal ) &&
    malformedSnapshotNullObjectReceipt.Error.Contains( "Payload could not be copied", StringComparison.Ordinal ),
    "malformed null snapshot object returns an actionable reject instead of escaping" );

// Regression: a component GUID is part of normalized runtime identity. A Before state
// using N-form component identity must compare equal to the authority's D-form state.
var componentGuidAuthority = new RuntimeAuthority( _ => { } );
var componentGuidObjectId = Guid.NewGuid();
var componentGuidComponentId = Guid.NewGuid();
var componentGuidObject = RuntimeObjWithId( "component-guid", componentGuidObjectId.ToString( "D" ),
    components: RuntimeComponentWithId( "Sandbox.ModelRenderer", componentGuidComponentId.ToString( "D" ), ( "Tint", "1,0,0,1" ) ) );
Assert( componentGuidAuthority.Apply( RuntimeSnapshotPacket( "component-guid-seed", Guid.NewGuid().ToString(),
    new[] { componentGuidObject }, 1, 1, runId: "run-component-guid" ) ).Kind == "accepted",
    "component GUID regression fixture snapshot is accepted" );
var componentGuidBeforeData = (JsonObject)componentGuidObject.Data.DeepClone();
((JsonArray)componentGuidBeforeData["Components"]!)[0]!.AsObject()["__guid"] = componentGuidComponentId.ToString( "N" );
var componentGuidBefore = new ObjectState( componentGuidObjectId.ToString( "N" ), "", componentGuidBeforeData );
var componentGuidAfter = RuntimeValues( componentGuidObject, name: "component-guid-accepted" );
var componentGuidReceipt = componentGuidAuthority.Apply( RuntimeEditPacket(
    "component-guid-edit", Guid.NewGuid().ToString(), 2, 2,
    new[] { new Change( componentGuidObjectId.ToString( "N" ), componentGuidBefore, componentGuidAfter ) },
    runId: "run-component-guid" ) );
Assert( componentGuidReceipt.Kind == "accepted" &&
    componentGuidAuthority.State( "run-component-guid", "scenes/runtime.scene" )[componentGuidObjectId.ToString( "D" )].Data["Name"]!.GetValue<string>() == "component-guid-accepted",
    "runtime authority accepts equivalent alternate component GUID representations in compare-and-swap before state" );
Assert( ((JsonArray)componentGuidAuthority.State( "run-component-guid", "scenes/runtime.scene" )[componentGuidObjectId.ToString( "D" )].Data["Components"]!)[0]!.AsObject()["__guid"]!.GetValue<string>() == componentGuidComponentId.ToString( "D" ),
    "runtime authority canonicalizes component GUIDs in normalized state" );

// Regression: the journal gets an isolated receipt, and mutating that packet cannot alter
// either the caller's accepted receipt or the retained duplicate receipt.
var callbackIsolationAuthority = new RuntimeAuthority( packet =>
{
    packet.Kind = "callback-mutated";
    packet.Revision = 999;
    packet.Error = "callback-mutated";
    packet.Objects![0].Data["Name"] = "callback-mutated";
} );
var callbackIsolationObject = RuntimeObj( "callback-original" );
var callbackIsolationSeed = RuntimeSnapshotPacket( "callback-isolation-seed", Guid.NewGuid().ToString(),
    new[] { callbackIsolationObject }, 1, 1, runId: "run-callback-isolation" );
var callbackIsolationReceipt = callbackIsolationAuthority.Apply( callbackIsolationSeed );
Assert( callbackIsolationReceipt.Kind == "accepted" && callbackIsolationReceipt.Revision == 1 &&
    callbackIsolationReceipt.Error is null && callbackIsolationReceipt.Objects![0].Data["Name"]!.GetValue<string>() == "callback-original" &&
    callbackIsolationAuthority.State( "run-callback-isolation", "scenes/runtime.scene" )[callbackIsolationObject.Id].Data["Name"]!.GetValue<string>() == "callback-original",
    "journal callback packet is isolated from the returned accepted receipt" );
var callbackIsolationDuplicate = callbackIsolationAuthority.Apply( callbackIsolationSeed );
Assert( callbackIsolationDuplicate.Kind == "accepted" && callbackIsolationDuplicate.Revision == 1 &&
    callbackIsolationDuplicate.Error is null && callbackIsolationDuplicate.Objects![0].Data["Name"]!.GetValue<string>() == "callback-original",
    "journal callback mutation cannot alter the retained duplicate receipt" );

// Regression: the journal may retain and mutate the caller's original operation packet.
// Its pre-validation identity and cursor fields must not be read after that boundary, and
// a valid snapshot must still commit atomically from captured metadata.
var callerAliasObjectId = Guid.NewGuid();
var callerAliasObject = RuntimeObjWithId( "caller-alias-original", callerAliasObjectId.ToString( "D" ) );
var callerAliasSnapshotPeer = Guid.NewGuid().ToString();
Packet callerAliasSnapshot = null;
var callerAliasAuthority = new RuntimeAuthority( _ =>
{
    callerAliasSnapshot.RuntimeRunId = null;
    callerAliasSnapshot.Scene = "scenes/mutated.scene";
    callerAliasSnapshot.RuntimeSequence = null;
    callerAliasSnapshot.RuntimeTick = null;
    callerAliasSnapshot.Peer = null;
    callerAliasSnapshot.Id = null;
} );
var callerAliasReceipt = callerAliasAuthority.Apply( callerAliasSnapshot = RuntimeSnapshotPacket(
    "caller-alias-snapshot", callerAliasSnapshotPeer, new[] { callerAliasObject },
    1, 7, runId: "run-caller-alias" ) );
Assert(callerAliasReceipt.Kind == "accepted" && callerAliasReceipt.Revision == 1 &&
    callerAliasReceipt.Id == "caller-alias-snapshot" && callerAliasReceipt.Peer == callerAliasSnapshotPeer &&
    callerAliasReceipt.RuntimeRunId == "run-caller-alias" && callerAliasReceipt.Scene == "scenes/runtime.scene" &&
    callerAliasReceipt.RuntimeSequence == 1 && callerAliasReceipt.RuntimeTick == 7 &&
    callerAliasAuthority.Revision( "run-caller-alias", "scenes/runtime.scene" ) == 1 &&
    callerAliasAuthority.LastSequence( "run-caller-alias", "scenes/runtime.scene" ) == 1 &&
    callerAliasAuthority.LastTick( "run-caller-alias", "scenes/runtime.scene" ) == 7 &&
    callerAliasAuthority.ReceiptCount( "run-caller-alias", "scenes/runtime.scene" ) == 1 &&
    StateRules.Equal( callerAliasAuthority.State( "run-caller-alias", "scenes/runtime.scene" )[callerAliasObjectId.ToString( "D" )], callerAliasObject ),
    "journal mutation of the original operation cannot corrupt or partially commit the accepted snapshot" );

// Regression: after a failed journal callback, the reject must retain the captured
// pre-callback identity without exposing the caller's now-malformed packet, and no
// state, revision, cursor, receipt, or persisted-acknowledgement state may advance.
var callerAliasRejectObjectId = Guid.NewGuid();
var callerAliasRejectObject = RuntimeObjWithId( "caller-alias-reject", callerAliasRejectObjectId.ToString( "D" ) );
Packet callerAliasRejectEdit = null;
Packet callerAliasRejectJournalPacket = null;
var callerAliasRejectJournalInvocations = 0;
var callerAliasFailEditJournal = false;
var callerAliasRejectAuthority = new RuntimeAuthority( packet =>
{
    callerAliasRejectJournalInvocations++;
    callerAliasRejectJournalPacket = packet;
    if (!callerAliasFailEditJournal) return;
    callerAliasRejectEdit.RuntimeRunId = null;
    callerAliasRejectEdit.Scene = null;
    callerAliasRejectEdit.RuntimeSequence = null;
    callerAliasRejectEdit.RuntimeTick = null;
    callerAliasRejectEdit.Peer = null;
    callerAliasRejectEdit.Id = null;
    callerAliasRejectEdit.Changes = null;
    throw new IOException( "runtime journal rejected mutated caller packet" );
} );
Assert(callerAliasRejectAuthority.Apply( RuntimeSnapshotPacket(
    "caller-alias-reject-seed", Guid.NewGuid().ToString(), new[] { callerAliasRejectObject },
    1, 7, runId: "run-caller-alias-reject" ) ).Kind == "accepted",
    "journal-failure caller-alias regression fixture seeds a separate authority" );
var callerAliasRejectStateBefore = callerAliasRejectAuthority.State( "run-caller-alias-reject", "scenes/runtime.scene" );
var callerAliasRejectRevisionBefore = callerAliasRejectAuthority.Revision( "run-caller-alias-reject", "scenes/runtime.scene" );
var callerAliasRejectSequenceBefore = callerAliasRejectAuthority.LastSequence( "run-caller-alias-reject", "scenes/runtime.scene" );
var callerAliasRejectTickBefore = callerAliasRejectAuthority.LastTick( "run-caller-alias-reject", "scenes/runtime.scene" );
var callerAliasRejectReceiptsBefore = callerAliasRejectAuthority.ReceiptCount( "run-caller-alias-reject", "scenes/runtime.scene" );
var callerAliasRejectEditPeer = Guid.NewGuid().ToString();
var callerAliasRejectEditId = "caller-alias-reject-edit";
callerAliasFailEditJournal = true;
var callerAliasRejectThrew = false;
Packet callerAliasRejectReceipt = null;
try
{
    callerAliasRejectReceipt = callerAliasRejectAuthority.Apply( callerAliasRejectEdit = RuntimeEditPacket(
        callerAliasRejectEditId, callerAliasRejectEditPeer, 2, 8,
        new[] { new Change( callerAliasRejectObjectId.ToString( "D" ), callerAliasRejectObject,
            RuntimeValues( callerAliasRejectObject, name: "must-not-commit" ) ) },
        runId: "run-caller-alias-reject" ) );
}
catch (Exception)
{
    callerAliasRejectThrew = true;
}
var callerAliasRejectStateAfter = callerAliasRejectAuthority.State( "run-caller-alias-reject", "scenes/runtime.scene" );
Assert(!callerAliasRejectThrew && callerAliasRejectReceipt is not null && callerAliasRejectReceipt.Kind == "reject" &&
    callerAliasRejectReceipt.Id == callerAliasRejectEditId && callerAliasRejectReceipt.Peer == callerAliasRejectEditPeer &&
    callerAliasRejectReceipt.RuntimeRunId == "run-caller-alias-reject" && callerAliasRejectReceipt.Scene == "scenes/runtime.scene" &&
    callerAliasRejectReceipt.RuntimeSequence == 2 && callerAliasRejectReceipt.RuntimeTick == 8 &&
    !string.IsNullOrWhiteSpace( callerAliasRejectReceipt.Error ) && callerAliasRejectJournalInvocations == 2 &&
    callerAliasRejectJournalPacket is not null && callerAliasRejectJournalPacket.Id == callerAliasRejectEditId &&
    callerAliasRejectJournalPacket.Peer == callerAliasRejectEditPeer && callerAliasRejectJournalPacket.RuntimeSequence == 2 &&
    callerAliasRejectAuthority.Revision( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectRevisionBefore &&
    callerAliasRejectAuthority.LastSequence( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectSequenceBefore &&
    callerAliasRejectAuthority.LastTick( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectTickBefore &&
    callerAliasRejectAuthority.ReceiptCount( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectReceiptsBefore &&
    callerAliasRejectStateAfter.Count == callerAliasRejectStateBefore.Count &&
    StateRules.Equal( callerAliasRejectStateAfter[callerAliasRejectObjectId.ToString( "D" )], callerAliasRejectObject ),
    "failed journal cannot expose mutated caller identity or partially advance state revision cursors or receipts" );

// Regression: the same malformed caller packet remains retryable from an independent,
// pristine copy after the failed journal, proving failure reporting used captured metadata.
callerAliasFailEditJournal = false;
var callerAliasRejectRetry = callerAliasRejectAuthority.Apply( RuntimeEditPacket(
    callerAliasRejectEditId, callerAliasRejectEditPeer, 2, 8,
    new[] { new Change( callerAliasRejectObjectId.ToString( "D" ), callerAliasRejectObject,
        RuntimeValues( callerAliasRejectObject, name: "must-not-commit" ) ) },
    runId: "run-caller-alias-reject" ) );
Assert(callerAliasRejectRetry.Kind == "accepted" && callerAliasRejectRetry.Revision == callerAliasRejectRevisionBefore + 1 &&
    callerAliasRejectAuthority.LastSequence( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectSequenceBefore + 1 &&
    callerAliasRejectAuthority.LastTick( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectTickBefore + 1 &&
    callerAliasRejectAuthority.ReceiptCount( "run-caller-alias-reject", "scenes/runtime.scene" ) == callerAliasRejectReceiptsBefore + 1 &&
    StateRules.Equal( callerAliasRejectAuthority.State( "run-caller-alias-reject", "scenes/runtime.scene" )[callerAliasRejectObjectId.ToString( "D" )],
        RuntimeValues( callerAliasRejectObject, name: "must-not-commit" ) ),
    "caller packet aliased by a failed journal remains retryable from its captured operation identity" );

// Regression: re-entrant Apply during the commit window must be rejected, and the outer
// operation must remain the sole commit. The rejected nested operation remains retryable.
var reentrantObjectId = Guid.NewGuid();
var reentrantObject = RuntimeObjWithId( "reentrant", reentrantObjectId.ToString( "D" ) );
RuntimeAuthority reentrantAuthority = null;
Packet reentrantNestedReceipt = null;
var reenterJournal = false;
var callbackInProgress = false;
reentrantAuthority = new RuntimeAuthority( packet =>
{
    if ( !reenterJournal || callbackInProgress ) return;
    callbackInProgress = true;
    try
    {
        var nestedBefore = reentrantAuthority.State( "run-reentrant", "scenes/runtime.scene" )[reentrantObjectId.ToString( "D" )];
        reentrantNestedReceipt = reentrantAuthority.Apply( RuntimeEditPacket(
            "reentrant-inner", Guid.NewGuid().ToString(), 3, 3,
            new[] { new Change( reentrantObjectId.ToString( "D" ), nestedBefore, RuntimeValues( nestedBefore, name: "inner" ) ) },
            runId: "run-reentrant" ) );
    }
    finally
    {
        callbackInProgress = false;
    }
} );
Assert( reentrantAuthority.Apply( RuntimeSnapshotPacket( "reentrant-seed", Guid.NewGuid().ToString(),
    new[] { reentrantObject }, 1, 1, runId: "run-reentrant" ) ).Kind == "accepted",
    "re-entrant regression fixture snapshot is accepted" );
var reentrantBefore = reentrantAuthority.State( "run-reentrant", "scenes/runtime.scene" )[reentrantObjectId.ToString( "D" )];
var reentrantOuterPacket = RuntimeEditPacket( "reentrant-outer", Guid.NewGuid().ToString(), 2, 2,
    new[] { new Change( reentrantObjectId.ToString( "D" ), reentrantBefore, RuntimeValues( reentrantBefore, name: "outer" ) ) },
    runId: "run-reentrant" );
reenterJournal = true;
var reentrantOuterReceipt = reentrantAuthority.Apply( reentrantOuterPacket );
Assert( reentrantOuterReceipt.Kind == "accepted" && reentrantNestedReceipt is not null &&
    reentrantNestedReceipt.Kind == "reject" && reentrantNestedReceipt.Error.Contains( "re-entrant", StringComparison.OrdinalIgnoreCase ) &&
    reentrantAuthority.Revision( "run-reentrant", "scenes/runtime.scene" ) == 2 &&
    reentrantAuthority.LastSequence( "run-reentrant", "scenes/runtime.scene" ) == 2 &&
    reentrantAuthority.State( "run-reentrant", "scenes/runtime.scene" )[reentrantObjectId.ToString( "D" )].Data["Name"]!.GetValue<string>() == "outer",
    "re-entrant Apply is rejected and cannot overwrite outer commit ordering or state" );
reenterJournal = false;
var reentrantOuterState = reentrantAuthority.State( "run-reentrant", "scenes/runtime.scene" )[reentrantObjectId.ToString( "D" )];
var reentrantRetry = reentrantAuthority.Apply( RuntimeEditPacket(
    "reentrant-inner", Guid.NewGuid().ToString(), 3, 3,
    new[] { new Change( reentrantObjectId.ToString( "D" ), reentrantOuterState, RuntimeValues( reentrantOuterState, name: "inner" ) ) },
    runId: "run-reentrant" ) );
Assert( reentrantRetry.Kind == "accepted" && reentrantRetry.Revision == 3 &&
    reentrantAuthority.State( "run-reentrant", "scenes/runtime.scene" )[reentrantObjectId.ToString( "D" )].Data["Name"]!.GetValue<string>() == "inner",
    "a re-entrant rejection is not retained and the nested operation can be retried" );

Assert( HasMissingNamespaceError( () => runtimeAuthority.State( null, "scenes/runtime.scene" ) ) &&
    HasMissingNamespaceError( () => runtimeAuthority.Revision( null, "scenes/runtime.scene" ) ) &&
    HasMissingNamespaceError( () => runtimeAuthority.LastSequence( "run-1", null ) ) &&
    HasMissingNamespaceError( () => runtimeAuthority.LastTick( null, null ) ) &&
    HasMissingNamespaceError( () => runtimeAuthority.ReceiptCount( null, "scenes/runtime.scene" ) ),
    "null runtime query arguments report MissingNamespace instead of NullReferenceException" );

// Regression: a compare-and-swap Before state is semantically identical when object,
// serialized object, and parent GUIDs use a valid alternate representation. The
// authority must compare canonical identities and serialized identity, not raw strings.
var alternateGuidAuthority = new RuntimeAuthority( _ => { } );
var alternateGuidParentId = Guid.NewGuid();
var alternateGuidChildId = Guid.NewGuid();
var alternateGuidParent = RuntimeObjWithId( "alternate-parent", alternateGuidParentId.ToString( "D" ) );
var alternateGuidChild = RuntimeObjWithId( "alternate-child", alternateGuidChildId.ToString( "D" ), alternateGuidParentId.ToString( "D" ) );
var alternateGuidSeedReceipt = alternateGuidAuthority.Apply( RuntimeSnapshotPacket(
    "alternate-guid-seed",
    Guid.NewGuid().ToString(),
    new[] { alternateGuidParent, alternateGuidChild },
    1,
    1,
    runId: "run-alternate-guid" ) );
Assert(alternateGuidSeedReceipt.Kind == "accepted", "alternate GUID regression fixture snapshot is accepted");
var alternateGuidBeforeData = (JsonObject)alternateGuidChild.Data.DeepClone();
alternateGuidBeforeData["__guid"] = alternateGuidChildId.ToString( "N" );
var alternateGuidBefore = new ObjectState(
    alternateGuidChildId.ToString( "N" ),
    alternateGuidParentId.ToString( "N" ),
    alternateGuidBeforeData );
var alternateGuidAfter = RuntimeValues( alternateGuidChild, name: "alternate-guid-accepted" );
var alternateGuidEditReceipt = alternateGuidAuthority.Apply( RuntimeEditPacket(
    "alternate-guid-edit",
    Guid.NewGuid().ToString(),
    2,
    2,
    new[] { new Change( alternateGuidChildId.ToString( "N" ), alternateGuidBefore, alternateGuidAfter ) },
    runId: "run-alternate-guid" ) );
Assert(alternateGuidEditReceipt.Kind == "accepted" &&
    alternateGuidAuthority.State( "run-alternate-guid", "scenes/runtime.scene" )[alternateGuidChildId.ToString( "D" )].Data["Name"]!.GetValue<string>() == "alternate-guid-accepted",
    "runtime authority accepts equivalent alternate GUID representations in compare-and-swap before state");

var alternateGuidCurrent = alternateGuidAuthority.State( "run-alternate-guid", "scenes/runtime.scene" )[alternateGuidChildId.ToString( "D" )];
var alternateGuidMalformedBefore = alternateGuidBefore with { Parent = "not-a-guid" };
var alternateGuidMalformedReceipt = alternateGuidAuthority.Apply( RuntimeEditPacket(
    "alternate-guid-malformed",
    Guid.NewGuid().ToString(),
    3,
    3,
    new[] { new Change( alternateGuidChildId.ToString( "N" ), alternateGuidMalformedBefore, alternateGuidAfter ) },
    runId: "run-alternate-guid" ) );
Assert(alternateGuidMalformedReceipt.Kind == "reject" &&
    alternateGuidMalformedReceipt.Error.StartsWith( "Runtime object identity is invalid", StringComparison.Ordinal ),
    "runtime authority still rejects malformed alternate before identities");
var alternateGuidMismatchedData = (JsonObject)alternateGuidBefore.Data.DeepClone();
alternateGuidMismatchedData["__guid"] = Guid.NewGuid().ToString( "N" );
var alternateGuidMismatchedBefore = alternateGuidBefore with { Data = alternateGuidMismatchedData };
var alternateGuidMismatchedReceipt = alternateGuidAuthority.Apply( RuntimeEditPacket(
    "alternate-guid-mismatch",
    Guid.NewGuid().ToString(),
    3,
    3,
    new[] { new Change( alternateGuidChildId.ToString( "N" ), alternateGuidMismatchedBefore, alternateGuidAfter ) },
    runId: "run-alternate-guid" ) );
Assert(alternateGuidMismatchedReceipt.Kind == "reject" &&
    alternateGuidMismatchedReceipt.Error.StartsWith( "Runtime object identity is invalid", StringComparison.Ordinal ),
    "runtime authority rejects a different serialized object identity in before state");
var alternateGuidStaleBefore = RuntimeValues( alternateGuidCurrent, name: "genuinely-stale-before" );
var alternateGuidStaleAfter = RuntimeValues( alternateGuidCurrent, name: "genuinely-stale-after" );
var alternateGuidConflictReceipt = alternateGuidAuthority.Apply( RuntimeEditPacket(
    "alternate-guid-conflict",
    Guid.NewGuid().ToString(),
    3,
    3,
    new[] { new Change( alternateGuidChildId.ToString( "D" ), alternateGuidStaleBefore, alternateGuidStaleAfter ) },
    runId: "run-alternate-guid" ) );
Assert(alternateGuidConflictReceipt.Kind == "reject" &&
    alternateGuidConflictReceipt.Error.Contains( "Runtime conflict", StringComparison.Ordinal ) &&
    StateRules.Equal( alternateGuidAuthority.State( "run-alternate-guid", "scenes/runtime.scene" )[alternateGuidChildId.ToString( "D" )], alternateGuidCurrent ),
    "runtime authority still conflicts for a genuinely different before state");

var runtimeAPosition = RuntimeValues(runtimeA, position: "1,2,3", enabled: true);
var runtimeARotation = RuntimeValues(runtimeAPosition, rotation: "0,90,0,1", tags: "moving");
var runtimeAEdit = RuntimeEditPacket("edit-a", runtimeSeed.Peer, 2, 11,
    new[] { new Change(runtimeA.Id, runtimeA, runtimeARotation) });
var runtimeAReceipt = runtimeAuthority.Apply(runtimeAEdit);
Assert(runtimeAReceipt.Kind == "accepted" && runtimeAReceipt.Revision == 2, "same-run runtime sequences are accepted in increasing order");
var runtimeBName = RuntimeValues(runtimeB, name: "runtime-b-current");
var runtimeBReceipt = runtimeAuthority.Apply(RuntimeEditPacket(runtimeAEdit.Id, Guid.NewGuid().ToString(), 3, 12,
    new[] { new Change(runtimeB.Id, runtimeB, runtimeBName) }));
Assert(runtimeBReceipt.Kind == "accepted" && runtimeBReceipt.Revision == 3, "runtime operation sequence is monotonic per run");
Assert(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id].Data["Position"]!.GetValue<string>() == "1,2,3" &&
    runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id].Data["Enabled"]!.GetValue<bool>() &&
    runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id].Data["Rotation"]!.GetValue<string>() == "0,90,0,1" &&
    runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id].Data["Tags"]!.GetValue<string>() == "moving" &&
    runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id].Data["Name"]!.GetValue<string>() == "runtime-b-current",
    "independent runtime objects merge without losing multiple properties");

var runtimeDuplicate = runtimeAuthority.Apply(runtimeAEdit);
Assert(runtimeDuplicate.Kind == runtimeAReceipt.Kind && runtimeDuplicate.Revision == runtimeAReceipt.Revision &&
    runtimeDuplicate.Id == runtimeAReceipt.Id && runtimeDuplicate.RuntimeSequence == runtimeAReceipt.RuntimeSequence &&
    runtimeDuplicate.RuntimeTick == runtimeAReceipt.RuntimeTick && runtimeJournal.Count == 3,
    "duplicate runtime operation ID returns the original receipt without journaling twice");
runtimeDuplicate.Error = "caller mutation";
runtimeDuplicate.Revision = 999;
var runtimeDuplicateAgain = runtimeAuthority.Apply(runtimeAEdit);
Assert(runtimeDuplicateAgain.Kind == "accepted" && runtimeDuplicateAgain.Error is null &&
    runtimeDuplicateAgain.Revision == runtimeAReceipt.Revision && runtimeDuplicateAgain.RuntimeSequence == runtimeAReceipt.RuntimeSequence,
    "caller mutation cannot alter the retained duplicate runtime receipt");
var runtimeStateAfterDuplicate = runtimeAuthority.State("run-1", "scenes/runtime.scene");
Assert(runtimeStateAfterDuplicate[runtimeA.Id].Data["Rotation"]!.GetValue<string>() == "0,90,0,1" &&
    runtimeStateAfterDuplicate[runtimeB.Id].Data["Name"]!.GetValue<string>() == "runtime-b-current",
    "duplicate runtime operation cannot mutate state a second time");

var runtimeBeforeRejected = runtimeAuthority.Revision("run-1", "scenes/runtime.scene");
var runtimeSequenceBeforeRejected = runtimeAuthority.LastSequence("run-1", "scenes/runtime.scene");
var runtimeJournalBeforeRejected = runtimeJournal.Count;
var runtimeReceiptCountBeforeRejected = runtimeAuthority.ReceiptCount("run-1", "scenes/runtime.scene");
var runtimeInvalidA = RuntimeValues(runtimeA, name: "invalid-networked");
runtimeInvalidA.Data["NetworkMode"] = 3;
var runtimeRejected = runtimeAuthority.Apply(RuntimeEditPacket("reject-invalid", Guid.NewGuid().ToString(), 4, 13,
    new[] { new Change(runtimeA.Id, runtimeAPosition, runtimeInvalidA) }));
Assert(runtimeRejected.Kind == "reject" && runtimeRejected.Error.Contains("not allowlisted", StringComparison.Ordinal),
    "runtime schema invalid operation returns an actionable reject");
Assert(runtimeAuthority.Revision("run-1", "scenes/runtime.scene") == runtimeBeforeRejected &&
    runtimeAuthority.LastSequence("run-1", "scenes/runtime.scene") == runtimeSequenceBeforeRejected &&
    runtimeAuthority.ReceiptCount("run-1", "scenes/runtime.scene") == runtimeReceiptCountBeforeRejected &&
    runtimeJournal.Count == runtimeJournalBeforeRejected &&
    StateRules.Equal(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id], runtimeARotation) &&
    StateRules.Equal(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id], runtimeBName),
    "ordinary runtime validation rejection preserves state revision sequence receipts and journal");

var runtimeCurrentA = runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id];
var runtimeMultiIndependent = RuntimeValues(runtimeB, position: "9,8,7", enabled: false);
var runtimeMultiStale = RuntimeValues(runtimeA, position: "99,99,99");
var runtimeMultiReject = runtimeAuthority.Apply(RuntimeEditPacket("reject-multi", Guid.NewGuid().ToString(), 4, 13,
    new[]
    {
        new Change(runtimeB.Id, runtimeBName, runtimeMultiIndependent),
        new Change(runtimeA.Id, runtimeA, runtimeMultiStale)
    }));
Assert(runtimeMultiReject.Kind == "reject" && runtimeMultiReject.Error.Contains(runtimeA.Id, StringComparison.Ordinal),
    "same-object stale runtime edit rejects an entire multi-change operation");
Assert(StateRules.Equal(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeA.Id], runtimeCurrentA) &&
    StateRules.Equal(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id], runtimeBName),
    "same-object stale runtime edit rejects without partial mutation");

var runtimeAfterRejectReceipt = runtimeAuthority.Apply(RuntimeEditPacket("after-reject", Guid.NewGuid().ToString(), 4, 14,
    new[] { new Change(runtimeB.Id, runtimeBName, runtimeMultiIndependent) }));
Assert(runtimeAfterRejectReceipt.Kind == "accepted" && runtimeAfterRejectReceipt.Revision == runtimeBeforeRejected + 1,
    "a rejected sequence remains available to the next valid operation");
Assert(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id].Data["Position"]!.GetValue<string>() == "9,8,7" &&
    !runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id].Data["Enabled"]!.GetValue<bool>(),
    "operation after a rejected sequence commits only its independent object changes");

var runtimeCurrentB = runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id];
var runtimeLate = RuntimeEditPacket("late", Guid.NewGuid().ToString(), 3, 12,
    new[] { new Change(runtimeB.Id, runtimeBName, RuntimeValues(runtimeB, name: "late")) });
var runtimeLateReceipt = runtimeAuthority.Apply(runtimeLate);
Assert(runtimeLateReceipt.Kind == "reject" && runtimeLateReceipt.RuntimeSequence == 3 && runtimeLateReceipt.RuntimeTick == 12 &&
    !string.IsNullOrWhiteSpace(runtimeLateReceipt.RuntimeRunId) && runtimeLateReceipt.Scene == "scenes/runtime.scene" &&
    runtimeLateReceipt.Error.Contains("stale", StringComparison.Ordinal) && runtimeLateReceipt.Id == "late" && runtimeLateReceipt.Revision == runtimeBeforeRejected + 1,
    "late earlier tick or sequence envelope returns an identity-preserving actionable reject");
Assert(StateRules.Equal(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id], runtimeCurrentB),
    "late runtime packet does not mutate current state");
var runtimeStaleSequence = RuntimeEditPacket("stale-sequence", Guid.NewGuid().ToString(), 4, 15,
    new[] { new Change(runtimeB.Id, runtimeCurrentB, RuntimeValues(runtimeB, name: "stale-sequence")) });
Assert(runtimeAuthority.Apply(runtimeStaleSequence).Kind == "reject" && StateRules.Equal(runtimeAuthority.State("run-1", "scenes/runtime.scene")[runtimeB.Id], runtimeCurrentB),
    "duplicate sequence is rejected without state mutation");

var runtimeNewRun = RuntimeSnapshotPacket(runtimeSeed.Id, runtimeSeed.Peer, new[] { runtimeA, runtimeB }, 1, 1, runId: "run-2");
var runtimeNewRunReceipt = runtimeAuthority.Apply(runtimeNewRun);
Assert(runtimeNewRunReceipt.Kind == "accepted" && runtimeNewRunReceipt.Revision == 1 &&
    runtimeAuthority.Revision("run-1", "scenes/runtime.scene") == runtimeBeforeRejected + 1,
    "new runtime run ID starts a fresh authority namespace");
Assert(runtimeAuthority.State("run-2", "scenes/runtime.scene")[runtimeA.Id].Data["Position"] is null &&
    runtimeAuthority.State("run-2", "scenes/runtime.scene")[runtimeB.Id].Data["Name"]!.GetValue<string>() == "runtime-b",
    "new runtime run does not collide with prior run state");

var wrongScene = RuntimeEditPacket("wrong-scene", Guid.NewGuid().ToString(), 8, 20,
    new[] { new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "wrong-scene")) },
    scene: "scenes/other.scene");
var wrongSceneReceipt = runtimeAuthority.Apply(wrongScene);
Assert(wrongSceneReceipt.Kind == "reject" && wrongSceneReceipt.Scene == "scenes/other.scene" && wrongSceneReceipt.RuntimeRunId == "run-1" &&
    wrongSceneReceipt.Id == "wrong-scene" && wrongSceneReceipt.Error.Contains("scene", StringComparison.OrdinalIgnoreCase),
    "runtime operation for a different scene path is rejected");
var unsafeScene = RuntimeEditPacket("unsafe-scene", Guid.NewGuid().ToString(), 8, 20,
    new[] { new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "unsafe-scene")) },
    scene: "scenes/../other.scene");
Assert(runtimeAuthority.Apply(unsafeScene).Kind == "reject", "runtime authority rejects unsafe scene paths");
var wrongMode = RuntimeEditPacket("wrong-mode", Guid.NewGuid().ToString(), 8, 20,
    new[] { new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "wrong-mode")) },
    mode: CollaborationMode.Source);
var wrongVersion = RuntimeEditPacket("wrong-version", Guid.NewGuid().ToString(), 8, 20,
    new[] { new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "wrong-version")) },
    version: Protocol.CurrentVersion - 1);
var wrongRun = RuntimeEditPacket("wrong-run", Guid.NewGuid().ToString(), 8, 20,
    new[] { new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "wrong-run")) },
    runId: "run-3");
Assert(runtimeAuthority.Apply(wrongMode).Kind == "reject" && runtimeAuthority.Apply(wrongVersion).Kind == "reject" && runtimeAuthority.Apply(wrongRun).Kind == "reject",
    "runtime authority rejects wrong mode version and run envelopes");

var sourcePacketAttempt = Edit("source-peer", new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "source-edit")));
var sourceAttemptReceipt = runtimeAuthority.Apply(sourcePacketAttempt);
Assert(sourceAttemptReceipt.Kind == "reject" && sourceAttemptReceipt.Error.Contains("runtime-operation", StringComparison.OrdinalIgnoreCase),
    "source operations cannot use the runtime authority");

var runtimeJournalFailureList = new List<Packet>();
var runtimeFailJournal = true;
var runtimeJournalInvocationCount = 0;
var runtimeFailingAuthority = new RuntimeAuthority(packet =>
{
    runtimeJournalInvocationCount++;
    if (runtimeFailJournal) throw new IOException("runtime journal unavailable");
    runtimeJournalFailureList.Add(packet);
});
var runtimeJournalSeed = RuntimeSnapshotPacket("journal-seed-run-2", Guid.NewGuid().ToString(),
    new[] { runtimeA, runtimeB }, 1, 1, runId: "run-2");
runtimeFailJournal = false;
Assert(runtimeFailingAuthority.Apply(runtimeJournalSeed).Kind == "accepted", "runtime journal failure fixture seeds a separate run namespace");
var runtimeBeforeJournalFailure = runtimeFailingAuthority.Revision("run-2", "scenes/runtime.scene");
var runtimeSequenceBeforeJournalFailure = runtimeFailingAuthority.LastSequence("run-2", "scenes/runtime.scene");
var runtimeTickBeforeJournalFailure = runtimeFailingAuthority.LastTick("run-2", "scenes/runtime.scene");
var runtimeReceiptCountBeforeJournalFailure = runtimeFailingAuthority.ReceiptCount("run-2", "scenes/runtime.scene");
var runtimeJournalInvocationCountBeforeFailure = runtimeJournalInvocationCount;
var runtimeFailingEdit = RuntimeEditPacket("journal-failure", Guid.NewGuid().ToString(), 2, 2,
    new[] { new Change(runtimeA.Id, runtimeA, RuntimeValues(runtimeA, name: "must-not-commit")) },
    runId: "run-2");
runtimeFailJournal = true;
var runtimeJournalFailureReceipt = runtimeFailingAuthority.Apply(runtimeFailingEdit);
Assert(runtimeJournalFailureReceipt.Kind == "reject" && runtimeJournalFailureReceipt.Error.Contains("journal", StringComparison.OrdinalIgnoreCase) &&
    runtimeJournalFailureReceipt.Id == "journal-failure" && runtimeJournalFailureReceipt.RuntimeRunId == "run-2" &&
    runtimeFailingAuthority.Revision("run-2", "scenes/runtime.scene") == runtimeBeforeJournalFailure &&
    runtimeFailingAuthority.LastSequence("run-2", "scenes/runtime.scene") == runtimeSequenceBeforeJournalFailure &&
    runtimeFailingAuthority.LastTick("run-2", "scenes/runtime.scene") == runtimeTickBeforeJournalFailure &&
    runtimeFailingAuthority.ReceiptCount("run-2", "scenes/runtime.scene") == runtimeReceiptCountBeforeJournalFailure &&
    runtimeJournalInvocationCount == runtimeJournalInvocationCountBeforeFailure + 1 &&
    StateRules.Equal(runtimeFailingAuthority.State("run-2", "scenes/runtime.scene")[runtimeA.Id], runtimeA),
    "runtime journal failure rejects before state revision sequence or receipt acknowledgement");
runtimeFailJournal = false;
var runtimeJournalRetry = runtimeFailingAuthority.Apply(runtimeFailingEdit);
Assert(runtimeJournalRetry.Kind == "accepted" && StateRules.Equal(runtimeFailingAuthority.State("run-2", "scenes/runtime.scene")[runtimeA.Id], RuntimeValues(runtimeA, name: "must-not-commit")) &&
    runtimeFailingAuthority.Revision("run-2", "scenes/runtime.scene") == runtimeBeforeJournalFailure + 1,
    "retry after a journal failure is evaluated as new and may commit without replaying the failed acknowledgement");

// Task 5 pure runtime scene boundary. These tests intentionally use opaque
// session identities instead of s&box types so the safety seam remains engine-independent.
var task5Source = new object();
var task5OtherSource = new object();
var task5Path = "scenes/runtime.scene";
var task5SourceSession = new RuntimeSessionDescriptor
{
    Kind = RuntimeSessionKind.Source,
    SessionToken = task5Source,
    IsValid = true,
    IsDestroyed = false,
    SourceScenePath = task5Path
};
var task5RuntimeSession = new RuntimeSessionDescriptor
{
    Kind = RuntimeSessionKind.Game,
    SessionToken = new object(),
    IsValid = true,
    IsDestroyed = false,
    ParentSessionToken = task5Source,
    SourceScenePath = task5Path
};
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( null, task5Source, task5Path ),
    "Runtime session is missing or destroyed",
    "runtime guard rejects a missing runtime session" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession with { IsValid = false }, task5Source, task5Path ),
    "Runtime session is missing or destroyed",
    "runtime guard rejects a destroyed-but-reference-valid runtime session" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession with { IsDestroyed = true }, task5Source, task5Path ),
    "Runtime session is missing or destroyed",
    "runtime guard explicitly rejects a destroyed runtime session" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5SourceSession, task5Source, task5Path ),
    "Runtime scene adapter accepts only GameEditorSession",
    "runtime guard rejects a source SceneEditorSession" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession with { ParentSessionToken = null }, null, task5Path ),
    "Expected source session is missing",
    "runtime guard explicitly rejects a null expected source session" );
Assert( ReferenceEquals(
        RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession, task5Source, task5Path ),
        task5RuntimeSession ),
    "runtime guard accepts a GameEditorSession bound to the expected source session and scene path" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession, task5OtherSource, task5Path ),
    "Runtime session parent is not the expected source session",
    "runtime guard rejects a runtime session attached to a different source session" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession, task5Source, "scenes/other.scene" ),
    "Runtime session source path does not match the configured source path",
    "runtime guard rejects a runtime session attached to a different source scene path" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession with { SourceScenePath = "scenes\\runtime.scene" }, task5Source, task5Path ),
    "Runtime session source path is not a safe scenes/... path",
    "runtime guard rejects a backslash runtime source path instead of normalizing it" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession, task5Source, "scenes/../runtime.scene" ),
    "Configured source path is not a safe scenes/... path",
    "runtime guard rejects unsafe configured traversal before path comparison" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession, task5Source, "assets/runtime.scene" ),
    "Configured source path is not a safe scenes/... path",
    "runtime guard rejects configured paths outside scenes before path comparison" );
AssertRuntimeRejected(
    () => RuntimeSessionGuard.EnsureRuntimeSession( task5RuntimeSession with { SourceScenePath = "scenes/Other.scene" }, task5Source, task5Path ),
    "Runtime session source path does not match the configured source path",
    "runtime guard compares safe source paths with canonical equality" );

var task5SerializedId = Guid.NewGuid();
var task5SerializedComponentId = Guid.NewGuid();
var task5Serialized = new JsonObject
{
    ["__guid"] = JsonValue.Create( task5SerializedId ),
    ["Name"] = "Runtime Only",
    ["Position"] = "1,2,3",
    ["Rotation"] = "0,0,0,1",
    ["Scale"] = "1,1,1",
    ["Enabled"] = true,
    ["Tags"] = "runtime,test",
    ["NetworkMode"] = 2,
    ["Prefab"] = "scenes/source.prefab",
    ["Components"] = new JsonArray
    {
        new JsonObject
        {
            ["__type"] = "Sandbox.ModelRenderer",
            ["__guid"] = JsonValue.Create( task5SerializedComponentId ),
            ["Tint"] = "1,0,0,1",
            ["RenderOptions"] = new JsonObject { ["GameLayer"] = true },
            ["CustomRuntimeState"] = new JsonObject { ["secret"] = 42 }
        }
    },
    ["Children"] = new JsonArray()
};
var task5PrefabSerialized = (JsonObject)task5Serialized.DeepClone();
task5PrefabSerialized.Remove( "Prefab" );
var task5Projected = RuntimeSceneSerialization.ProjectObject( task5PrefabSerialized, "" );
RuntimeStateRules.ValidateObject( task5Projected );
Assert( task5Projected.Data["__guid"]!.GetValue<string>() == task5SerializedId.ToString( "D" ) &&
    ((JsonArray)task5Projected.Data["Components"]!)[0]!["__guid"]!.GetValue<string>() == task5SerializedComponentId.ToString( "D" ),
    "runtime serialization normalizes typed GUID JSON through ToJsonString and JsonNode.Parse" );
Assert( !task5Projected.Data.ContainsKey( "NetworkMode" ) &&
    !((JsonObject)((JsonArray)task5Projected.Data["Components"]!)[0]!).ContainsKey( "RenderOptions" ) &&
    !((JsonObject)((JsonArray)task5Projected.Data["Components"]!)[0]!).ContainsKey( "CustomRuntimeState" ),
    "runtime serialization projects only Task 3 allowlisted object and component fields" );
AssertRuntimeRejected(
    () => RuntimeSceneSerialization.ProjectObject( task5Serialized, "" ),
    "Runtime state is not allowlisted",
    "runtime serialization fails visibly for prefab bookkeeping instead of dropping it" );

var task5CustomComponent = (JsonObject)task5PrefabSerialized.DeepClone();
((JsonArray)task5CustomComponent["Components"]!)[0]!["__type"] = "Game.RuntimeOnlyComponent";
AssertRuntimeRejected(
    () => RuntimeSceneSerialization.ProjectObject( task5CustomComponent, "" ),
    "Runtime component type is not allowlisted",
    "runtime serialization fails visibly for an unsupported runtime component" );
AssertRuntimeRejected(
    () => RuntimeSceneCapture.CaptureSerializedObject( null, "" ),
    "Runtime state shape is invalid: serialized object is missing",
    "runtime object capture fails closed when GameObject.Serialize returns null" );
AssertRuntimeRejected(
    () => RuntimeSceneCapture.EnsureRuntimeObjectEntry( null, true ),
    "Runtime state shape is invalid: runtime object entry is null",
    "runtime snapshot capture fails closed for a null root entry" );
var task5NullChildSerialization = (JsonObject)task5PrefabSerialized.DeepClone();
task5NullChildSerialization["__guid"] = Guid.NewGuid().ToString( "D" );
AssertRuntimeRejected(
    () => RuntimeSceneCapture.EnsureRuntimeObjectEntry( null, false ),
    "Runtime state shape is invalid: runtime object entry is null",
    "runtime snapshot capture fails closed for a null child entry" );
AssertRuntimeRejected(
    () => RuntimeSceneCapture.CaptureObject( null, Guid.NewGuid(), task5PrefabSerialized, "", true ),
    "Runtime state shape is invalid: runtime object entry is null",
    "the shared capture seam rejects a null root entry before serialization" );
AssertRuntimeRejected(
    () => RuntimeSceneCapture.CaptureObject(
        null,
        Guid.NewGuid(),
        task5NullChildSerialization,
        task5SerializedId.ToString( "D" ),
        false ),
    "Runtime state shape is invalid: runtime object entry is null",
    "the shared capture seam rejects a null child entry before serialization" );
var task5MalformedNormalizedJson = JsonNode.Parse("[1,2,3]");
AssertRuntimeRejected(
    () => RuntimeSceneCapture.CaptureSerializedObject( task5MalformedNormalizedJson, "" ),
    "Runtime state shape is invalid: normalized serialization must be a JSON object",
    "runtime object capture reports malformed normalized JSON with an actionable domain error" );
var task5AlternateSerialized = (JsonObject)task5PrefabSerialized.DeepClone();
task5AlternateSerialized["__guid"] = task5SerializedId.ToString( "N" );
var task5AlternateProjected = RuntimeSceneCapture.CaptureObject(
    task5SerializedId,
    task5SerializedId,
    task5AlternateSerialized,
    "",
    true );
Assert( task5AlternateProjected.Id == task5SerializedId.ToString( "D" ) &&
    task5AlternateProjected.Data["__guid"]!.GetValue<string>() == task5SerializedId.ToString( "D" ),
    "runtime object capture accepts equivalent runtime D-form and serialized N-form GUIDs" );
var task5MismatchedSerialized = (JsonObject)task5PrefabSerialized.DeepClone();
task5MismatchedSerialized["__guid"] = Guid.NewGuid().ToString( "D" );
AssertRuntimeRejected(
    () => RuntimeSceneCapture.CaptureObject(
        task5SerializedId,
        task5SerializedId,
        task5MismatchedSerialized,
        "",
        true ),
    $"Runtime object identity is invalid: runtime object id {task5SerializedId:D} does not match serialized __guid",
    "runtime object capture rejects a runtime/serialized identity mismatch" );
var task5MalformedRuntimeId = (JsonObject)task5PrefabSerialized.DeepClone();
task5MalformedRuntimeId["__guid"] = "not-a-guid";
AssertRuntimeRejected(
    () => RuntimeSceneCapture.CaptureObject( new object(), Guid.Empty, task5MalformedRuntimeId, "", true ),
    "Runtime object identity is invalid: runtime object id is missing or invalid",
    "runtime object capture keeps malformed runtime identity failures domain-level" );

var task5Keep = RuntimeObj( "keep" );
var task5Update = RuntimeValues( RuntimeObj( "before" ), name: "after" );
var task5Delete = RuntimeObj( "delete" );
var task5Create = RuntimeObj( "create" );
var task5Before = new Dictionary<string, ObjectState>
{
    [task5Keep.Id] = task5Keep,
    [task5Update.Id] = RuntimeObj( "update-current" ),
    [task5Delete.Id] = task5Delete
};
// Use a stable current state for the update identity while retaining the desired after state.
task5Before[task5Update.Id] = RuntimeObjWithId( "update-current", task5Update.Id );
var task5Target = new Dictionary<string, ObjectState>( task5Before )
{
    [task5Update.Id] = task5Update,
    [task5Create.Id] = task5Create
};
task5Target.Remove( task5Delete.Id );
var task5Plan = RuntimeSceneApplyPlanner.Plan( task5Path, task5Before, task5Target );
Assert( task5Plan.Status == RuntimeSceneApplyStatus.Unsupported &&
    !task5Plan.MutationSupported &&
    task5Plan.Operations.Count == 3 &&
    task5Plan.Operations.Single( x => x.Id == task5Create.Id ).Kind == RuntimeSceneOperationKind.Create &&
    task5Plan.Operations.Single( x => x.Id == task5Update.Id ).Kind == RuntimeSceneOperationKind.Update &&
    task5Plan.Operations.Single( x => x.Id == task5Delete.Id ).Kind == RuntimeSceneOperationKind.Delete,
    "runtime apply planning validates both complete snapshots and emits deterministic target-only operations" );
Assert( task5Plan.Operations.Select( x => x.Id ).SequenceEqual( task5Plan.Operations.Select( x => x.Id ).OrderBy( x => x, StringComparer.Ordinal ) ),
    "runtime apply plan has a stable GUID ordering" );
var task5CanonicalPlanParent = Guid.NewGuid();
var task5CanonicalPlanChild = Guid.NewGuid();
var task5CanonicalParent = RuntimeObjWithId( "canonical-plan-parent", task5CanonicalPlanParent.ToString( "D" ) );
var task5CanonicalChildData = (JsonObject)RuntimeObjWithId( "canonical-plan-child", task5CanonicalPlanChild.ToString( "D" ) ).Data.DeepClone();
task5CanonicalChildData["__guid"] = task5CanonicalPlanChild.ToString( "N" );
var task5CanonicalChild = new ObjectState(
    task5CanonicalPlanChild.ToString( "N" ),
    task5CanonicalPlanParent.ToString( "N" ),
    task5CanonicalChildData );
var task5CanonicalBefore = new Dictionary<string, ObjectState>
{
    [task5CanonicalPlanParent.ToString( "D" )] = task5CanonicalParent,
    [task5CanonicalPlanChild.ToString( "D" )] = task5CanonicalChild
};
var task5CanonicalAfter = new Dictionary<string, ObjectState>
{
    [task5CanonicalPlanParent.ToString( "D" )] = task5CanonicalParent,
    [task5CanonicalPlanChild.ToString( "N" )] = task5CanonicalChild with { Parent = task5CanonicalPlanParent.ToString( "D" ) }
};
var task5CanonicalPlan = RuntimeSceneApplyPlanner.Plan( task5Path, task5CanonicalBefore, task5CanonicalAfter );
Assert( task5CanonicalPlan.Operations.Count == 0,
    "runtime apply planning canonicalizes object, serialized, and parent GUIDs before comparing snapshots" );
var task5DuplicateEquivalent = new Dictionary<string, ObjectState>( task5CanonicalBefore )
{
    [task5CanonicalPlanChild.ToString( "N" )] = task5CanonicalChild
};
AssertRuntimeRejected(
    () => RuntimeSceneApplyPlanner.Plan( task5Path, task5DuplicateEquivalent, task5CanonicalAfter ),
    "Runtime object identity is invalid",
    "runtime apply planning rejects equivalent duplicate object ids" );
AssertRuntimeRejected(
    () => RuntimeSceneApplyPlanner.Plan( task5Path, task5CanonicalBefore,
        new Dictionary<string, ObjectState>( task5CanonicalAfter )
        {
            [task5CanonicalPlanChild.ToString( "N" )] = task5CanonicalChild with { Id = "not-a-guid" }
        } ),
    "Runtime object identity is invalid",
    "runtime apply planning preserves malformed object identity errors" );

var task5InvalidTargetData = (JsonObject)task5Create.Data.DeepClone();
task5InvalidTargetData["NetworkMode"] = 2;
var task5InvalidTarget = new Dictionary<string, ObjectState>( task5Target )
{
    [task5Create.Id] = task5Create with { Data = task5InvalidTargetData }
};
var task5ProductionBoundary = RuntimeSceneTargetOnlyBoundary.Shared;
var task5PlanCallsBefore = task5ProductionBoundary.PlanCalls;
AssertRuntimeRejected(
    () => task5ProductionBoundary.PlanForAdapter(
        task5Path,
        task5Before,
        task5InvalidTarget ),
    "Runtime state is not allowlisted",
    "the production target-only boundary rejects an invalid complete target before producing operations" );
Assert( task5ProductionBoundary.PlanCalls == task5PlanCallsBefore + 1,
    "the production adapter boundary method increments the shared plan-path counter" );
Assert( task5ProductionBoundary.SourceCalls == 0,
    "the production target-only boundary and its counter observe no source Save, Reload, dirty, undo, or apply calls" );
Assert( ReferenceEquals( task5ProductionBoundary, RuntimeSceneTargetOnlyBoundary.Shared ),
    "the production adapter plan path uses the same shared target-only boundary observed by the test" );

var task5GuardSurface = typeof( RuntimeSessionGuard ).GetMethods( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly )
    .Select( x => x.Name )
    .ToArray();
Assert( task5GuardSurface.SequenceEqual( new[] { nameof( RuntimeSessionGuard.EnsureRuntimeSession ) } ),
    "runtime guard exposes only EnsureRuntimeSession, including void methods in the public surface check" );
var task5BoundarySurface = typeof( RuntimeSceneTargetOnlyBoundary ).GetMethods( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly )
    .Select( x => x.Name )
    .ToArray();
Assert( !task5BoundarySurface.Any( x => x is "Save" or "Reload" or "Dirty" or "SetHasUnsavedChanges" or "Undo" or "Apply" ) &&
    task5BoundarySurface.Contains( nameof( RuntimeSceneTargetOnlyBoundary.PlanForAdapter ) ),
    "the production target-only boundary exposes planning but no source-scene mutation operation" );

// Real SessionServer path: the first hello is validated by the same protocol contract and
// malformed envelopes fail closed with a fatal response.
var validHello = ValidServerHello();
var missingVersion = ValidServerHello(); missingVersion.ProtocolVersion = null;
var missingMode = ValidServerHello(); missingMode.Mode = null;
var invalidMode = ValidServerHello(); invalidMode.Mode = (CollaborationMode)99;
var blankRuntimeRunId = ValidServerHello(); blankRuntimeRunId.Mode = CollaborationMode.RuntimeExperimental; blankRuntimeRunId.RuntimeRunId = " ";
var welcome = await ExchangeHelloAsync( validHello );
Assert( welcome.Kind == "welcome" && !string.IsNullOrWhiteSpace( welcome.Session ), "SessionServer accepts valid source hello" );
// Task 2 gate seam: ExchangeHelloAsync leaves peerFacts null, so this exchange proves the
// friend / organization / library / manifest gates are skipped for that seam instead of
// refusing every fixture peer for want of a fact the harness never supplied.
var seamSkipped = await ExchangeHelloAsync( ValidServerHello() );
Assert( seamSkipped.Kind == "welcome" && !string.IsNullOrWhiteSpace( seamSkipped.Session ),
    "a SessionServer with peerFacts = null still admits the fixture peer" );
var missingVersionResponse = await ExchangeHelloAsync( missingVersion );
Assert( missingVersionResponse.Kind == "fatal" && missingVersionResponse.Error.Contains( "protocol version" ), "SessionServer rejects missing protocol version" );
var missingModeResponse = await ExchangeHelloAsync( missingMode );
Assert( missingModeResponse.Kind == "fatal" && missingModeResponse.Error.Contains( "mode" ), "SessionServer rejects missing mode" );
var invalidModeResponse = await ExchangeHelloAsync( invalidMode );
Assert( invalidModeResponse.Kind == "fatal" && invalidModeResponse.Error.Contains( "mode" ), "SessionServer rejects invalid enum mode" );
var blankRuntimeRunIdResponse = await ExchangeHelloAsync( blankRuntimeRunId );
    await RefusedHandshakeDoesNotHoldSlotAsync();

    // ---- Steam token HTTP verification (Task 9) ------------------------------
    // The transport is faked, so the request shape and the decision are asserted without
    // a network. The real Sandbox.Http transport is never exercised here.
    var fakeOk = new FakeTokenTransport( 200, "{\"SteamId\":76561198000000001,\"Status\":\"ok\"}" );
    var vOk = new SteamTokenHttpVerifier( fakeOk );
    Assert( await vOk.VerifyAsync( SteamHello(), default ) == SteamTokenVerdict.Verified,
        "a token the service confirms for the claimed account verifies" );
    Assert( fakeOk.LastUrl == SteamTokenEndpoint.VerifyUrl,
        "verification posts to the documented endpoint URL" );
    Assert( fakeOk.LastBody != null && fakeOk.LastBody.Contains( "\"steamid\":76561198000000001" ),
        "the request body carries the claimed steamid field" );
    Assert( fakeOk.LastBody != null && fakeOk.LastBody.Contains( SteamHello().SteamToken ),
        "the request body carries the token field" );

    // Status-only validation is insufficient: a token that belongs to a DIFFERENT account
    // is a replay and must be refused even though the service said "ok".
    var fakeOther = new FakeTokenTransport( 200, "{\"SteamId\":76561198000000099,\"Status\":\"ok\"}" );
    Assert( await new SteamTokenHttpVerifier( fakeOther ).VerifyAsync( SteamHello(), default )
            == SteamTokenVerdict.SteamIdMismatch,
        "a token verifying for a different account is refused even when the status is ok" );

    Assert( await new SteamTokenHttpVerifier( new FakeTokenTransport( 401, null ) ).VerifyAsync( SteamHello(), default )
            == SteamTokenVerdict.ServiceRejected,
        "a non-200 from the service is a rejection" );
    // Unreachable (null status) must be distinct from a refusal.
    Assert( await new SteamTokenHttpVerifier( new FakeTokenTransport( null, null ) ).VerifyAsync( SteamHello(), default )
            == SteamTokenVerdict.ServiceUnavailable,
        "an unreachable service reports unavailable, never a rejection" );
    Assert( await new SteamTokenHttpVerifier( new ThrowingTokenTransport() ).VerifyAsync( SteamHello(), default )
            == SteamTokenVerdict.ServiceUnavailable,
        "a transport that throws reports unavailable and never verifies" );
    // A garbage body must not parse into a guess.
    Assert( await new SteamTokenHttpVerifier( new FakeTokenTransport( 200, "not json at all" ) ).VerifyAsync( SteamHello(), default )
            != SteamTokenVerdict.Verified,
        "an unparseable response body never yields a verified verdict" );

    // A missing or structurally impossible token must cost no network call at all.
    var counting = new FakeTokenTransport( 200, "{\"SteamId\":76561198000000001,\"Status\":\"ok\"}" );
    var vCount = new SteamTokenHttpVerifier( counting );
    Assert( await vCount.VerifyAsync( SteamHello( steamToken: null ), default ) == SteamTokenVerdict.TokenMissing,
        "a hello with no token is reported as a missing token" );
    Assert( await vCount.VerifyAsync( SteamHello( steamToken: "\0" ), default ) == SteamTokenVerdict.TokenMalformed,
        "a control-character token is reported as malformed" );
    Assert( await vCount.VerifyAsync( SteamHello( steamId: "0" ), default ) == SteamTokenVerdict.SteamIdMismatch,
        "a signed-out SteamID is refused before any request is made" );
    Assert( counting.Calls == 0,
        "no network request is made for a token or identity that cannot possibly verify" );

    // The credential must not escape into anything the caller can surface.
    var secretHello = SteamHello( steamToken: "SUPER-SECRET-TOKEN-VALUE" );
    Assert( !SteamTokenEndpoint.BuildRequestBody( 76561198000000001, secretHello.SteamToken )
                .Contains( "SUPER-SECRET" ) == false,
        "the token is present only in the request body" );
    var verdict = await new SteamTokenHttpVerifier( new ThrowingTokenTransport() ).VerifyAsync( secretHello, default );
    Assert( !verdict.ToString().Contains( "SUPER-SECRET" ),
        "a verdict never carries the token" );

    // ---- Deferred admission: an async verifier must not block or pre-admit ------------
    // With a slow verifier the peer is NOT admitted on the first pump, Pump returns
    // promptly, and admission happens only once the task has completed.
    var gate = new TaskCompletionSource<SteamTokenVerdict>();
    var (slowServer, slowPort) = StartServer( NewServerJournalPath(), hello => gate.Task );
    Wire slowClient = null;
    try
    {
        slowClient = await Wire.Connect( "127.0.0.1", slowPort );
        slowClient.Send( ValidServerHello() );
        var until = DateTime.UtcNow.AddSeconds( 2 );
        while ( DateTime.UtcNow < until ) { slowServer.Pump(); await Task.Delay( 10 ); }

        Assert( slowServer.PendingVerificationCount == 1,
            "a peer whose identity is still being verified is held, not admitted" );
        var early = new List<Packet>();
        while ( slowClient.Incoming.TryDequeue( out var q ) ) early.Add( q );
        Assert( !early.Any( x => x.Kind == "welcome" ),
            "no welcome is sent before the identity check completes" );

        gate.SetResult( SteamTokenVerdict.Verified );
        until = DateTime.UtcNow.AddSeconds( 3 );
        var sawWelcome = false;
        while ( DateTime.UtcNow < until )
        {
            slowServer.Pump();
            if ( slowClient.Incoming.TryDequeue( out var r ) && r.Kind == "welcome" ) { sawWelcome = true; break; }
            await Task.Delay( 10 );
        }
        Assert( sawWelcome, "the peer is admitted once its verification completes" );
        Assert( slowServer.PendingVerificationCount == 0,
            "a completed verification is no longer pending" );
    }
    finally { slowClient?.Dispose(); slowServer.Dispose(); }

    // A verifier that FAULTS must never be read as a verification. It becomes
    // ServiceUnavailable, which AllowUnverifiedPeers may legitimately excuse — so the
    // property to assert is that it is not admitted as a VERIFIED peer, not that it is
    // always refused.
    var (badServer, badPort) = StartServer( NewServerJournalPath(), hello => Task.FromException<SteamTokenVerdict>( new HttpRequestException( "boom" ) ) );
    Wire badClient = null;
    try
    {
        badClient = await Wire.Connect( "127.0.0.1", badPort );
        badClient.Send( ValidServerHello() );
        var until = DateTime.UtcNow.AddSeconds( 3 );
        Packet reply = null;
        while ( DateTime.UtcNow < until )
        {
            badServer.Pump();
            if ( badClient.Incoming.TryDequeue( out reply ) ) break;
            await Task.Delay( 10 );
        }
        // With unverified peers allowed, a faulted check may be admitted — but only as an
        // UNVERIFIED peer. With the setting off it must be refused outright.
        Assert( badServer.PendingVerificationCount == 0,
            "a faulted verification is resolved rather than left pending forever" );

        var (strictServer, strictPort) = StartServer( NewServerJournalPath(), hello => Task.FromException<SteamTokenVerdict>( new HttpRequestException( "boom" ) ) );
        strictServer.AllowUnverifiedPeers = false;
        Wire strictClient = null;
        try
        {
            strictClient = await Wire.Connect( "127.0.0.1", strictPort );
            strictClient.Send( ValidServerHello() );
            until = DateTime.UtcNow.AddSeconds( 3 );
            var got = new List<Packet>();
            while ( DateTime.UtcNow < until )
            {
                strictServer.Pump();
                while ( strictClient.Incoming.TryDequeue( out var r ) ) got.Add( r );
                if ( got.Any( x => x.Kind == "welcome" || x.Kind == "fatal" || x.Kind == "reject" ) ) break;
                await Task.Delay( 10 );
            }
            Assert( got.Any( x => x.Kind == "fatal" || x.Kind == "reject" ) && !got.Any( x => x.Kind == "welcome" ),
                "with unverified peers disallowed, a faulted verification refuses the peer" );

    // A host that supplies no policy at all must be secure by default: an identity check
    // that could not be performed refuses the peer rather than admitting it.
    var (defaultServer, defaultPort) = StartServer( NewServerJournalPath(), hello => Task.FromResult( SteamTokenVerdict.ServiceUnavailable ) );
    Wire defaultClient = null;
    try
    {
        Assert( !defaultServer.AllowUnverifiedPeers,
            "a host refuses unverified peers unless the host explicitly opts in" );
        defaultClient = await Wire.Connect( "127.0.0.1", defaultPort );
        defaultClient.Send( ValidServerHello() );
        var deadline2 = DateTime.UtcNow.AddSeconds( 3 );
        var frames = new List<Packet>();
        while ( DateTime.UtcNow < deadline2 )
        {
            defaultServer.Pump();
            while ( defaultClient.Incoming.TryDequeue( out var f ) ) frames.Add( f );
            if ( frames.Any( x => x.Kind == "welcome" || x.Kind == "fatal" || x.Kind == "reject" ) ) break;
            await Task.Delay( 10 );
        }
        Assert( !frames.Any( x => x.Kind == "welcome" ),
            "an unreachable identity service does not admit a peer on a default host" );
    }
    finally { defaultClient?.Dispose(); defaultServer.Dispose(); }
        }
        finally { strictClient?.Dispose(); strictServer.Dispose(); }
    }
    finally { badClient?.Dispose(); badServer.Dispose(); }
    Assert( !verdict.ToString().Contains( "SUPER-SECRET" ),
        "a verdict never carries the token" );
Assert( blankRuntimeRunIdResponse.Kind == "fatal" && blankRuntimeRunIdResponse.Error.Contains( "runtime run ID" ), "SessionServer rejects blank runtime run ID" );

var collaboration = new CollaborationStateModel();
Assert(collaboration.Mode == CollaborationMode.Source, "collaboration starts in source mode");
Assert(collaboration.RunId is null && collaboration.ScenePath is null, "collaboration starts without runtime state");
Assert(collaboration.EnableRuntime("run-1", "scenes/test.scene") == RuntimeEnableResult.Ok, "runtime explicitly enabled");
Assert(collaboration.Mode == CollaborationMode.RuntimeExperimental, "runtime mode enabled");
Assert(collaboration.RunId == "run-1" && collaboration.ScenePath == "scenes/test.scene", "runtime run and scene retained");
Assert(collaboration.EnableRuntime("run-1", "scenes/test.scene") == RuntimeEnableResult.AlreadyEnabled, "duplicate runtime enable is idempotent");
Assert(collaboration.EnableRuntime("run-2", "scenes/test.scene") == RuntimeEnableResult.RunAlreadyActive && collaboration.RunId == "run-1" && collaboration.ScenePath == "scenes/test.scene", "different run with same scene rejected without changing runtime state");
Assert(collaboration.EnableRuntime("run-1", "scenes/other.scene") == RuntimeEnableResult.RunAlreadyActive, "same run with different scene rejected");
Assert(collaboration.RunId == "run-1" && collaboration.ScenePath == "scenes/test.scene", "rejected same-run scene change leaves runtime state unchanged");
Assert(collaboration.EnableRuntime("run-2", "scenes/other.scene") == RuntimeEnableResult.RunAlreadyActive, "different run rejected while runtime active");
Assert(collaboration.RunId == "run-1" && collaboration.ScenePath == "scenes/test.scene", "rejected run leaves runtime state unchanged");
collaboration.DisableRuntime();
Assert(collaboration.Mode == CollaborationMode.Source, "runtime disabled");
Assert(collaboration.RunId is null && collaboration.ScenePath is null, "disable clears runtime state");
Assert(collaboration.EnableRuntime("run-2", "scenes/other.scene") == RuntimeEnableResult.Ok, "new run enabled after disable");
Assert(collaboration.RunId == "run-2" && collaboration.ScenePath == "scenes/other.scene", "new run state retained");
collaboration.DisableRuntime();
Assert(collaboration.EnableRuntime("", "scenes/test.scene") == RuntimeEnableResult.InvalidRunId, "blank run id rejected");
Assert(collaboration.EnableRuntime("   ", "scenes/test.scene") == RuntimeEnableResult.InvalidRunId, "whitespace run id rejected");
Assert(collaboration.EnableRuntime("run-3", "   ") == RuntimeEnableResult.InvalidScenePath, "whitespace scene path rejected");
Assert(collaboration.EnableRuntime("run-3", "scenes/../secrets.scene") == RuntimeEnableResult.InvalidScenePath, "scene path traversal rejected");
Assert(collaboration.EnableRuntime("run-3", "assets/test.scene") == RuntimeEnableResult.InvalidScenePath, "non-scenes path rejected");
Assert(collaboration.Mode == CollaborationMode.Source && collaboration.RunId is null && collaboration.ScenePath is null, "invalid enable leaves source state unchanged");

// Task 6 play-mode lifecycle. The editor hook supplies an opaque source/runtime
// descriptor, while this coordinator owns only deterministic lifecycle state.
var task6Source = new object();
var task6Runtime = new object();
var task6RuntimeDescriptor = new RuntimeSessionDescriptor
{
    Kind = RuntimeSessionKind.Game,
    SessionToken = task6Runtime,
    ParentSessionToken = task6Source,
    IsValid = true,
    IsDestroyed = false,
    SourceScenePath = "scenes/runtime.scene"
};
var task6PendingEvents = new PlayModeLifecycleEventState();
var task6PendingLifecycle = new PlayModeLifecycle( false, "source-state-v1" );
var task6PendingStart = task6PendingEvents.RecordStartIntent(
    task6PendingLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "pending-run-1" );
var task6PendingBlocked = task6PendingLifecycle.BlockForHotload( "test-blocked" );
var task6RejectedPendingStart = task6PendingEvents.RecordStartIntent(
    task6PendingLifecycle,
    new object(),
    "scenes/other.scene",
    "pending-run-2" );
Assert( task6PendingStart.Accepted && task6PendingBlocked.Accepted && !task6RejectedPendingStart.Accepted &&
    task6PendingEvents.Pending is { SourceScenePath: "scenes/runtime.scene", RuntimeRunId: "pending-run-1" } &&
    ReferenceEquals( task6PendingEvents.Pending.SourceSessionToken, task6Source ),
    "a rejected scene.startplay preserves the prior pending source, path, and run candidate" );
var task6StoppedEvents = new PlayModeLifecycleEventState();
var task6StoppedLifecycle = new PlayModeLifecycle( false, "source-state-v1" );
var task6StoppedStart = task6StoppedEvents.RecordStartIntent(
    task6StoppedLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "pending-run-before-stop" );
var task6StoppedLifecycleBlocked = task6StoppedLifecycle.BlockForHotload( "test-blocked-stop" );
var task6RejectedStoppedStop = task6StoppedEvents.RecordStop( task6StoppedLifecycle );
Assert( task6StoppedStart.Accepted && task6StoppedLifecycleBlocked.Accepted && !task6RejectedStoppedStop.Accepted &&
    task6StoppedEvents.Pending is { SourceScenePath: "scenes/runtime.scene", RuntimeRunId: "pending-run-before-stop" } &&
    ReferenceEquals( task6StoppedEvents.Pending.SourceSessionToken, task6Source ),
    "a rejected scene.stop preserves the prior pending source, path, and run candidate" );
var task6AcceptedStopEvents = new PlayModeLifecycleEventState();
var task6AcceptedStopLifecycle = new PlayModeLifecycle( false, "source-state-v1" );
task6AcceptedStopEvents.RecordStartIntent(
    task6AcceptedStopLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "pending-run-cleared" );
var task6AcceptedStop = task6AcceptedStopEvents.RecordStop( task6AcceptedStopLifecycle );
Assert( task6AcceptedStop.Accepted && task6AcceptedStopLifecycle.State == PlayModeLifecycleState.Source &&
    task6AcceptedStopEvents.Pending is null,
    "an accepted scene.stop clears the pending source, path, and run candidate" );
var task6BoundRunEvents = new PlayModeLifecycleEventState();
var task6BoundRunLifecycle = new PlayModeLifecycle( true, "source-state-bound-run" );
var task6BoundRunStart = task6BoundRunEvents.RecordStartIntent(
    task6BoundRunLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "generated-run-1" );
var task6BoundRunMismatch = task6BoundRunEvents.AttachRuntime(
    task6BoundRunLifecycle,
    task6RuntimeDescriptor,
    "generated-run-2" );
Assert( task6BoundRunStart.Accepted && !task6BoundRunMismatch.Accepted &&
    task6BoundRunLifecycle.State == PlayModeLifecycleState.RuntimeStarting &&
    task6BoundRunLifecycle.RuntimeSessionToken is null &&
    task6BoundRunLifecycle.RunId is null &&
    task6BoundRunLifecycle.PendingScenePath == "scenes/runtime.scene" &&
    task6BoundRunEvents.Pending is { SourceScenePath: "scenes/runtime.scene", RuntimeRunId: "generated-run-1" } &&
    ReferenceEquals( task6BoundRunEvents.Pending.SourceSessionToken, task6Source ),
    "a mismatched runtime run ID is rejected before attach mutates lifecycle or pending identity" );
var task6BoundRunMatch = task6BoundRunEvents.AttachRuntime(
    task6BoundRunLifecycle,
    task6RuntimeDescriptor,
    "generated-run-1" );
Assert( task6BoundRunMatch.Accepted && task6BoundRunLifecycle.State == PlayModeLifecycleState.RuntimeActive &&
    task6BoundRunLifecycle.RunId == "generated-run-1" && task6BoundRunEvents.Pending is null,
    "an exact generated runtime run ID binds the lifecycle and clears its pending candidate" );
var task6InvalidPaddedRunEvents = new PlayModeLifecycleEventState();
var task6InvalidPaddedRunLifecycle = new PlayModeLifecycle( true, "source-state-padded-run" );
var task6InvalidPaddedRunStart = task6InvalidPaddedRunEvents.RecordStartIntent(
    task6InvalidPaddedRunLifecycle,
    task6Source,
    "scenes/runtime.scene",
    " padded-run " );
Assert( !task6InvalidPaddedRunStart.Accepted && task6InvalidPaddedRunLifecycle.State == PlayModeLifecycleState.Source &&
    task6InvalidPaddedRunEvents.Pending is null,
    "a whitespace-padded generated run ID is rejected before recording lifecycle identity" );
var task6InvalidControlRunEvents = new PlayModeLifecycleEventState();
var task6InvalidControlRunLifecycle = new PlayModeLifecycle( true, "source-state-control-run" );
var task6InvalidControlRunStart = task6InvalidControlRunEvents.RecordStartIntent(
    task6InvalidControlRunLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "control\nrun" );
Assert( !task6InvalidControlRunStart.Accepted && task6InvalidControlRunLifecycle.State == PlayModeLifecycleState.Source &&
    task6InvalidControlRunEvents.Pending is null,
    "a control-character generated run ID is rejected before recording lifecycle identity" );
var task6InvalidOversizedRunEvents = new PlayModeLifecycleEventState();
var task6InvalidOversizedRunLifecycle = new PlayModeLifecycle( true, "source-state-oversized-run" );
var task6InvalidOversizedRunStart = task6InvalidOversizedRunEvents.RecordStartIntent(
    task6InvalidOversizedRunLifecycle,
    task6Source,
    "scenes/runtime.scene",
    new string( 'r', RuntimeStateRules.MaxRuntimeRunIdLength + 1 ) );
Assert( !task6InvalidOversizedRunStart.Accepted && task6InvalidOversizedRunLifecycle.State == PlayModeLifecycleState.Source &&
    task6InvalidOversizedRunEvents.Pending is null,
    "an oversized generated run ID is rejected before recording lifecycle identity" );
var task6ValidOpaqueRunEvents = new PlayModeLifecycleEventState();
var task6ValidOpaqueRunLifecycle = new PlayModeLifecycle( true, "source-state-opaque-run" );
var task6ValidOpaqueRunStart = task6ValidOpaqueRunEvents.RecordStartIntent(
    task6ValidOpaqueRunLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "coexist-run" );
var task6ValidOpaqueRunAttach = task6ValidOpaqueRunEvents.AttachRuntime(
    task6ValidOpaqueRunLifecycle,
    task6RuntimeDescriptor,
    "coexist-run" );
Assert( task6ValidOpaqueRunStart.Accepted && task6ValidOpaqueRunAttach.Accepted &&
    task6ValidOpaqueRunLifecycle.RunId == "coexist-run" && task6ValidOpaqueRunEvents.Pending is null,
    "short opaque non-GUID run IDs remain valid lifecycle identities" );
var task6SourceCoexistenceApproved = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    new PlayModeLifecycle( true, "source-state-v1" ) );
var task6ApprovedCoexistenceLifecycle = new PlayModeLifecycle( true, "source-state-v2" );
var task6ApprovedCoexistenceStart = task6ApprovedCoexistenceLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6ApprovedCoexistenceAttach = task6ApprovedCoexistenceLifecycle.AttachRuntime( task6RuntimeDescriptor, "coexist-run", task6Source, "scenes/runtime.scene" );
var task6CurrentRuntimeApproved = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor );
var task6CurrentRuntimeWrongToken = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor with { SessionToken = new object() } );
var task6CurrentRuntimeDestroyed = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor with { IsDestroyed = true } );
var task6CurrentRuntimeInvalid = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor with { IsValid = false } );
var task6CurrentRuntimeWrongParent = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor with { ParentSessionToken = new object() } );
var task6CurrentRuntimeWrongPath = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor with { SourceScenePath = "scenes/other.scene" } );
Assert( task6CurrentRuntimeApproved &&
    !task6CurrentRuntimeWrongToken && !task6CurrentRuntimeDestroyed && !task6CurrentRuntimeInvalid &&
    !task6CurrentRuntimeWrongParent && !task6CurrentRuntimeWrongPath,
    "runtime authoring admission requires the approved current runtime descriptor identity" );
var task6DefaultOffCoexistence = new PlayModeLifecycle( false, "source-state-v1" );
var task6DefaultOffStart = task6DefaultOffCoexistence.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6DefaultOffAttach = task6DefaultOffCoexistence.AttachRuntime( task6RuntimeDescriptor, null, task6Source, "scenes/runtime.scene" );
var task6WrongParentCoexistence = new PlayModeLifecycle( true, "source-state-v3" );
task6WrongParentCoexistence.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6WrongParentAttach = task6WrongParentCoexistence.AttachRuntime( task6RuntimeDescriptor with { ParentSessionToken = new object() }, "wrong-parent-run", task6Source, "scenes/runtime.scene" );
var task6ApprovedAdmissionSource = PlayModeSourceCoexistence.CanSampleAuthoringSource(
    task6Source,
    "scenes/runtime.scene",
    task6ApprovedCoexistenceLifecycle,
    task6RuntimeDescriptor );
Assert( !task6SourceCoexistenceApproved &&
    task6ApprovedCoexistenceStart.Accepted && task6ApprovedCoexistenceAttach.Accepted &&
    task6ApprovedAdmissionSource &&
    !PlayModeSourceCoexistence.CanSampleAuthoringSource( new object(), "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor ) &&
    !PlayModeSourceCoexistence.CanSampleAuthoringSource( task6Source, "scenes/other.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor ) &&
    task6DefaultOffStart.Accepted && task6DefaultOffAttach.Accepted &&
    !PlayModeSourceCoexistence.CanSampleAuthoringSource( task6Source, "scenes/runtime.scene", task6DefaultOffCoexistence, task6RuntimeDescriptor ) &&
    !task6WrongParentAttach.Accepted && task6WrongParentCoexistence.State == PlayModeLifecycleState.RuntimeStarting &&
    !PlayModeSourceCoexistence.CanSampleAuthoringSource( task6Source, "scenes/runtime.scene", task6WrongParentCoexistence, task6RuntimeDescriptor ),
    "only an approved matching opted-in runtime lifecycle allows a playing authoring source to keep source collaboration operational" );
var task6PacketAdmissionMethod = typeof( PlayModeSourceCoexistence ).GetMethods( System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public )
    .SingleOrDefault( method => method.Name == "CanProcessSourcePackets" && method.GetParameters().Length == 5 );
var task6PacketAdmission = task6PacketAdmissionMethod?.Invoke( null, [true, task6Source, "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor] ) as bool?;
var task6PacketAdmissionWrongSource = task6PacketAdmissionMethod?.Invoke( null, [true, new object(), "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor] ) as bool?;
var task6PacketAdmissionStarting = task6PacketAdmissionMethod?.Invoke( null, [true, task6Source, "scenes/runtime.scene", task6WrongParentCoexistence, task6RuntimeDescriptor] ) as bool?;
var task6PacketAdmissionSourceMode = task6PacketAdmissionMethod?.Invoke( null, [false, null, "scenes/runtime.scene", new PlayModeLifecycle( true, "source-state-queued" ), null] ) as bool?;
var task6BlockedAdmissionLifecycle = new PlayModeLifecycle( true, "source-state-blocked-admission" );
task6BlockedAdmissionLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6BlockedAdmissionLifecycle.BlockForHotload( "blocked-admission" );
var task6PacketAdmissionBlocked = task6PacketAdmissionMethod?.Invoke( null, [true, task6Source, "scenes/runtime.scene", task6BlockedAdmissionLifecycle, task6RuntimeDescriptor] ) as bool?;
var task6PacketAdmissionWrongRuntime = task6PacketAdmissionMethod?.Invoke( null, [true, task6Source, "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor with { SessionToken = new object() }] ) as bool?;
var task6PacketAdmissionNonPlaying = task6PacketAdmissionMethod?.Invoke( null, [false, null, "scenes/runtime.scene", new PlayModeLifecycle( true, "source-state-nonplaying" ), null] ) as bool?;
Assert( task6PacketAdmissionMethod is not null && task6PacketAdmission is true && task6PacketAdmissionWrongSource is false && task6PacketAdmissionStarting is false && task6PacketAdmissionBlocked is false && task6PacketAdmissionSourceMode is true && task6PacketAdmissionWrongRuntime is false && task6PacketAdmissionNonPlaying is true,
    "source packet admission rejects starting, blocked, and non-current runtime states while allowing approved active and non-playing source states" );
var task6OtherPlayingSource = new object();
var task6TargetSnapshotAdmission = PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(
    "snapshot", true, task6OtherPlayingSource, "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor, "scenes/runtime.scene" );
var task6TargetAcceptedAdmission = PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(
    "accepted", true, task6OtherPlayingSource, "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor, "scenes/runtime.scene" );
var task6TargetNonPlayingAdmission = PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(
    "snapshot", false, task6OtherPlayingSource, "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, null, "scenes/runtime.scene" );
var task6TargetApprovedAdmission = PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(
    "accepted", true, task6Source, "scenes/runtime.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor, "scenes/runtime.scene" );
var task6TargetOtherSceneAdmission = PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(
    "accepted", true, task6Source, "scenes/other.scene", task6ApprovedCoexistenceLifecycle, task6RuntimeDescriptor, "scenes/other.scene" );
var task6TargetDisabledAdmission = PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(
    "snapshot", true, task6Source, "scenes/runtime.scene", task6DefaultOffCoexistence, task6RuntimeDescriptor, "scenes/runtime.scene" );
Assert( !task6TargetSnapshotAdmission && !task6TargetAcceptedAdmission &&
    task6TargetNonPlayingAdmission && task6TargetApprovedAdmission && !task6TargetOtherSceneAdmission &&
    !task6TargetDisabledAdmission,
    "per-target scene packet admission rejects an other playing source for snapshots and accepted edits while preserving allowed source states" );
var task6Lifecycle = new PlayModeLifecycle( false, "source-state-v1" );
var task6StartIntent = task6Lifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
Assert( task6StartIntent.Accepted &&
    task6Lifecycle.State == PlayModeLifecycleState.RuntimeStarting &&
    task6Lifecycle.SourceStateToken == "source-state-v1" &&
    task6Lifecycle.RunId is null,
    "start intent records a pending transition before a runtime session exists and preserves source state" );
var task6DisabledAttach = task6Lifecycle.AttachRuntime( task6RuntimeDescriptor, null, task6Source, "scenes/runtime.scene" );
Assert( task6DisabledAttach.Accepted &&
    task6Lifecycle.State == PlayModeLifecycleState.RuntimeDisabled &&
    task6Lifecycle.SourceStateToken == "source-state-v1",
    "runtime session does not enable collaboration without explicit opt-in" );
var task6DisabledStop = task6Lifecycle.RecordStop();
Assert( task6DisabledStop.Accepted &&
    task6Lifecycle.State == PlayModeLifecycleState.Source &&
    task6Lifecycle.SourceStateToken == "source-state-v1",
    "stop intent from RuntimeDisabled returns to source mode" );
var task6InitialSourceStopLifecycle = new PlayModeLifecycle( true, "source-state-v4" );
var task6InitialSourceStop = task6InitialSourceStopLifecycle.RecordStop();
var task6InitialSourceStopAgain = task6InitialSourceStopLifecycle.RecordStop();
Assert( task6InitialSourceStop.Accepted && task6InitialSourceStopAgain.Accepted &&
    task6InitialSourceStop.ActionableError is null && task6InitialSourceStopAgain.ActionableError is null &&
    task6InitialSourceStopLifecycle.State == PlayModeLifecycleState.Source &&
    task6InitialSourceStopLifecycle.SourceStateToken == "source-state-v4",
    "stop intent in initial source mode is idempotently accepted without changing source state" );
var task6ArchiveFailStopLifecycle = new PlayModeLifecycle( true, "source-state-archive" );
var task6ArchiveFailStopStart = task6ArchiveFailStopLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6ArchiveFailStopBlock = task6ArchiveFailStopLifecycle.BlockForHotload( "archive-contract" );
var task6RecoveryWithoutArchive = task6ArchiveFailStopLifecycle.RecoverFromHotload();
var task6RecordArchiveMethod = typeof( PlayModeLifecycle ).GetMethod( "RecordHotloadArchive" );
var task6ArchiveRecord = task6RecordArchiveMethod?.Invoke( task6ArchiveFailStopLifecycle, null ) as PlayModeTransitionResult;
var task6RecoveryAfterArchive = task6ArchiveFailStopLifecycle.RecoverFromHotload();
var task6ArchiveRecordedProperty = typeof( PlayModeLifecycle ).GetProperty( "HotloadArchiveRecorded" )?.GetValue( task6ArchiveFailStopLifecycle );
Assert( task6ArchiveFailStopStart.Accepted && task6ArchiveFailStopBlock.Accepted &&
    !task6RecoveryWithoutArchive.Accepted && !string.IsNullOrWhiteSpace( task6RecoveryWithoutArchive.ActionableError ) &&
    task6RecordArchiveMethod is not null && task6ArchiveRecord?.Accepted == true &&
    task6RecoveryAfterArchive.Accepted && task6ArchiveFailStopLifecycle.State == PlayModeLifecycleState.Source &&
    task6ArchiveRecordedProperty is false,
    "hotload recovery reset is fail-stop until a successful recovery archive is recorded" );
var task6BoundArchiveDiagnosticMethod = typeof( PlayModeLifecycle ).GetMethod( "FormatHotloadArchiveFailure", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public );
var task6BoundArchiveDiagnostic = task6BoundArchiveDiagnosticMethod?.Invoke( null, [new Exception( new string( 'x', 1000 ) )] ) as string;
var task6ArchiveFailureLifecycle = new PlayModeLifecycle( true, "source-state-archive-failure" );
task6ArchiveFailureLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6ArchiveFailureLifecycle.BlockForHotload( "archive-failure" );
task6ArchiveFailureLifecycle.RecordHotloadArchiveFailure( new Exception( new string( 'y', 1000 ) ) );
var task6ArchiveFailureDiagnostic = task6ArchiveFailureLifecycle.LastDiagnostic;
var task6NullArchiveDiagnosticLifecycle = new PlayModeLifecycle( true, "source-state-null-archive-failure" );
task6NullArchiveDiagnosticLifecycle.RecordHotloadArchiveFailure( null );
var task6NullArchiveDiagnostic = task6NullArchiveDiagnosticLifecycle.LastDiagnostic;
var task6ArchiveRetryLifecycle = new PlayModeLifecycle( true, "source-state-archive-retry" );
task6ArchiveRetryLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6ArchiveRetryLifecycle.BlockForHotload( "archive-retry" );
var task6ArchiveRetryRecorded = task6ArchiveRetryLifecycle.RecordHotloadArchive();
task6ArchiveRetryLifecycle.RecordHotloadArchiveFailure( new Exception( "retry failed" ) );
var task6ArchiveRetryAfterFailure = task6ArchiveRetryLifecycle.RecoverFromHotload();
Assert( task6ArchiveRetryRecorded.Accepted && !task6ArchiveRetryAfterFailure.Accepted &&
    !task6ArchiveRetryLifecycle.HotloadArchiveRecorded &&
    task6ArchiveRetryLifecycle.State == PlayModeLifecycleState.RuntimeBlocked,
    "a failed archive retry invalidates a prior successful archive marker and blocks recovery" );
var task6NullTeardownLifecycle = new PlayModeLifecycle( true, "source-state-null-teardown" );
task6NullTeardownLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6NullTeardownLifecycle.BlockForHotload( "null-teardown" );
task6NullTeardownLifecycle.RecordHotloadTeardownFailure( null );
var task6NullTeardownDiagnostic = task6NullTeardownLifecycle.LastDiagnostic;
var task6ComposedArchiveAndTeardownDiagnostic = PlayModeLifecycle.FormatHotloadArchiveFailure(
    new Exception( new string( 'a', 1000 ) ) );
task6ComposedArchiveAndTeardownDiagnostic = PlayModeLifecycle.FormatHotloadArchiveFailure(
    new Exception( task6ComposedArchiveAndTeardownDiagnostic + task6NullTeardownDiagnostic ) );
var task6TeardownFailureDiagnosticLifecycle = new PlayModeLifecycle( true, "source-state-teardown-failure" );
task6TeardownFailureDiagnosticLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6TeardownFailureDiagnosticLifecycle.BlockForHotload( "teardown-failure" );
task6TeardownFailureDiagnosticLifecycle.RecordHotloadTeardownFailure( new string( 'q', 1000 ) );
var task6TerminalLifecycleRecovery = task6TeardownFailureDiagnosticLifecycle.RecoverFromHotload();
var task6TerminalLifecycleStart = task6TeardownFailureDiagnosticLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6TerminalLifecycleStop = task6TeardownFailureDiagnosticLifecycle.RecordStop();
var task6TerminalLifecycleSessionStart = task6TeardownFailureDiagnosticLifecycle.ValidateSessionStart();
var task6TerminalLifecycleArchiveRetry = task6TeardownFailureDiagnosticLifecycle.RecordHotloadArchive();
Assert( task6BoundArchiveDiagnosticMethod is not null && task6BoundArchiveDiagnostic is not null &&
    task6BoundArchiveDiagnostic.Length <= 512 && task6BoundArchiveDiagnostic.StartsWith( "Runtime lifecycle blocked, but recovery archive failed: ", StringComparison.Ordinal ) &&
    task6ArchiveFailureDiagnostic?.Length <= 512 && task6ArchiveFailureLifecycle.LastActionableError == task6ArchiveFailureDiagnostic &&
    task6NullArchiveDiagnostic?.Length <= 512 &&
    task6NullTeardownDiagnostic?.Length <= 512 &&
    task6ComposedArchiveAndTeardownDiagnostic.Length <= 512 &&
    task6TeardownFailureDiagnosticLifecycle.LastDiagnostic?.Length <= 512 &&
    task6TeardownFailureDiagnosticLifecycle.LastActionableError == task6TeardownFailureDiagnosticLifecycle.LastDiagnostic &&
    task6TeardownFailureDiagnosticLifecycle.HasTerminalTeardownFailure &&
    !task6TerminalLifecycleRecovery.Accepted && !task6TerminalLifecycleStart.Accepted &&
    !task6TerminalLifecycleStop.Accepted && !task6TerminalLifecycleSessionStart.Accepted &&
    !task6TerminalLifecycleArchiveRetry.Accepted &&
    task6TeardownFailureDiagnosticLifecycle.State == PlayModeLifecycleState.RuntimeBlocked &&
    !task6TeardownFailureDiagnosticLifecycle.HotloadArchiveRecorded,
    "composed hotload archive and teardown failure diagnostics are each bounded to 512 characters" );
Assert( !task6TerminalLifecycleRecovery.Accepted && !task6TerminalLifecycleStart.Accepted &&
    !task6TerminalLifecycleStop.Accepted && !task6TerminalLifecycleSessionStart.Accepted &&
    !task6TerminalLifecycleArchiveRetry.Accepted &&
    task6TeardownFailureDiagnosticLifecycle.HasTerminalTeardownFailure &&
    task6TeardownFailureDiagnosticLifecycle.State == PlayModeLifecycleState.RuntimeBlocked,
    "terminal teardown failure blocks lifecycle recovery, archive retry, and new runtime/session transitions" );
var task6TeardownGate = new ConnectionTeardownGate();
var task6TeardownCompletion = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
var task6TeardownTransport = new TrackedDisposable();
task6TeardownGate.Begin( task6TeardownCompletion.Task );
var task6TeardownPending = task6TeardownGate.IsPending;
task6TeardownCompletion.SetResult( task6TeardownTransport );
var task6TeardownDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6TeardownGate.IsPending && DateTime.UtcNow < task6TeardownDeadline ) await Task.Delay( 10 );
var task6TeardownCompleted = task6TeardownGate.TryTakeCompletion( out var task6TeardownFailure );
Assert( task6TeardownPending && task6TeardownCompleted && task6TeardownFailure is null && task6TeardownTransport.DisposeCount == 1,
    "detached connection teardown remains gated until a successfully produced transport is disposed" );
var task6FaultedTeardownGate = new ConnectionTeardownGate();
task6FaultedTeardownGate.Begin( Task.FromException<IDisposable>( new IOException( "connect fault" ) ) );
var task6FaultedTeardownDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6FaultedTeardownGate.IsPending && DateTime.UtcNow < task6FaultedTeardownDeadline ) await Task.Delay( 10 );
var task6FaultedTeardownFailure = task6FaultedTeardownGate.Failure;
var task6FaultedTeardownCompleted = task6FaultedTeardownGate.TryTakeCompletion( out var task6FaultedTeardownTakenFailure );
var task6FailureAfterTake = task6FaultedTeardownGate.Failure;
var task6FaultedTeardownNoConnectionRejected = false;
var task6FaultedTeardownResetRejected = false;
try
{
    task6FaultedTeardownGate.ResetForRetry();
}
catch ( InvalidOperationException error )
{
    task6FaultedTeardownResetRejected = error.Message.Contains( "failed", StringComparison.OrdinalIgnoreCase );
}
try
{
    task6FaultedTeardownGate.CompleteWithoutConnection();
}
catch ( InvalidOperationException error )
{
    task6FaultedTeardownNoConnectionRejected = error.Message.Contains( "failed", StringComparison.OrdinalIgnoreCase );
}
var task6FaultedTeardownRetryTransport = new TrackedDisposable();
var task6FaultedTeardownRetryRejected = false;
try
{
    task6FaultedTeardownGate.Begin( Task.FromResult<IDisposable>( task6FaultedTeardownRetryTransport ) );
}
catch ( InvalidOperationException error )
{
    task6FaultedTeardownRetryRejected = error.Message.Contains( "failed", StringComparison.OrdinalIgnoreCase );
}
var task6FaultedTeardownRetryCompleted = task6FaultedTeardownGate.TryTakeCompletion( out var task6FaultedTeardownRetryFailure );
Assert( task6FaultedTeardownCompleted && task6FaultedTeardownFailure?.Contains( "connect fault", StringComparison.Ordinal ) == true &&
    task6FaultedTeardownTakenFailure == task6FaultedTeardownFailure && task6FailureAfterTake == task6FaultedTeardownFailure &&
    task6FaultedTeardownGate.FailStopTeardown && task6FaultedTeardownGate.HasTerminalFailure &&
    task6FaultedTeardownNoConnectionRejected && task6FaultedTeardownResetRejected &&
    task6FaultedTeardownRetryRejected &&
    task6FaultedTeardownRetryCompleted && task6FaultedTeardownRetryFailure == task6FaultedTeardownFailure &&
    task6FaultedTeardownGate.Failure == task6FaultedTeardownFailure && task6FaultedTeardownGate.FailStopTeardown &&
    task6FaultedTeardownRetryTransport.DisposeCount == 0,
    "connection teardown failure remains visible and terminal across completion consumption, no-connection completion, and a second begin" );
var task6LongTeardownFailure = "Connection teardown failed: " + new string( 'z', 1000 );
var task6OrdinaryLeaveFailureDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: false,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: false,
    TeardownPending: false,
    TeardownFailure: task6LongTeardownFailure,
    TeardownFailed: true,
    RecoveryDeferred: false ) );
Assert( task6OrdinaryLeaveFailureDecision.Status == HotloadTeardownRecoveryStatus.TerminalTeardownFailure &&
    !task6OrdinaryLeaveFailureDecision.EnableDeferredRecovery &&
    !task6OrdinaryLeaveFailureDecision.AttemptRecovery &&
    task6OrdinaryLeaveFailureDecision.Error?.Length <= 512 &&
    task6OrdinaryLeaveFailureDecision.Error.Contains( "teardown", StringComparison.OrdinalIgnoreCase ),
    "ordinary Leave surfaces a pending-connect teardown failure without enabling or claiming hotload recovery" );
var task6BlockedFailureDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: true,
    TeardownPending: false,
    TeardownFailure: task6LongTeardownFailure,
    TeardownFailed: true,
    RecoveryDeferred: true ) );
Assert( task6BlockedFailureDecision.Status == HotloadTeardownRecoveryStatus.TerminalTeardownFailure &&
    !task6BlockedFailureDecision.EnableDeferredRecovery && !task6BlockedFailureDecision.AttemptRecovery &&
    task6BlockedFailureDecision.Error?.Length <= 512,
    "blocked Leave remains terminal after a teardown failure and never claims hotload recovery" );
var task6LeaveAfterCodeChangedDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: true,
    TeardownPending: true,
    TeardownFailure: null,
    TeardownFailed: false,
    RecoveryDeferred: false ) );
Assert( task6LeaveAfterCodeChangedDecision.Status == HotloadTeardownRecoveryStatus.WaitingForTeardown &&
    !task6LeaveAfterCodeChangedDecision.EnableDeferredRecovery && !task6LeaveAfterCodeChangedDecision.AttemptRecovery,
    "Leave after CodeChanged waits for detached pending teardown without enabling recovery early" );
var task6CompletedTeardownDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: true,
    TeardownPending: false,
    TeardownFailure: null,
    TeardownFailed: false,
    RecoveryDeferred: false ) );
Assert( task6CompletedTeardownDecision.Status == HotloadTeardownRecoveryStatus.Ready &&
    task6CompletedTeardownDecision.EnableDeferredRecovery && task6CompletedTeardownDecision.AttemptRecovery &&
    task6CompletedTeardownDecision.Error is null,
    "Leave after successful archive and teardown enables and immediately attempts deferred hotload recovery" );
var task6CodeChangedCompletedBeforeLeaveDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: false,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: true,
    TeardownPending: false,
    TeardownFailure: null,
    TeardownFailed: false,
    RecoveryDeferred: false ) );
Assert( task6CodeChangedCompletedBeforeLeaveDecision.Status == HotloadTeardownRecoveryStatus.NotRequired &&
    !task6CodeChangedCompletedBeforeLeaveDecision.EnableDeferredRecovery &&
    !task6CodeChangedCompletedBeforeLeaveDecision.AttemptRecovery &&
    task6CodeChangedCompletedBeforeLeaveDecision.Error is null,
    "successful CodeChanged teardown does not enable recovery before Leave" );
var task6PreviouslyDeferredTeardownDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: true,
    TeardownPending: false,
    TeardownFailure: null,
    TeardownFailed: false,
    RecoveryDeferred: true ) );
Assert( task6PreviouslyDeferredTeardownDecision.Status == HotloadTeardownRecoveryStatus.Ready &&
    !task6PreviouslyDeferredTeardownDecision.EnableDeferredRecovery && task6PreviouslyDeferredTeardownDecision.AttemptRecovery &&
    task6PreviouslyDeferredTeardownDecision.Error is null,
    "previously deferred hotload recovery is attempted only after archived state and successful teardown complete" );
var task6FailureBeforeArchiveDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: false,
    HotloadArchiveRecorded: false,
    TeardownPending: false,
    TeardownFailure: task6LongTeardownFailure,
    TeardownFailed: true,
    RecoveryDeferred: false ) );
Assert( task6FailureBeforeArchiveDecision.Status == HotloadTeardownRecoveryStatus.TerminalTeardownFailure &&
    !task6FailureBeforeArchiveDecision.EnableDeferredRecovery && !task6FailureBeforeArchiveDecision.AttemptRecovery,
    "a terminal teardown failure remains visible even when archive retry also failed" );
var task6MissingArchiveTeardownDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: false,
    HotloadArchiveRecorded: false,
    TeardownPending: true,
    TeardownFailure: null,
    TeardownFailed: false,
    RecoveryDeferred: false ) );
Assert( task6MissingArchiveTeardownDecision.Status == HotloadTeardownRecoveryStatus.ArchiveFailed &&
    !task6MissingArchiveTeardownDecision.EnableDeferredRecovery && !task6MissingArchiveTeardownDecision.AttemptRecovery,
    "Leave cannot enable deferred recovery before a successful hotload archive" );
var task6ArchiveFailureDecision = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: false,
    HotloadArchiveRecorded: false,
    TeardownPending: false,
    TeardownFailure: null,
    TeardownFailed: false,
    RecoveryDeferred: false ) );
Assert( task6ArchiveFailureDecision.Status == HotloadTeardownRecoveryStatus.ArchiveFailed &&
    !task6ArchiveFailureDecision.EnableDeferredRecovery && !task6ArchiveFailureDecision.AttemptRecovery,
    "hotload recovery remains fail-stop when its archive was not successfully recorded" );
var task6RejectedResetLifecycle = new PlayModeLifecycle( true, "source-state-reset" );
var task6RejectedResetStart = task6RejectedResetLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6RejectedResetAttach = task6RejectedResetLifecycle.AttachRuntime( task6RuntimeDescriptor, "active-run-before-reset", task6Source, "scenes/runtime.scene" );
var task6RejectedResetDiagnostic = task6RejectedResetLifecycle.LastDiagnostic;
var task6RejectedReset = task6RejectedResetLifecycle.RecoverFromHotload();
Assert( task6RejectedResetStart.Accepted && task6RejectedResetAttach.Accepted && !task6RejectedReset.Accepted &&
    !string.IsNullOrWhiteSpace( task6RejectedReset.ActionableError ) &&
    task6RejectedResetLifecycle.State == PlayModeLifecycleState.RuntimeActive &&
    task6RejectedResetLifecycle.SourceStateToken == "source-state-reset" &&
    task6RejectedResetLifecycle.OptIn &&
    ReferenceEquals( task6RejectedResetLifecycle.SourceSessionToken, task6Source ) &&
    task6RejectedResetLifecycle.SourceScenePath == "scenes/runtime.scene" &&
    ReferenceEquals( task6RejectedResetLifecycle.RuntimeSessionToken, task6Runtime ) &&
    task6RejectedResetLifecycle.RunId == "active-run-before-reset" &&
    task6RejectedResetLifecycle.PendingScenePath is null &&
    task6RejectedResetLifecycle.ArchiveReason is null &&
    task6RejectedResetLifecycle.LastDiagnostic == task6RejectedResetDiagnostic &&
    task6RejectedResetLifecycle.LastActionableError is null,
    "hotload recovery reset rejects outside RuntimeBlocked without mutating lifecycle identity or diagnostics" );
var task6LeaveSourceLifecycle = new PlayModeLifecycle( true, "source-state-leave-source" );
var task6LeaveDisabledLifecycle = new PlayModeLifecycle( false, "source-state-leave-disabled" );
var task6LeaveDisabledStart = task6LeaveDisabledLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6LeaveDisabledAttach = task6LeaveDisabledLifecycle.AttachRuntime( task6RuntimeDescriptor, null, task6Source, "scenes/runtime.scene" );
var task6LeaveActiveLifecycle = new PlayModeLifecycle( true, "source-state-leave-active" );
var task6LeaveActiveStart = task6LeaveActiveLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6LeaveActiveAttach = task6LeaveActiveLifecycle.AttachRuntime( task6RuntimeDescriptor, "leave-active-run", task6Source, "scenes/runtime.scene" );
var task6LeaveBlockedLifecycle = new PlayModeLifecycle( true, "source-state-leave-blocked" );
var task6LeaveBlockedStart = task6LeaveBlockedLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6LeaveBlocked = task6LeaveBlockedLifecycle.BlockForHotload( "leave-blocked-reason" );
Assert( !task6LeaveSourceLifecycle.RequiresHotloadRecoveryAfterLeave &&
    task6LeaveDisabledStart.Accepted && task6LeaveDisabledAttach.Accepted && !task6LeaveDisabledLifecycle.RequiresHotloadRecoveryAfterLeave &&
    task6LeaveActiveStart.Accepted && task6LeaveActiveAttach.Accepted && !task6LeaveActiveLifecycle.RequiresHotloadRecoveryAfterLeave &&
    task6LeaveBlockedStart.Accepted && task6LeaveBlocked.Accepted && task6LeaveBlockedLifecycle.RequiresHotloadRecoveryAfterLeave &&
    task6LeaveSourceLifecycle.LastActionableError is null && task6LeaveDisabledLifecycle.LastActionableError is null && task6LeaveActiveLifecycle.LastActionableError is null,
    "hotload recovery after Leave is gated exclusively by RuntimeBlocked lifecycle state" );
var task6StartGuardLifecycle = new PlayModeLifecycle( true, "source-state-guard" );
var task6StartGuardStart = task6StartGuardLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6StartGuardBlock = task6StartGuardLifecycle.BlockForHotload( "blocked-before-recovery" );
var task6StartGuardBeforeLeave = task6StartGuardLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6StartGuardSurfaceBeforeLeave = task6StartGuardLifecycle.ValidateSessionStart();
var task6StartGuardRecoveryBeforeArchive = task6StartGuardLifecycle.RecoverFromHotload();
var task6StartGuardArchive = task6StartGuardLifecycle.RecordHotloadArchive();
var task6StartGuardAfterLeave = task6StartGuardLifecycle.RecoverFromHotload();
var task6StartGuardSurfaceAfterLeave = task6StartGuardLifecycle.ValidateSessionStart();
var task6StartGuardNewRun = task6StartGuardLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6StartGuardAttach = task6StartGuardLifecycle.AttachRuntime( task6RuntimeDescriptor, "new-run-after-recovery", task6Source, "scenes/runtime.scene" );
Assert( task6StartGuardStart.Accepted && task6StartGuardBlock.Accepted &&
    !task6StartGuardBeforeLeave.Accepted && task6StartGuardBeforeLeave.ActionableError.Contains( "Leave", StringComparison.OrdinalIgnoreCase ) &&
    !task6StartGuardRecoveryBeforeArchive.Accepted && task6StartGuardArchive.Accepted && task6StartGuardAfterLeave.Accepted && task6StartGuardNewRun.Accepted && task6StartGuardAttach.Accepted &&
    !task6StartGuardSurfaceBeforeLeave.Accepted && task6StartGuardSurfaceBeforeLeave.ActionableError.Contains( "Leave", StringComparison.OrdinalIgnoreCase ) &&
    task6StartGuardSurfaceAfterLeave.Accepted &&
    task6StartGuardLifecycle.State == PlayModeLifecycleState.RuntimeActive && task6StartGuardLifecycle.RunId == "new-run-after-recovery",
    "blocked lifecycle start guard remains rejected until Leave recovery reset, then a new runtime run can start" );
var task6RecoveryEvents = new PlayModeLifecycleEventState();
var task6RecoveryLifecycle = new PlayModeLifecycle( true, "source-state-v2" );
var task6RecoveryStart = task6RecoveryEvents.RecordStartIntent(
    task6RecoveryLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "stale-run-before-reset" );
var task6RecoveryActiveAttach = task6RecoveryLifecycle.AttachRuntime(
    task6RuntimeDescriptor,
    "stale-run-before-reset",
    task6Source,
    "scenes/runtime.scene" );
var task6RecoveryBlock = task6RecoveryLifecycle.BlockForHotload( "stale-hotload-reason" );
var task6RecoveryArchive = task6RecoveryLifecycle.RecordHotloadArchive();
var task6Recovery = task6RecoveryEvents.RecoverFromHotload( task6RecoveryLifecycle );
var task6RecoveryIdentityCleared = task6Recovery.Accepted &&
    task6RecoveryLifecycle.State == PlayModeLifecycleState.Source &&
    task6RecoveryLifecycle.SourceStateToken == "source-state-v2" &&
    task6RecoveryLifecycle.OptIn &&
    task6RecoveryLifecycle.SourceSessionToken is null &&
    task6RecoveryLifecycle.SourceScenePath is null &&
    task6RecoveryLifecycle.PendingScenePath is null &&
    task6RecoveryLifecycle.RuntimeSessionToken is null &&
    task6RecoveryLifecycle.RunId is null &&
    task6RecoveryLifecycle.ArchiveReason is null &&
    task6RecoveryEvents.Pending is null &&
    task6RecoveryLifecycle.LastActionableError is null &&
    task6RecoveryLifecycle.LastDiagnostic.Contains( "Leave", StringComparison.OrdinalIgnoreCase ) &&
    task6RecoveryLifecycle.LastDiagnostic.Contains( "recovery", StringComparison.OrdinalIgnoreCase ) &&
    task6RecoveryLifecycle.LastDiagnostic.Contains( "must", StringComparison.OrdinalIgnoreCase ) &&
    task6RecoveryLifecycle.LastDiagnostic.Contains( "before", StringComparison.OrdinalIgnoreCase ) &&
    task6RecoveryLifecycle.LastDiagnostic.Length <= 512;
var task6RecoveryEventsAfterReset = new PlayModeLifecycleEventState();
var task6RecoveryEventsAfterResetStart = task6RecoveryEventsAfterReset.RecordStartIntent(
    task6RecoveryLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "new-run-after-hotload-reset" );
var task6RecoveryEventsAfterResetAttach = task6RecoveryEventsAfterReset.AttachRuntime(
    task6RecoveryLifecycle,
    task6RuntimeDescriptor,
    "new-run-after-hotload-reset" );
Assert( task6RecoveryStart.Accepted && task6RecoveryActiveAttach.Accepted && task6RecoveryBlock.Accepted && task6RecoveryArchive.Accepted && task6RecoveryIdentityCleared,
    "accepted blocked hotload reset atomically clears stale runtime and event identity while preserving explicit opt-in and source state" );
Assert( task6RecoveryEventsAfterResetStart.Accepted && task6RecoveryEventsAfterResetAttach.Accepted &&
    task6RecoveryLifecycle.State == PlayModeLifecycleState.RuntimeActive &&
    task6RecoveryLifecycle.RunId == "new-run-after-hotload-reset" && task6RecoveryEventsAfterReset.Pending is null,
    "a new lifecycle run starts after explicit blocked hotload recovery reset" );
var task6RejectedResetEvents = new PlayModeLifecycleEventState();
var task6RejectedEventLifecycle = new PlayModeLifecycle( true, "source-state-event-reset" );
var task6RejectedEventStart = task6RejectedResetEvents.RecordStartIntent(
    task6RejectedEventLifecycle,
    task6Source,
    "scenes/runtime.scene",
    "event-run-before-reset" );
var task6RejectedEventReset = task6RejectedResetEvents.RecoverFromHotload( task6RejectedEventLifecycle );
Assert( task6RejectedEventStart.Accepted && !task6RejectedEventReset.Accepted &&
    task6RejectedResetEvents.Pending is { RuntimeRunId: "event-run-before-reset" },
    "event-state hotload recovery preserves its pending candidate when lifecycle reset is rejected" );
var task6NoAutoStart = new PlayModeLifecycle( false, "source-state-v1" );
var task6NoAutoStartIntent = task6NoAutoStart.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6NoAutoAttach = task6NoAutoStart.AttachRuntime( task6RuntimeDescriptor, null, task6Source, "scenes/runtime.scene" );
Assert( task6NoAutoStartIntent.Accepted && task6NoAutoAttach.Accepted &&
    task6NoAutoStart.State == PlayModeLifecycleState.RuntimeDisabled,
    "scene start and play events never auto-enable runtime collaboration" );
var task6OptedIn = new PlayModeLifecycle( true, "source-state-v2" );
var task6OptInIntent = task6OptedIn.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6MissingRunLifecycle = new PlayModeLifecycle( true, "source-state-v2" );
var task6MissingRunStart = task6MissingRunLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6MissingRunResult = task6MissingRunLifecycle.AttachRuntime( task6RuntimeDescriptor, " ", task6Source, "scenes/runtime.scene" );
var task6DisabledInvalidRunLifecycle = new PlayModeLifecycle( false, "source-state-disabled-invalid-run" );
var task6DisabledInvalidRunStart = task6DisabledInvalidRunLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6DisabledInvalidRunResult = task6DisabledInvalidRunLifecycle.AttachRuntime( task6RuntimeDescriptor, " padded-run ", task6Source, "scenes/runtime.scene" );
Assert( task6MissingRunStart.Accepted && !task6MissingRunResult.Accepted &&
    task6MissingRunLifecycle.State == PlayModeLifecycleState.RuntimeStarting &&
    task6MissingRunLifecycle.PendingScenePath == "scenes/runtime.scene" &&
    task6MissingRunLifecycle.RuntimeSessionToken is null && task6MissingRunLifecycle.RunId is null &&
    ReferenceEquals( task6MissingRunLifecycle.SourceSessionToken, task6Source ),
    "a blank opted-in runtime run ID is rejected before runtime token, pending path, state, or prior run identity change" );
Assert( task6DisabledInvalidRunStart.Accepted && !task6DisabledInvalidRunResult.Accepted &&
    task6DisabledInvalidRunLifecycle.State == PlayModeLifecycleState.RuntimeStarting &&
    task6DisabledInvalidRunLifecycle.RuntimeSessionToken is null && task6DisabledInvalidRunLifecycle.RunId is null,
    "the lifecycle rejects malformed run IDs even when runtime collaboration is opted out" );
var task6OptInRunId = Guid.NewGuid().ToString( "N" );
var task6Attached = task6OptedIn.AttachRuntime( task6RuntimeDescriptor, task6OptInRunId, task6Source, "scenes/runtime.scene" );
Assert( task6OptInIntent.Accepted && task6Attached.Accepted &&
    task6OptedIn.State == PlayModeLifecycleState.RuntimeActive &&
    task6OptedIn.RunId == task6OptInRunId &&
    task6OptedIn.SourceStateToken == "source-state-v2",
    "matching opt-in GameEditorSession descriptor attaches and reaches active runtime state" );
var task6SecondStart = task6OptedIn.RecordStartIntent( "scenes/other.scene", new object(), "scenes/other.scene" );
Assert( !task6SecondStart.Accepted && task6SecondStart.ActionableError.Contains( "already", StringComparison.OrdinalIgnoreCase ) &&
    task6OptedIn.State == PlayModeLifecycleState.RuntimeActive &&
    task6OptedIn.RunId == task6OptInRunId && task6OptedIn.SourceStateToken == "source-state-v2",
    "second concurrent runtime start is rejected without replacing prior state" );
var task6WrongParent = new PlayModeLifecycle( true, "source-state-v2" );
var task6WrongParentStart = task6WrongParent.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6WrongParentResult = task6WrongParent.AttachRuntime( task6RuntimeDescriptor with { ParentSessionToken = new object() }, Guid.NewGuid().ToString(), task6Source, "scenes/runtime.scene" );
Assert( task6WrongParentStart.Accepted && !task6WrongParentResult.Accepted && task6WrongParentResult.ActionableError.Contains( "parent", StringComparison.OrdinalIgnoreCase ) &&
    task6WrongParent.State == PlayModeLifecycleState.RuntimeStarting && task6WrongParent.SourceStateToken == "source-state-v2",
    "runtime descriptor with wrong parent is rejected visibly and preserves prior transition state" );
var task6WrongPath = new PlayModeLifecycle( true, "source-state-v2" );
var task6WrongPathStart = task6WrongPath.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6WrongPathResult = task6WrongPath.AttachRuntime( task6RuntimeDescriptor with { SourceScenePath = "scenes/wrong.scene" }, Guid.NewGuid().ToString(), task6Source, "scenes/runtime.scene" );
Assert( task6WrongPathStart.Accepted && !task6WrongPathResult.Accepted && task6WrongPathResult.ActionableError.Contains( "path", StringComparison.OrdinalIgnoreCase ) &&
    task6WrongPath.State == PlayModeLifecycleState.RuntimeStarting && task6WrongPath.SourceStateToken == "source-state-v2",
    "runtime descriptor with wrong source path is rejected visibly and preserves prior transition state" );
var task6Stop = task6OptedIn.RecordStop();
Assert( task6Stop.Accepted && task6OptedIn.State == PlayModeLifecycleState.Source &&
    task6OptedIn.SourceStateToken == "source-state-v2" && task6OptedIn.RunId is null &&
    !string.IsNullOrWhiteSpace( task6OptedIn.LastDiagnostic ) && task6OptedIn.LastDiagnostic.Length <= 512,
    "runtime stop returns to source mode without discarding source state and records a bounded final diagnostic" );
var task6HotloadTransition = task6OptedIn.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6Hotload = task6OptedIn.BlockForHotload( "hotloaded" );
Assert( task6HotloadTransition.Accepted && task6Hotload.Accepted && task6Hotload.ArchiveMarker &&
    task6OptedIn.State == PlayModeLifecycleState.RuntimeBlocked &&
    task6OptedIn.ArchiveReason == "hotloaded" &&
    task6OptedIn.SourceStateToken == "source-state-v2",
    "hotload blocks an in-progress transition with an archive marker and preserves source state" );
var task6PostHotloadStart = task6OptedIn.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
Assert( !task6PostHotloadStart.Accepted && !string.IsNullOrWhiteSpace( task6PostHotloadStart.ActionableError ) &&
    task6OptedIn.State == PlayModeLifecycleState.RuntimeBlocked,
    "blocked lifecycle rejects a new transition with an actionable error instead of looping" );
var task6InvalidAttach = new PlayModeLifecycle( true, "source-state-v3" );
var task6InvalidAttachStart = task6InvalidAttach.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6InvalidAttachResult = task6InvalidAttach.AttachRuntime( new RuntimeSessionDescriptor
{
    Kind = RuntimeSessionKind.Source,
    SessionToken = new object(),
    IsValid = true,
    IsDestroyed = false,
    SourceScenePath = "scenes/runtime.scene"
}, Guid.NewGuid().ToString(), task6Source, "scenes/runtime.scene" );
Assert( task6InvalidAttachStart.Accepted && !task6InvalidAttachResult.Accepted && !string.IsNullOrWhiteSpace( task6InvalidAttachResult.ActionableError ) &&
    task6InvalidAttach.State == PlayModeLifecycleState.RuntimeStarting && task6InvalidAttach.SourceStateToken == "source-state-v3",
    "invalid runtime attach is an actionable rejected transition that preserves the prior starting state" );
var task6AfterStop = new PlayModeLifecycle( true, "source-state-v1" );
var task6AfterStopInitial = task6AfterStop.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6AfterStopInitialAttach = task6AfterStop.AttachRuntime( task6RuntimeDescriptor, Guid.NewGuid().ToString(), task6Source, "scenes/runtime.scene" );
var task6AfterStopInitialStop = task6AfterStop.RecordStop();
var task6AfterStopStart = task6AfterStop.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6AfterStopAttach = task6AfterStop.AttachRuntime( task6RuntimeDescriptor, Guid.NewGuid().ToString(), task6Source, "scenes/runtime.scene" );
Assert( task6AfterStopInitial.Accepted && task6AfterStopInitialAttach.Accepted && task6AfterStopInitialStop.Accepted &&
    task6AfterStopStart.Accepted && task6AfterStopAttach.Accepted && task6AfterStop.State == PlayModeLifecycleState.RuntimeActive,
    "a stopped runtime run may start again with a new run identity" );
var task6LifecycleSurface = typeof( PlayModeLifecycle ).GetMethods( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly )
    .Select( method => method.Name )
    .ToArray();
Assert( !task6LifecycleSurface.Any( method => method is "Save" or "Reload" or "Apply" or "Undo" or "Dirty" or "SetHasUnsavedChanges" ),
    "the production lifecycle coordinator exposes no source-scene mutation operation" );
Assert( !Enum.GetNames<PlayModeLifecycleState>().Contains( "RuntimeStopping" ) &&
    !task6LifecycleSurface.Any( method => method is "SetRuntimeStopping" ),
    "the production lifecycle coordinator exposes no dead RuntimeStopping state or transition API" );

// Task 6 production-wiring regression: the shared transaction seam must
// perform the post-validation preparation callback before it commits the
// coordinator's start bookkeeping. This is deliberately coupled to a throw so
// the old commit-before-preparation implementation is behaviorally rejected.
var task6ProductionStartTransaction = new ConnectionTeardownCoordinator();
task6ProductionStartTransaction.DeferHotloadRecovery();
task6ProductionStartTransaction.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ProductionStartPrepared = false;
var task6ProductionStartResult = task6ProductionStartTransaction.BeginStart(
    new SessionStartCommitRequest(
        task6ProductionStartTransaction.ValidateStart( new SessionStartGuardRequest(
            PlayModeTransitionResult.Ok(),
            ConnectionBusy: false,
            ConnectionTeardownPending: false,
            ConnectionTeardownFailed: false,
            TerminalTeardownDiagnostic: null ) ),
        () => { task6ProductionStartPrepared = true; throw new IOException( "preparation failed" ); } ) );
Assert( !task6ProductionStartResult.Accepted && task6ProductionStartPrepared &&
    task6ProductionStartResult.ActionableError?.Contains( "preparation failed", StringComparison.Ordinal ) == true &&
    task6ProductionStartTransaction.LeaveRequested && task6ProductionStartTransaction.DeferredHotloadRecovery &&
    !task6ProductionStartTransaction.TeardownCompleted,
    "a production start transaction preserves leave/recovery state when preparation throws after validation" );
var task6ProductionReconnectTransaction = new ConnectionTeardownCoordinator();
task6ProductionReconnectTransaction.DeferHotloadRecovery();
task6ProductionReconnectTransaction.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ProductionReconnectPrepared = false;
var task6ProductionReconnectResult = task6ProductionReconnectTransaction.BeginReconnect( new SessionReconnectCommitRequest(
    task6ProductionReconnectTransaction.ValidateReconnect( new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Disconnected ) ),
    () => { task6ProductionReconnectPrepared = true; throw new IOException( "reconnect preparation failed" ); } ) );
Assert( !task6ProductionReconnectResult.Accepted && task6ProductionReconnectPrepared &&
    task6ProductionReconnectResult.Stage == ReconnectCommitStage.FailedDuringPrepare &&
    task6ProductionReconnectResult.PreviousResourcesDisposed &&
    task6ProductionReconnectResult.ActionableError?.Contains( "reconnect preparation failed", StringComparison.Ordinal ) == true &&
    task6ProductionReconnectResult.ActionableError?.Contains( "no collaboration bookkeeping was committed", StringComparison.Ordinal ) != true &&
    task6ProductionReconnectTransaction.LeaveRequested && task6ProductionReconnectTransaction.DeferredHotloadRecovery,
    "a production reconnect transaction preserves leave/recovery state when preparation throws after validation" );
var task6ProductionReconnectRejected = new ConnectionTeardownCoordinator();
task6ProductionReconnectRejected.DeferHotloadRecovery();
task6ProductionReconnectRejected.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ProductionReconnectRejectedResult = task6ProductionReconnectRejected.BeginReconnect( new SessionReconnectCommitRequest(
    task6ProductionReconnectRejected.ValidateReconnect( new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Disconnected ) ),
    () => false ) );
Assert( !task6ProductionReconnectRejectedResult.Accepted &&
    task6ProductionReconnectRejectedResult.Stage == ReconnectCommitStage.FailedDuringPrepare &&
    task6ProductionReconnectRejectedResult.ActionableError?.Contains( "already closed", StringComparison.Ordinal ) == true &&
    task6ProductionReconnectRejected.LeaveRequested && task6ProductionReconnectRejected.DeferredHotloadRecovery,
    "a failed reconnect preparation is rejected before clearing leave and deferred recovery state" );

// Task 6 remediation BLOCKER 3: a reconnect preparation that already closed the
// previous transport must never be reported as "no collaboration bookkeeping was
// committed" and must never be swallowed without a user-visible failure. The
// outcome distinguishes a pre-commit rejection from a post-prepare failure.
var task6ReconnectGuardRejectedPrepare = new ConnectionTeardownCoordinator();
task6ReconnectGuardRejectedPrepare.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ReconnectPrepareRan = false;
var task6ReconnectGuardRejected = task6ReconnectGuardRejectedPrepare.BeginReconnect( new SessionReconnectCommitRequest(
    // A guard rejection: no established session, so preparation must never run.
    task6ReconnectGuardRejectedPrepare.ValidateReconnect( new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: false,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Disconnected ) ),
    () => { task6ReconnectPrepareRan = true; return true; } ) );
Assert( !task6ReconnectGuardRejected.Accepted &&
    task6ReconnectGuardRejected.Stage == ReconnectCommitStage.RejectedBeforePrepare &&
    !task6ReconnectGuardRejected.PreviousResourcesDisposed &&
    !task6ReconnectPrepareRan,
    "a pre-commit reconnect rejection never runs preparation and never claims a closed connection" );
var task6ReconnectPrepareFalseCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectPrepareFalse = task6ReconnectPrepareFalseCoordinator.BeginReconnect( new SessionReconnectCommitRequest(
    task6ReconnectPrepareFalseCoordinator.ValidateReconnect( new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Disconnected ) ),
    () => false ) );
Assert( !task6ReconnectPrepareFalse.Accepted &&
    task6ReconnectPrepareFalse.Stage == ReconnectCommitStage.FailedDuringPrepare &&
    task6ReconnectPrepareFalse.PreviousResourcesDisposed &&
    !string.IsNullOrWhiteSpace( task6ReconnectPrepareFalse.ActionableError ) &&
    task6ReconnectPrepareFalse.ActionableError?.Contains( "no collaboration bookkeeping was committed", StringComparison.Ordinal ) != true &&
    task6ReconnectPrepareFalse.ActionableError?.Contains( "already closed", StringComparison.Ordinal ) == true,
    "a reconnect preparation returning false reports a post-prepare failure that a caller can surface" );
var task6ReconnectPrepareThrowCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectPrepareThrow = task6ReconnectPrepareThrowCoordinator.BeginReconnect( new SessionReconnectCommitRequest(
    task6ReconnectPrepareThrowCoordinator.ValidateReconnect( new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Disconnected ) ),
    () => throw new IOException( new string( 'r', 900 ) ) ) );
Assert( !task6ReconnectPrepareThrow.Accepted &&
    task6ReconnectPrepareThrow.Stage == ReconnectCommitStage.FailedDuringPrepare &&
    task6ReconnectPrepareThrow.PreviousResourcesDisposed &&
    task6ReconnectPrepareThrow.ActionableError?.Contains( "no collaboration bookkeeping was committed", StringComparison.Ordinal ) != true &&
    task6ReconnectPrepareThrow.ActionableError?.Length <= CollaborationDiagnostics.MaxDiagnosticLength,
    "a throwing reconnect preparation is a bounded post-prepare failure, never an escaped exception" );
var task6ReconnectCommittedCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectCommittedCoordinator.DeferHotloadRecovery();
task6ReconnectCommittedCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ReconnectCommitted = task6ReconnectCommittedCoordinator.BeginReconnect( new SessionReconnectCommitRequest(
    task6ReconnectCommittedCoordinator.ValidateReconnect( new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Disconnected ) ),
    () => true ) );
Assert( task6ReconnectCommitted.Accepted &&
    task6ReconnectCommitted.Stage == ReconnectCommitStage.Committed &&
    task6ReconnectCommitted.ActionableError is null &&
    !task6ReconnectCommittedCoordinator.LeaveRequested,
    "an accepted reconnect commits and clears leave authorization at the shared seam" );

// Final cleanup: two refusals the transport state cannot express. After a Leave nothing is
// attached, nothing is pending and the failure — when there is one — is the old session's,
// so this looks exactly like a connect that never attached; a host looks like any other
// failed session while its own listener is still bound. Both therefore arrive as named
// facts and are refused BEFORE the transport-state check, which would otherwise answer a
// question the user never asked.
var cleanupSessionLeftGuard = new ConnectionTeardownCoordinator().ValidateReconnect(
    new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Failed,
        SessionLeft: true ) );
Assert( !cleanupSessionLeftGuard.Accepted &&
    cleanupSessionLeftGuard.ActionableError == "No session to recover — start or join one.",
    "a reconnect after Leave is refused with an actionable message even when the transport still looks recoverable" );
var cleanupHostedGuard = new ConnectionTeardownCoordinator().ValidateReconnect(
    new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Failed,
        HostedSession: true ) );
Assert( !cleanupHostedGuard.Accepted &&
    cleanupHostedGuard.ActionableError == "A hosted session cannot be recovered in place; leave and host the session again.",
    "a reconnect of a hosted session is refused with an actionable message instead of dialing the listener it just closed" );
var cleanupGuardControl = new ConnectionTeardownCoordinator().ValidateReconnect(
    new SessionReconnectGuardRequest(
        task6ApprovedCoexistenceLifecycle,
        SessionEstablished: true,
        ConnectionAttemptPending: false,
        ConnectionBusy: false,
        TransportState: CollaborationTransportState.Failed ) );
Assert( cleanupGuardControl.Accepted &&
    cleanupGuardControl.ActionableError is null,
    "the same failed session with neither a Leave nor a host behind it is still accepted for reconnect" );
Assert( CollaborationDiagnostics.Bounded( cleanupHostedGuard.ActionableError ) == cleanupHostedGuard.ActionableError &&
    CollaborationDiagnostics.Bounded( cleanupSessionLeftGuard.ActionableError ) == cleanupSessionLeftGuard.ActionableError,
    "both new refusals are stored bounded, as every other guard refusal is" );

// Task 6 remediation BLOCKER 1: transport classification must rank a recorded
// failure above "not attached". A faulted connect leaves attached=false with
// failed=true, and Detect must still report Failed or Reconnect is rejected
// permanently. This drives the exact Detect production calls.
var task6TransportTable = new List<( bool Attached, bool Live, bool Failed, bool Connecting, CollaborationTransportState Expected )>();
foreach ( var task6AttachedBit in new[] { false, true } )
foreach ( var task6LiveBit in new[] { false, true } )
foreach ( var task6FailedBit in new[] { false, true } )
foreach ( var task6ConnectingBit in new[] { false, true } )
    task6TransportTable.Add( ( task6AttachedBit, task6LiveBit, task6FailedBit, task6ConnectingBit,
        CollaborationTransportStateRules.Detect( task6AttachedBit, task6LiveBit, task6FailedBit, task6ConnectingBit ) ) );
var task6TransportFaultedConnect = task6TransportTable.First( row => !row.Attached && row.Failed && !row.Connecting && !row.Live );
Assert( task6TransportFaultedConnect.Expected == CollaborationTransportState.Failed,
    "a faulted connect with no attached transport and no live session classifies as Failed, not None" );
var task6TransportFaultedStaleLive = task6TransportTable.First( row => !row.Attached && row.Failed && row.Live && !row.Connecting );
Assert( task6TransportFaultedStaleLive.Expected == CollaborationTransportState.Failed,
    "a recorded failure outranks a stale live flag when no transport is attached" );
var task6TransportExpected = new Dictionary<( bool, bool, bool, bool ), CollaborationTransportState>
{
    [(false, false, false, false)] = CollaborationTransportState.None,
    [(false, false, true, false)] = CollaborationTransportState.Failed,
    [(false, true, false, false)] = CollaborationTransportState.None,
    [(false, true, true, false)] = CollaborationTransportState.Failed,
    [(true, false, false, false)] = CollaborationTransportState.Disconnected,
    [(true, false, true, false)] = CollaborationTransportState.Failed,
    [(true, true, false, false)] = CollaborationTransportState.Live,
    [(true, true, true, false)] = CollaborationTransportState.Failed,
    [(false, false, false, true)] = CollaborationTransportState.Connecting,
    [(false, false, true, true)] = CollaborationTransportState.Connecting,
    [(false, true, false, true)] = CollaborationTransportState.Connecting,
    [(false, true, true, true)] = CollaborationTransportState.Connecting,
    [(true, false, false, true)] = CollaborationTransportState.Connecting,
    [(true, false, true, true)] = CollaborationTransportState.Connecting,
    [(true, true, false, true)] = CollaborationTransportState.Connecting,
    [(true, true, true, true)] = CollaborationTransportState.Connecting
};
var task6TransportMisclassified = string.Join( " ", task6TransportTable.Where( row =>
    !task6TransportExpected.TryGetValue( (row.Attached, row.Live, row.Failed, row.Connecting), out var expected ) ||
    expected != row.Expected ||
    expected != CollaborationTransportStateRules.Detect( row.Attached, row.Live, row.Failed, row.Connecting ) )
    .Select( row => $"{row.Attached}{row.Live}{row.Failed}{row.Connecting}->{row.Expected}" ) );
Assert( task6TransportTable.Count == 16 &&
    task6TransportTable.All( row => task6TransportExpected.TryGetValue( (row.Attached, row.Live, row.Failed, row.Connecting), out var expected ) && expected == row.Expected ) &&
    task6TransportMisclassified.Length == 0,
    "the production transport classifier ranks failed above unattached across the full attached x live x failed x connecting space"
        + ( task6TransportMisclassified.Length == 0 ? "" : " [misclassified: " + task6TransportMisclassified + "]" ) );
// The faulted-connect classification must actually admit Reconnect through the
// same guard production calls, otherwise the user can never recover.
var task6TransportFaultedReconnectCoordinator = new ConnectionTeardownCoordinator();
task6TransportFaultedReconnectCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6TransportFaultedReconnect = task6TransportFaultedReconnectCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: task6TransportFaultedConnect.Connecting,
    ConnectionBusy: task6TransportFaultedConnect.Attached,
    TransportState: task6TransportFaultedConnect.Expected ) );
Assert( task6TransportFaultedReconnect.Accepted && !task6TransportFaultedReconnectCoordinator.LeaveRequested,
    "Reconnect is admitted for a faulted connect, and the accepted commit clears prior leave authorization" );

// Task 6 remediation BLOCKER 2: a hotload while connected outside play mode
// refuses BlockForHotload (no runtime transition exists) and writes no archive,
// yet the archive helper still answers "archived" for that nothing-to-do case.
// This drives the seam with the value PRODUCTION actually passes here, not a
// hand-built false: the seam must derive the claim from lifecycle state, so a
// non-blocked caller can never make it claim an archive or promise a recovery
// that Leave will not finish.
var task6HotloadSourceLifecycle = new PlayModeLifecycle( false, "source-state-hotload" );
var task6HotloadSourceBlock = task6HotloadSourceLifecycle.BlockForHotload( "hotloaded" );
var task6HotloadSourceReport = HotloadCodeChangeRules.Decide( new HotloadCodeChangeRequest(
    task6HotloadSourceLifecycle.State,
    LifecycleBlockAccepted: task6HotloadSourceBlock.Accepted,
    ArchiveWritten: true,
    TransportActive: true,
    LifecycleRejectionReason: task6HotloadSourceBlock.ActionableError ) );
Assert( !task6HotloadSourceBlock.Accepted &&
    task6HotloadSourceLifecycle.State == PlayModeLifecycleState.Source &&
    task6HotloadSourceLifecycle.ArchiveReason is null &&
    !task6HotloadSourceReport.ArchiveClaimed,
    "a hotload with no runtime transition to block claims no recovery archive" );
Assert( !task6HotloadSourceReport.RequiresHotloadRecoveryAfterLeave &&
    task6HotloadSourceReport.TeardownTransport &&
    !task6HotloadSourceReport.Message.Contains( "Leave will finish transport teardown", StringComparison.Ordinal ) &&
    !task6HotloadSourceReport.Message.Contains( "archive failed", StringComparison.OrdinalIgnoreCase ) &&
    task6HotloadSourceReport.LifecycleRejectionReason ==
        task6HotloadSourceLifecycle.LastActionableError &&
    !string.IsNullOrWhiteSpace( task6HotloadSourceReport.LifecycleRejectionReason ),
    "an unblocked hotload never claims an archive or a recovery Leave would finish, and the refusal reason stays visible" );
// The real blocked path still writes an archive and still defers recovery.
var task6HotloadBlockedLifecycle = new PlayModeLifecycle( true, "source-state-hotload-blocked" );
task6HotloadBlockedLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
var task6HotloadBlockedBlock = task6HotloadBlockedLifecycle.BlockForHotload( "hotloaded" );
var task6HotloadBlockedReport = HotloadCodeChangeRules.Decide( new HotloadCodeChangeRequest(
    task6HotloadBlockedLifecycle.State,
    LifecycleBlockAccepted: task6HotloadBlockedBlock.Accepted,
    ArchiveWritten: true,
    TransportActive: true,
    LifecycleRejectionReason: null ) );
Assert( task6HotloadBlockedBlock.Accepted && task6HotloadBlockedReport.ArchiveClaimed &&
    task6HotloadBlockedReport.RequiresHotloadRecoveryAfterLeave &&
    task6HotloadBlockedReport.Message == HotloadCodeChangeRules.ArchivedMessage,
    "a genuinely blocked hotload keeps the truthful archived message and deferred recovery" );
// A refused archive in a genuinely blocked lifecycle must never be reported as archived.
var task6HotloadArchiveFailed = HotloadCodeChangeRules.Decide( new HotloadCodeChangeRequest(
    PlayModeLifecycleState.RuntimeBlocked,
    LifecycleBlockAccepted: true,
    ArchiveWritten: false,
    TransportActive: true ) );
Assert( !task6HotloadArchiveFailed.ArchiveClaimed &&
    !task6HotloadArchiveFailed.RequiresHotloadRecoveryAfterLeave &&
    task6HotloadArchiveFailed.Message == HotloadCodeChangeRules.ArchiveFailedMessage,
    "a failed archive in a blocked lifecycle reports the failure, never a completed archive" );
// A repeat hotload while ALREADY RuntimeBlocked after a terminal teardown failure
// has its block refused (HasTerminalTeardownFailure) yet the runtime is still
// blocked and no archive was written: the truth is "the archive failed", never
// "no archive was required". The message must be selected on blocked alone.
var task6HotloadRepeatFailureLifecycle = new PlayModeLifecycle( true, "source-state-hotload-repeat-failure" );
task6HotloadRepeatFailureLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6HotloadRepeatFailureLifecycle.BlockForHotload( "hotloaded" );
task6HotloadRepeatFailureLifecycle.RecordHotloadTeardownFailure( "transport dispose fault" );
var task6HotloadRepeatFailureBlock = task6HotloadRepeatFailureLifecycle.BlockForHotload( "hotloaded" );
var task6HotloadRepeatFailureReport = HotloadCodeChangeRules.Decide( new HotloadCodeChangeRequest(
    task6HotloadRepeatFailureLifecycle.State,
    LifecycleBlockAccepted: task6HotloadRepeatFailureBlock.Accepted,
    ArchiveWritten: false,
    TransportActive: true,
    LifecycleRejectionReason: task6HotloadRepeatFailureBlock.ActionableError ) );
Assert( !task6HotloadRepeatFailureBlock.Accepted &&
    task6HotloadRepeatFailureLifecycle.State == PlayModeLifecycleState.RuntimeBlocked &&
    !task6HotloadRepeatFailureReport.ArchiveClaimed &&
    task6HotloadRepeatFailureReport.Message == HotloadCodeChangeRules.ArchiveFailedMessage,
    "a repeat hotload blocked by a terminal teardown failure reports the archive failure, not that no archive was required" );
// The full blocked x archived matrix: an archive is claimable ONLY in the
// blocked-and-actually-written case, whatever the caller claims and whether or
// not the repeat block was accepted.
var task6HotloadClaimMatrix = new[]
{
    (Label: "blocked+archived", Blocked: true, Written: true, BlockAccepted: true, Transport: true, Claimed: true, Failed: true),
    (Label: "blocked+archived,block refused", Blocked: true, Written: true, BlockAccepted: false, Transport: true, Claimed: true, Failed: true),
    (Label: "blocked+not archived,block accepted", Blocked: true, Written: false, BlockAccepted: true, Transport: true, Claimed: false, Failed: false),
    (Label: "blocked+not archived,block refused", Blocked: true, Written: false, BlockAccepted: false, Transport: true, Claimed: false, Failed: false),
    (Label: "unblocked+transport active", Blocked: false, Written: true, BlockAccepted: false, Transport: true, Claimed: false, Failed: false),
    (Label: "unblocked+no transport", Blocked: false, Written: false, BlockAccepted: false, Transport: false, Claimed: false, Failed: false)
};
var task6HotloadMatrixRows = task6HotloadClaimMatrix.Select( row => HotloadCodeChangeRules.Decide( new HotloadCodeChangeRequest(
    row.Blocked ? PlayModeLifecycleState.RuntimeBlocked : PlayModeLifecycleState.Source,
    LifecycleBlockAccepted: row.BlockAccepted,
    ArchiveWritten: row.Written,
    TransportActive: row.Transport ) ) ).ToArray();
var task6HotloadMatrixBroken = string.Join( " ", task6HotloadClaimMatrix.Zip( task6HotloadMatrixRows, ( row, report ) =>
    (report.ArchiveClaimed == row.Claimed && report.RequiresHotloadRecoveryAfterLeave == row.Failed &&
     report.Message == ( row.Claimed
        ? HotloadCodeChangeRules.ArchivedMessage
        : row.Blocked
            ? HotloadCodeChangeRules.ArchiveFailedMessage
            : HotloadCodeChangeRules.NoArchiveNeededMessage ) )
        ? null : row.Label ).Where( label => label is not null ) );
Assert( task6HotloadMatrixRows.All( report => !report.ArchiveClaimed || report.Message.Contains( "Leave will finish transport teardown", StringComparison.Ordinal ) == report.RequiresHotloadRecoveryAfterLeave ) &&
    task6HotloadMatrixBroken.Length == 0,
    "an archive is claimed only when blocked and actually written, and an archive-failure claim is only ever made while blocked"
        + ( task6HotloadMatrixBroken.Length == 0 ? "" : " [broken: " + task6HotloadMatrixBroken + "]" ) );
// The seam's surface-error contract: a hotload with a live transport always
// surfaces an error, and one with no transport never claims a collaboration was
// stopped. This pins the shared decision seam only. CodeChanged itself lives in
// Editor/ and depends on s&box engine types, so it cannot be exercised from this
// engine-independent project, and EditorProbe.cs carries no hotload command, so
// no live editor run covers it either. The compile gate is the only static proof
// it still builds.
var task6HotloadSurfacePairs = task6HotloadClaimMatrix.Zip( task6HotloadMatrixRows,
    ( row, report ) => ( Transport: row.Transport, report.SurfaceError ) ).ToArray();
Assert( task6HotloadSurfacePairs.Where( pair => pair.Transport ).All( pair => pair.SurfaceError ) &&
    task6HotloadSurfacePairs.Where( pair => !pair.Transport ).All( pair => !pair.SurfaceError ),
    "a hotload with a live transport surfaces an error, and one with no transport never claims a collaboration was stopped" );
var task6HotloadMessageLengths = new[]
{
    task6HotloadSourceReport.Message.Length,
    task6HotloadBlockedReport.Message.Length,
    task6HotloadArchiveFailed.Message.Length,
    ( task6HotloadSourceReport.LifecycleRejectionReason ?? string.Empty ).Length
};
Assert( task6HotloadMessageLengths.All( length => length <= CollaborationDiagnostics.MaxDiagnosticLength ),
    "every hotload report field is bounded at the shared diagnostic boundary" );
// Production wiring: CodeChanged is the only place that decides what a hotload
// claims, and it must reach that decision through this single entry point.
// (CollaborationSession itself cannot be constructed in a core test; the
// editor build gate proves the editor handler still compiles against it.)
var task6HotloadSeamEntry = typeof( HotloadCodeChangeRules ).GetMethods( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly )
    .Where( method => method.ReturnType == typeof( HotloadCodeChangeReport ) )
    .Select( method => method.GetParameters().Select( parameter => parameter.ParameterType ).ToArray() )
    .ToArray();
Assert( task6HotloadSeamEntry.Length == 1 &&
    task6HotloadSeamEntry[0].SequenceEqual( new[] { typeof( HotloadCodeChangeRequest ) } ),
    "the hotload decision has exactly one shared entry point taking one guard request" );

// Task 6 remediation: Start/Reconnect must run every guard before any bookkeeping
// mutation, and must reject while a connection teardown is pending or terminal.
var task6StartBlockedLifecycle = new PlayModeLifecycle( true, "source-state-start-blocked" );
task6StartBlockedLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6StartBlockedLifecycle.BlockForHotload( "start-blocked" );
var task6StartBlockedValidation = task6StartBlockedLifecycle.ValidateSessionStart();
var task6StartGuardRejectedLifecycle = new PlayModeLifecycle( true, "source-state-start-rejected" );
var task6StartTerminalCoordinator = new ConnectionTeardownCoordinator();
var task6StartPendingCoordinator = new ConnectionTeardownCoordinator();
var task6StartFailedCoordinator = new ConnectionTeardownCoordinator();
// Every rejected Start must preserve this exact prior bookkeeping snapshot.
foreach ( var seeded in new[]
{
    task6StartPendingCoordinator,
    task6StartFailedCoordinator
} )
{
    seeded.DeferHotloadRecovery();
    seeded.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
}
task6StartTerminalCoordinator.RecordTerminalTeardownFailure( "terminal start teardown failure" );
var task6StartPendingRejected = task6StartPendingCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(),
    ConnectionBusy: false,
    ConnectionTeardownPending: true,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
var task6StartFailedRejected = task6StartFailedCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(),
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: true,
    TerminalTeardownDiagnostic: null ) );
var task6StartTerminalRejected = task6StartTerminalCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(),
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: "terminal start teardown failure" ) );
var task6StartBusyCoordinator = new ConnectionTeardownCoordinator();
task6StartBusyCoordinator.DeferHotloadRecovery();
task6StartBusyCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6StartBusyRejected = task6StartBusyCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(),
    ConnectionBusy: true,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
var task6StartRejectionTexts = new[]
{
    task6StartPendingRejected.ActionableError,
    task6StartFailedRejected.ActionableError,
    task6StartTerminalRejected.ActionableError,
    task6StartBusyRejected.ActionableError
};
Assert( task6StartRejectionTexts.All( text => !string.IsNullOrWhiteSpace( text ) && text.Length <= 512 ),
    "each Start rejection surfaces a bounded actionable diagnostic" );
Assert( !task6StartPendingRejected.Accepted && !task6StartFailedRejected.Accepted &&
    !task6StartTerminalRejected.Accepted && !task6StartBusyRejected.Accepted &&
    task6StartPendingCoordinator.LeaveRequested && task6StartPendingCoordinator.DeferredHotloadRecovery &&
    task6StartFailedCoordinator.LeaveRequested && task6StartFailedCoordinator.DeferredHotloadRecovery &&
    !task6StartPendingCoordinator.TeardownCompletionPending &&
    !task6StartFailedCoordinator.TeardownCompletionPending &&
    task6StartBusyCoordinator.LeaveRequested && task6StartBusyCoordinator.DeferredHotloadRecovery,
    "a rejected Start preserves prior leave, deferred recovery, and teardown bookkeeping" );
var task6StartBlockedCoordinator = new ConnectionTeardownCoordinator();
var task6StartBlockedRejected = task6StartBlockedCoordinator.TryBeginStart( new SessionStartGuardRequest(
    task6StartBlockedValidation,
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
Assert( !task6StartBlockedValidation.Accepted && !task6StartBlockedRejected.Accepted &&
    !string.IsNullOrWhiteSpace( task6StartBlockedRejected.ActionableError ) &&
    !task6StartBlockedCoordinator.LeaveRequested && !task6StartBlockedCoordinator.DeferredHotloadRecovery,
    "a blocked lifecycle rejects Start with an actionable error and no bookkeeping change" );
var task6StartAcceptedCoordinator = new ConnectionTeardownCoordinator();
task6StartAcceptedCoordinator.DeferHotloadRecovery();
task6StartAcceptedCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6StartAccepted = task6StartAcceptedCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(),
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
Assert( task6StartAccepted.Accepted && task6StartAccepted.ActionableError is null &&
    !task6StartAcceptedCoordinator.LeaveRequested &&
    !task6StartAcceptedCoordinator.DeferredHotloadRecovery &&
    !task6StartAcceptedCoordinator.TeardownCompletionPending,
    "an accepted Start clears prior leave, deferred recovery, and teardown bookkeeping" );
var task6StartGuardParameters = typeof( ConnectionTeardownCoordinator ).GetMethod( nameof( ConnectionTeardownCoordinator.TryBeginStart ) )?
    .GetParameters().Select( parameter => parameter.ParameterType ).ToArray() ?? Array.Empty<Type>();
Assert( task6StartGuardParameters.SequenceEqual( new[] { typeof( SessionStartGuardRequest ) } ),
    "Start is admitted through a single shared engine-independent guard request" );
var task6ReconnectEstablishCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectEstablish = task6ReconnectEstablishCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: false,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
Assert( !task6ReconnectEstablish.Accepted &&
    task6ReconnectEstablish.ActionableError?.Contains( "Join", StringComparison.OrdinalIgnoreCase ) == true &&
    !task6ReconnectEstablishCoordinator.LeaveRequested,
    "Reconnect without an established session is rejected before clearing leave authorization" );
var task6ReconnectEstablishedCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectEstablishedCoordinator.DeferHotloadRecovery();
task6ReconnectEstablishedCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ReconnectEstablished = task6ReconnectEstablishedCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: true,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
var task6ReconnectBusyCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectBusy = task6ReconnectBusyCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: true,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
Assert( !task6ReconnectEstablished.Accepted && !task6ReconnectBusy.Accepted &&
    task6ReconnectEstablishedCoordinator.LeaveRequested &&
    task6ReconnectEstablishedCoordinator.DeferredHotloadRecovery &&
    !task6ReconnectBusyCoordinator.LeaveRequested,
    "a rejected Reconnect preserves leave authorization and deferred hotload recovery" );
var task6ReconnectPendingCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectPendingCoordinator.DeferHotloadRecovery();
task6ReconnectPendingCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ReconnectPending = task6ReconnectPendingCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: true,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
Assert( !task6ReconnectPending.Accepted &&
    task6ReconnectPending.ActionableError?.Contains( "pending", StringComparison.OrdinalIgnoreCase ) == true &&
    task6ReconnectPendingCoordinator.LeaveRequested &&
    task6ReconnectPendingCoordinator.DeferredHotloadRecovery,
    "Reconnect is rejected while a connection teardown is pending and preserves deferred recovery" );
var task6ReconnectFailedCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectFailed = task6ReconnectFailedCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: true,
    TerminalTeardownDiagnostic: null ) );
var task6ReconnectTerminalCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectTerminalCoordinator.RecordTerminalTeardownFailure( "terminal reconnect teardown failure" );
var task6ReconnectTerminal = task6ReconnectTerminalCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: task6ReconnectTerminalCoordinator.TerminalDiagnostic ) );
Assert( !task6ReconnectFailed.Accepted && !task6ReconnectTerminal.Accepted &&
    task6ReconnectFailed.ActionableError?.Contains( "fail-stop", StringComparison.OrdinalIgnoreCase ) == true &&
    task6ReconnectTerminal.ActionableError == task6ReconnectTerminalCoordinator.TerminalDiagnostic &&
    task6ReconnectTerminalCoordinator.TerminalDiagnostic?.Contains( "fail-stop", StringComparison.OrdinalIgnoreCase ) == true,
    "Reconnect is rejected for a pending or terminal connection teardown failure" );
var task6ReconnectStartingCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectStartingCoordinator.DeferHotloadRecovery();
var task6ReconnectBlockedCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectBlockedCoordinator.DeferHotloadRecovery();
var task6ReconnectStarting = task6ReconnectStartingCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6WrongParentCoexistence,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: true,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
var task6ReconnectBlocked = task6ReconnectBlockedCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6BlockedAdmissionLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: true,
    ConnectionBusy: true,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null ) );
Assert( task6WrongParentCoexistence.State == PlayModeLifecycleState.RuntimeStarting &&
    task6BlockedAdmissionLifecycle.State == PlayModeLifecycleState.RuntimeBlocked &&
    !task6ReconnectStarting.Accepted && !task6ReconnectBlocked.Accepted &&
    task6ReconnectStartingCoordinator.DeferredHotloadRecovery &&
    task6ReconnectBlockedCoordinator.DeferredHotloadRecovery,
    "Reconnect rejects starting and blocked lifecycles without clearing deferred hotload recovery" );
// A second connect must never start while a detached connect is still being
// disposed: the guard reads the real gate, not a stale bookkeeping flag.
var task6ReconnectDisposingCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectDisposingCompletion = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
task6ReconnectDisposingCoordinator.BeginDetachedTeardown( task6ReconnectDisposingCompletion.Task, recoverWhenComplete: true );
var task6ReconnectDisposing = task6ReconnectDisposingCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: task6ReconnectDisposingCoordinator.IsPending,
    ConnectionTeardownFailed: task6ReconnectDisposingCoordinator.HasTerminalFailure,
    TerminalTeardownDiagnostic: task6ReconnectDisposingCoordinator.TerminalDiagnostic ) );
var task6ReconnectDisposingSecondCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectDisposingSecondCompletion = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
task6ReconnectDisposingSecondCoordinator.BeginDetachedTeardown( task6ReconnectDisposingSecondCompletion.Task, recoverWhenComplete: false );
var task6ReconnectDisposingSecond = task6ReconnectDisposingSecondCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: task6ReconnectDisposingSecondCoordinator.IsPending,
    ConnectionTeardownFailed: task6ReconnectDisposingSecondCoordinator.HasTerminalFailure,
    TerminalTeardownDiagnostic: task6ReconnectDisposingSecondCoordinator.TerminalDiagnostic ) );
var task6ReconnectAcceptedCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectAcceptedCoordinator.DeferHotloadRecovery();
task6ReconnectAcceptedCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ReconnectAccepted = task6ReconnectAcceptedCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: task6ReconnectAcceptedCoordinator.IsPending,
    ConnectionTeardownFailed: task6ReconnectAcceptedCoordinator.HasTerminalFailure,
    TerminalTeardownDiagnostic: task6ReconnectAcceptedCoordinator.TerminalDiagnostic,
    TransportState: CollaborationTransportState.Disconnected ) );
Assert( task6ReconnectDisposingCompletion.Task.IsCompleted == false &&
    !task6ReconnectDisposing.Accepted && task6ReconnectDisposingCoordinator.DeferredHotloadRecovery &&
    !task6ReconnectDisposingSecond.Accepted && !task6ReconnectDisposingSecondCoordinator.DeferredHotloadRecovery &&
    task6ReconnectAccepted.Accepted && !task6ReconnectAcceptedCoordinator.LeaveRequested &&
    !task6ReconnectAcceptedCoordinator.DeferredHotloadRecovery,
    "Reconnect is refused while a detached connect is being disposed and clears recovery flags only when accepted" );
var task6ReconnectExplicitFailedCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectExplicitFailed = task6ReconnectExplicitFailedCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: true,
    TransportState: CollaborationTransportState.Failed ) );
var task6ReconnectExplicitDisconnectedCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectExplicitDisconnected = task6ReconnectExplicitDisconnectedCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    TransportState: CollaborationTransportState.Disconnected ) );
var task6ReconnectAmbiguousCoordinator = new ConnectionTeardownCoordinator();
var task6ReconnectAmbiguous = task6ReconnectAmbiguousCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: true,
    TransportState: CollaborationTransportState.None ) );
Assert( task6ReconnectExplicitFailed.Accepted && task6ReconnectExplicitDisconnected.Accepted &&
    !task6ReconnectAmbiguous.Accepted && !task6ReconnectAmbiguousCoordinator.LeaveRequested,
    "Reconnect admits only an explicitly failed or disconnected transport and rejects an ambiguous attached transport" );
var task6ReconnectLiveCoordinator = new ConnectionTeardownCoordinator();
task6ReconnectLiveCoordinator.DeferHotloadRecovery();
task6ReconnectLiveCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
var task6ReconnectLive = task6ReconnectLiveCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: true,
    ConnectionTeardownPending: false,
    ConnectionTeardownFailed: false,
    TerminalTeardownDiagnostic: null,
    ConnectionLive: true ) );
Assert( !task6ReconnectLive.Accepted &&
    task6ReconnectLive.ActionableError?.Contains( "Already connected", StringComparison.OrdinalIgnoreCase ) == true &&
    task6ReconnectLiveCoordinator.LeaveRequested && task6ReconnectLiveCoordinator.DeferredHotloadRecovery,
    "a live connection rejects Reconnect before leave authorization and deferred recovery are cleared" );
var task6ReconnectGuardParameters = typeof( ConnectionTeardownCoordinator ).GetMethod( nameof( ConnectionTeardownCoordinator.TryBeginReconnect ) )?
    .GetParameters().Select( parameter => parameter.ParameterType ).ToArray() ?? Array.Empty<Type>();
Assert( task6ReconnectGuardParameters.SequenceEqual( new[] { typeof( SessionReconnectGuardRequest ) } ),
    "Reconnect is admitted through a single shared engine-independent guard request" );

// Task 6 remediation BLOCKER 4: Leave must be exception-safe end to end. The
// unacknowledged-work predicate can throw from SceneAdapter.Capture and the
// recovery write can fail, so no exception may escape before disposal and the
// failure must be surfaced with a bounded diagnostic.
var task6LeaveAnnounced = new FlagDisposable();
var task6LeaveCaptureThrew = new ConnectionTeardownCoordinator();
var task6LeaveCaptureDisposed = new FlagDisposable();
var task6LeaveCaptureThrewOutcome = SessionLeaveRules.Run( new SessionLeaveRequest(
    task6LeaveCaptureThrew,
    LifecycleTransitionInProgress: false,
    HasUnacknowledgedWork: () => throw new IOException( "scene capture failed" ),
    Preserve: _ => true,
    Announce: () => task6LeaveAnnounced.Invoked = true,
    DisposeResources: () => { task6LeaveCaptureDisposed.Invoked = true; return true; } ) );
Assert( task6LeaveCaptureThrewOutcome.Accepted &&
    task6LeaveCaptureDisposed.Invoked &&
    task6LeaveCaptureThrewOutcome.ResourcesDisposed &&
    task6LeaveCaptureThrewOutcome.PreserveFailed &&
    task6LeaveCaptureThrewOutcome.Failure?.Contains( "scene capture failed", StringComparison.Ordinal ) == true &&
    task6LeaveCaptureThrewOutcome.Failure?.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    task6LeaveCaptureThrew.LeaveRequested,
    "a throwing unacknowledged-work check never escapes Leave before resources are disposed" );
var task6LeavePreserveThrew = new ConnectionTeardownCoordinator();
var task6LeavePreserveDisposed = new FlagDisposable();
var task6LeavePreserveThrewOutcome = SessionLeaveRules.Run( new SessionLeaveRequest(
    task6LeavePreserveThrew,
    LifecycleTransitionInProgress: false,
    HasUnacknowledgedWork: () => true,
    Preserve: _ => throw new IOException( "recovery write failed" ),
    Announce: () => task6LeaveAnnounced.Invoked = true,
    DisposeResources: () => { task6LeavePreserveDisposed.Invoked = true; return true; } ) );
Assert( task6LeavePreserveThrewOutcome.PreserveAttempted &&
    task6LeavePreserveDisposed.Invoked &&
    task6LeavePreserveThrewOutcome.ResourcesDisposed &&
    !task6LeavePreserveThrewOutcome.DisposalFailed &&
    task6LeavePreserveThrewOutcome.Failure?.Contains( "recovery write failed", StringComparison.Ordinal ) == true,
    "a failed recovery archive still disposes the transport and host server" );
// The leave announcement must reach peers while the transport is still open,
// so it is sent before disposal, and a failed announcement must not skip it.
var task6LeaveOrder = new List<string>();
var task6LeaveOrderOutcome = SessionLeaveRules.Run( new SessionLeaveRequest(
    new ConnectionTeardownCoordinator(),
    LifecycleTransitionInProgress: false,
    HasUnacknowledgedWork: () => false,
    Preserve: _ => true,
    Announce: () => task6LeaveOrder.Add( "announce" ),
    DisposeResources: () => { task6LeaveOrder.Add( "dispose" ); return true; } ) );
Assert( !task6LeaveOrderOutcome.DisposalFailed && task6LeaveOrder.SequenceEqual( new[] { "announce", "dispose" } ),
    "Leave announces the departure to peers before closing the transport" );
var task6LeaveDisposeThrows = new ConnectionTeardownCoordinator();
var task6LeaveDisposeThrowsOutcome = SessionLeaveRules.Run( new SessionLeaveRequest(
    task6LeaveDisposeThrows,
    LifecycleTransitionInProgress: false,
    HasUnacknowledgedWork: () => false,
    Preserve: _ => true,
    Announce: () => task6LeaveAnnounced.Invoked = true,
    DisposeResources: () => throw new IOException( "dispose exploded" ) ) );
Assert( task6LeaveDisposeThrowsOutcome.DisposalFailed &&
    task6LeaveDisposeThrowsOutcome.ResourcesDisposed &&
    task6LeaveDisposeThrowsOutcome.Failure?.Contains( "dispose exploded", StringComparison.Ordinal ) == true,
    "Leave never reports a clean teardown when disposal itself throws" );
var task6LeaveRefused = new ConnectionTeardownCoordinator();
var task6LeaveRefusedDispose = new FlagDisposable();
var task6LeaveRefusedOutcome = SessionLeaveRules.Run( new SessionLeaveRequest(
    task6LeaveRefused,
    LifecycleTransitionInProgress: true,
    HasUnacknowledgedWork: () => true,
    Preserve: _ => true,
    Announce: () => task6LeaveAnnounced.Invoked = true,
    DisposeResources: () => { task6LeaveRefusedDispose.Invoked = true; return true; } ) );
Assert( !task6LeaveRefusedOutcome.Accepted &&
    !task6LeaveRefusedOutcome.ResourcesDisposed &&
    !task6LeaveRefusedDispose.Invoked &&
    !task6LeaveRefused.LeaveRequested,
    "a refused Leave authorizes no recovery and disposes nothing" );
var task6LeaveValidateOnly = new ConnectionTeardownCoordinator();
var task6LeaveValidateRefused = task6LeaveValidateOnly.ValidateLeave( new SessionLeaveGuardRequest( true ) );
Assert( !task6LeaveValidateRefused.Accepted && !task6LeaveValidateOnly.LeaveRequested &&
    task6LeaveValidateOnly.ValidateLeave( new SessionLeaveGuardRequest( false ) ).Accepted,
    "a dry-run leave guard refuses without publishing leave authorization" );

// Task 6 remediation: Leave authorizes recovery only after its transition guard
// passes, and a rejected archive marker never reports a successful archive.
var task6LeaveGuardCoordinator = new ConnectionTeardownCoordinator();
var task6LeaveGuardRejected = task6LeaveGuardCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( true ) );
Assert( !task6LeaveGuardRejected.Accepted &&
    task6LeaveGuardRejected.ActionableError?.Contains( "in-progress", StringComparison.OrdinalIgnoreCase ) == true &&
    !task6LeaveGuardCoordinator.LeaveRequested && !task6LeaveGuardCoordinator.DeferredHotloadRecovery,
    "a rejected Leave never authorizes recovery" );
var task6LeaveRetryCoordinator = new ConnectionTeardownCoordinator();
var task6LeaveRetryRejected = task6LeaveRetryCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( true ) );
task6LeaveRetryCoordinator.DeferHotloadRecovery();
var task6LeaveRetryAccepted = task6LeaveRetryCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
Assert( !task6LeaveRetryRejected.Accepted && task6LeaveRetryAccepted.Accepted &&
    task6LeaveRetryCoordinator.LeaveRequested && task6LeaveRetryCoordinator.DeferredHotloadRecovery,
    "a Leave retry after a rejected transition begins the valid leave path and keeps deferred recovery armed" );
var task6LeaveGuardRequestParameter = typeof( ConnectionTeardownCoordinator ).GetMethod( nameof( ConnectionTeardownCoordinator.TryBeginLeave ) )?
    .GetParameters().Select( parameter => parameter.ParameterType ).ToArray() ?? Array.Empty<Type>();
Assert( task6LeaveGuardRequestParameter.SequenceEqual( new[] { typeof( SessionLeaveGuardRequest ) } ),
    "Leave is admitted through a single shared engine-independent guard request" );
var task6ArchiveRejectionLifecycle = new PlayModeLifecycle( true, "source-state-archive-rejected" );
task6ArchiveRejectionLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6ArchiveRejectionLifecycle.RecordHotloadTeardownFailure( "archive rejected by teardown failure" );
var task6ArchiveRejectionMarker = task6ArchiveRejectionLifecycle.RecordHotloadArchive();
var task6ArchiveRejectionCoordinator = new ConnectionTeardownCoordinator();
var task6ArchiveRejectionApplied = task6ArchiveRejectionCoordinator.ApplyArchiveMarker( task6ArchiveRejectionMarker );
var task6ArchiveRejectionObservation = task6ArchiveRejectionCoordinator.ObserveTeardownCompletion( true, false );
Assert( !task6ArchiveRejectionMarker.Accepted && !task6ArchiveRejectionApplied.Accepted &&
    !task6ArchiveRejectionLifecycle.HotloadArchiveRecorded &&
    !task6ArchiveRejectionCoordinator.HotloadArchiveRecorded &&
    !task6ArchiveRejectionObservation.AttemptRecovery,
    "a rejected archive marker is never recorded as a successful hotload archive" );
var task6ArchiveAcceptedLifecycle = new PlayModeLifecycle( true, "source-state-archive-accepted" );
task6ArchiveAcceptedLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6ArchiveAcceptedLifecycle.BlockForHotload( "archive accepted" );
var task6ArchiveAcceptedMarker = task6ArchiveAcceptedLifecycle.RecordHotloadArchive();
var task6ArchiveAcceptedCoordinator = new ConnectionTeardownCoordinator();
var task6ArchiveAcceptedApplied = task6ArchiveAcceptedCoordinator.ApplyArchiveMarker( task6ArchiveAcceptedMarker );
task6ArchiveAcceptedCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
task6ArchiveAcceptedCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
var task6ArchiveAcceptedObservation = task6ArchiveAcceptedCoordinator.ObserveTeardownCompletion( true, false );
Assert( task6ArchiveAcceptedMarker.Accepted && task6ArchiveAcceptedApplied.Accepted &&
    task6ArchiveAcceptedLifecycle.HotloadArchiveRecorded &&
    task6ArchiveAcceptedCoordinator.HotloadArchiveRecorded &&
    task6ArchiveAcceptedObservation.AttemptRecovery,
    "an accepted archive marker records the successful hotload archive" );

// Task 6 remediation: teardown disposal failures are fail-stop and bounded, and a
// repeated teardown reuses the pending gate instead of completing it again.
var task6TeardownFailingCoordinator = new ConnectionTeardownCoordinator();
var task6TeardownFailingBegin = task6TeardownFailingCoordinator.BeginDetachedTeardown<IDisposable>(
    Task.FromException<IDisposable>( new IOException( new string( 'd', 900 ) ) ),
    recoverWhenComplete: true );
var task6TeardownFailingDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6TeardownFailingCoordinator.IsPending && DateTime.UtcNow < task6TeardownFailingDeadline ) await Task.Delay( 10 );
var task6TeardownFailingObservation = task6TeardownFailingCoordinator.ObserveTeardownCompletion( true, false );
var task6TeardownFailingDiagnostic = task6TeardownFailingCoordinator.TerminalDiagnostic;
var task6TeardownFailingRecovery = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true,
    RequiresHotloadRecovery: true,
    ArchiveSucceeded: true,
    HotloadArchiveRecorded: true,
    TeardownPending: false,
    TeardownFailure: task6TeardownFailingObservation.TeardownFailure,
    TeardownFailed: task6TeardownFailingObservation.TeardownFailed,
    RecoveryDeferred: task6TeardownFailingCoordinator.DeferredHotloadRecovery ) );
Assert( task6TeardownFailingBegin.Outcome == ConnectionTeardownOutcome.Detached &&
    task6TeardownFailingObservation.Observed && task6TeardownFailingObservation.TeardownFailed &&
    !task6TeardownFailingObservation.AttemptRecovery &&
    task6TeardownFailingRecovery.Status == HotloadTeardownRecoveryStatus.TerminalTeardownFailure &&
    task6TeardownFailingCoordinator.HasTerminalFailure && task6TeardownFailingCoordinator.IsFailStop &&
    !task6TeardownFailingCoordinator.CanPromoteConnection,
    "a failed detached teardown is fail-stop: observed, terminal, and never promotes a connection" );
Assert( !string.IsNullOrWhiteSpace( task6TeardownFailingDiagnostic ) && task6TeardownFailingDiagnostic.Length <= 512 &&
    task6TeardownFailingDiagnostic.Contains( "fail-stop", StringComparison.OrdinalIgnoreCase ),
    "a teardown failure records a bounded terminal fail-stop diagnostic" );
var task6TeardownFailingReuse = task6TeardownFailingCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
Assert( task6TeardownFailingReuse.Outcome == ConnectionTeardownOutcome.TerminalFailure &&
    task6TeardownFailingReuse.Failure == task6TeardownFailingDiagnostic &&
    task6TeardownFailingCoordinator.HasTerminalFailure,
    "a repeated teardown after a terminal failure reports the same bounded failure instead of retrying" );
var task6TeardownPendingCoordinator = new ConnectionTeardownCoordinator();
var task6TeardownPendingCompletion = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
var task6TeardownPendingFirst = task6TeardownPendingCoordinator.BeginDetachedTeardown( task6TeardownPendingCompletion.Task, true );
var task6TeardownPendingSecond = task6TeardownPendingCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
var task6TeardownPendingThird = task6TeardownPendingCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
var task6TeardownPendingFrames = new[]
{
    task6TeardownPendingCoordinator.ObserveTeardownCompletion( true, false ),
    task6TeardownPendingCoordinator.ObserveTeardownCompletion( true, false )
};
Assert( task6TeardownPendingFirst.Outcome == ConnectionTeardownOutcome.Detached &&
    task6TeardownPendingSecond.Outcome == ConnectionTeardownOutcome.ReusedPending &&
    task6TeardownPendingThird.Outcome == ConnectionTeardownOutcome.ReusedPending &&
    !task6TeardownPendingFrames.Any( frame => frame.Observed ) &&
    task6TeardownPendingCoordinator.IsPending,
    "a repeated teardown reuses the pending gate and never completes it early" );
var task6TeardownPendingStart = task6TeardownPendingCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(), ConnectionBusy: true,
    ConnectionTeardownPending: true,
    ConnectionTeardownFailed: false, TerminalTeardownDiagnostic: null ) );
var task6TeardownPendingReconnect = task6TeardownPendingCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle, SessionEstablished: true, ConnectionAttemptPending: true, ConnectionBusy: true,
    ConnectionTeardownPending: true, ConnectionTeardownFailed: false, TerminalTeardownDiagnostic: null ) );
var task6TeardownPendingRecovery = HotloadTeardownRecovery.Decide( new HotloadTeardownRecoveryRequest(
    LeaveRequested: true, RequiresHotloadRecovery: true, ArchiveSucceeded: true, HotloadArchiveRecorded: true,
    TeardownPending: true, TeardownFailure: null, TeardownFailed: false,
    RecoveryDeferred: task6TeardownPendingCoordinator.DeferredHotloadRecovery ) );
Assert( !task6TeardownPendingStart.Accepted && !task6TeardownPendingReconnect.Accepted &&
    task6TeardownPendingRecovery.Status == HotloadTeardownRecoveryStatus.WaitingForTeardown &&
    !task6TeardownPendingRecovery.AttemptRecovery,
    "a pending teardown blocks Start, Reconnect, and hotload recovery" );
var task6TeardownPendingTransport = new TrackedDisposable();
task6TeardownPendingCompletion.SetResult( task6TeardownPendingTransport );
var task6TeardownPendingCompleteDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6TeardownPendingCoordinator.IsPending && DateTime.UtcNow < task6TeardownPendingCompleteDeadline ) await Task.Delay( 10 );
// The promotion decision is read before observing: an unobserved completed gate is
// still an unobserved teardown as far as connection promotion is concerned.
var task6TeardownPendingPromoteBefore = task6TeardownPendingCoordinator.CanPromoteConnection;
var task6TeardownPendingFirstCompletion = task6TeardownPendingCoordinator.ObserveTeardownCompletion( true, false );
var task6TeardownPendingPromoteAfter = task6TeardownPendingCoordinator.CanPromoteConnection;
var task6TeardownPendingIdle = task6TeardownPendingCoordinator.ObserveTeardownCompletion( false, false );
Assert( task6TeardownPendingTransport.DisposeCount == 1 &&
    !task6TeardownPendingPromoteBefore && task6TeardownPendingFirstCompletion.Observed &&
    !task6TeardownPendingFirstCompletion.TeardownFailed && task6TeardownPendingPromoteAfter &&
    task6TeardownPendingIdle.Observed && task6TeardownPendingCoordinator.IsPending == false,
    "a completed gate is observed before connection promotion is unblocked" );
var task6TeardownPendingLeaveCoordinator = new ConnectionTeardownCoordinator();
var task6TeardownPendingLeaveCompletion = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
task6TeardownPendingLeaveCoordinator.BeginDetachedTeardown( task6TeardownPendingLeaveCompletion.Task, true );
var task6TeardownPendingLeaveTransport = new TrackedDisposable();
task6TeardownPendingLeaveCompletion.SetResult( task6TeardownPendingLeaveTransport );
var task6TeardownPendingLeaveDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6TeardownPendingLeaveCoordinator.IsPending && DateTime.UtcNow < task6TeardownPendingLeaveDeadline ) await Task.Delay( 10 );
// Leave arrives after the teardown gate already completed.
task6TeardownPendingLeaveCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
task6TeardownPendingLeaveCoordinator.ApplyArchiveMarker( PlayModeTransitionResult.Ok() );
var task6TeardownPendingLeaveReady = task6TeardownPendingLeaveCoordinator.ObserveTeardownCompletion( true, false );
var task6TeardownPendingLeaveFrames = new[]
{
    task6TeardownPendingLeaveCoordinator.ObserveTeardownCompletion( true, false ),
    task6TeardownPendingLeaveCoordinator.ObserveTeardownCompletion( true, false )
};
var task6TeardownPendingLeaveIdle = task6TeardownPendingLeaveCoordinator.ObserveTeardownCompletion( false, false );
Assert( task6TeardownPendingLeaveTransport.DisposeCount == 1 &&
    task6TeardownPendingLeaveReady.Observed && task6TeardownPendingLeaveReady.AttemptRecovery &&
    task6TeardownPendingLeaveReady.Status == HotloadTeardownRecoveryStatus.Ready &&
    task6TeardownPendingLeaveCoordinator.DeferredHotloadRecovery &&
    task6TeardownPendingLeaveFrames.All( frame => frame.Observed && frame.AttemptRecovery &&
        frame.Status == task6TeardownPendingLeaveReady.Status ) &&
    task6TeardownPendingLeaveIdle.Observed && !task6TeardownPendingLeaveIdle.AttemptRecovery,
    "Leave after a completed teardown re-derives the same recovery decision and keeps deferred recovery stable" );

var task6UnobservedCompletionCoordinator = new ConnectionTeardownCoordinator();
var task6UnobservedCompletionSource = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
task6UnobservedCompletionCoordinator.BeginDetachedTeardown( task6UnobservedCompletionSource.Task, false );
var task6UnobservedCompletionResource = new TrackedDisposable();
task6UnobservedCompletionSource.SetResult( task6UnobservedCompletionResource );
var task6UnobservedCompletionDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6UnobservedCompletionCoordinator.IsPending && DateTime.UtcNow < task6UnobservedCompletionDeadline ) await Task.Delay( 10 );
var task6UnobservedStart = task6UnobservedCompletionCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(), ConnectionBusy: false,
    ConnectionTeardownPending: task6UnobservedCompletionCoordinator.IsPending,
    ConnectionTeardownFailed: task6UnobservedCompletionCoordinator.HasTerminalFailure,
    TerminalTeardownDiagnostic: task6UnobservedCompletionCoordinator.TerminalDiagnostic ) );
var task6UnobservedReconnect = task6UnobservedCompletionCoordinator.TryBeginReconnect( new SessionReconnectGuardRequest(
    task6ApprovedCoexistenceLifecycle,
    SessionEstablished: true,
    ConnectionAttemptPending: false,
    ConnectionBusy: false,
    ConnectionTeardownPending: task6UnobservedCompletionCoordinator.IsPending,
    ConnectionTeardownFailed: task6UnobservedCompletionCoordinator.HasTerminalFailure,
    TerminalTeardownDiagnostic: task6UnobservedCompletionCoordinator.TerminalDiagnostic,
    TransportState: CollaborationTransportState.Disconnected ) );
var task6UnobservedRepeat = task6UnobservedCompletionCoordinator.BeginDetachedTeardown<IDisposable>( null, false );
Assert( !task6UnobservedStart.Accepted && !task6UnobservedReconnect.Accepted &&
    task6UnobservedCompletionCoordinator.TeardownCompletionPending &&
    task6UnobservedRepeat.Outcome == ConnectionTeardownOutcome.ReusedPending,
    "an unobserved completed teardown blocks Start, Reconnect, and replacement by CompleteWithoutConnection" );
var task6UnobservedCompletion = task6UnobservedCompletionCoordinator.ObserveTeardownCompletion( false, false );
Assert( task6UnobservedCompletion.Observed && !task6UnobservedCompletionCoordinator.TeardownCompletionPending,
    "the first frame observes the preserved completed teardown exactly once" );

var task6TeardownDisconnectingCoordinator = new ConnectionTeardownCoordinator();
var task6TeardownDisconnectingLifecycle = new PlayModeLifecycle( true, "source-state-disconnecting" );
task6TeardownDisconnectingLifecycle.RecordStartIntent( "scenes/runtime.scene", task6Source, "scenes/runtime.scene" );
task6TeardownDisconnectingLifecycle.BlockForHotload( "disconnecting" );
task6TeardownDisconnectingLifecycle.RecordHotloadArchive();
task6TeardownDisconnectingCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
task6TeardownDisconnectingCoordinator.ApplyArchiveMarker( PlayModeTransitionResult.Ok() );
var task6TeardownDisconnectingBegin = task6TeardownDisconnectingCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
var task6TeardownDisconnectingObservation = task6TeardownDisconnectingCoordinator.ObserveTeardownCompletion( true, true );
Assert( task6TeardownDisconnectingBegin.Outcome == ConnectionTeardownOutcome.CompletedWithoutConnection &&
    !task6TeardownDisconnectingObservation.AttemptRecovery &&
    task6TeardownDisconnectingObservation.Status == HotloadTeardownRecoveryStatus.WaitingForTeardown,
    "a completed teardown never recovers while a connection attempt is still pending" );
var task6TeardownDisposingCoordinator = new ConnectionTeardownCoordinator();
var task6TeardownDisposingCompletion = new TaskCompletionSource<IDisposable>( TaskCreationOptions.RunContinuationsAsynchronously );
task6TeardownDisposingCoordinator.BeginDetachedTeardown( task6TeardownDisposingCompletion.Task, true );
var task6TeardownDisposingObservation = task6TeardownDisposingCoordinator.ObserveTeardownCompletion( true, false );
var task6TeardownDisposingSecond = task6TeardownDisposingCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
var task6TeardownDisposingTransport = new ThrowingDisposable();
task6TeardownDisposingCompletion.SetResult( task6TeardownDisposingTransport );
var task6TeardownDisposingCompleteDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6TeardownDisposingCoordinator.IsPending && DateTime.UtcNow < task6TeardownDisposingCompleteDeadline ) await Task.Delay( 10 );
var task6TeardownDisposingFinal = task6TeardownDisposingCoordinator.ObserveTeardownCompletion( true, false );
Assert( !task6TeardownDisposingObservation.Observed &&
    task6TeardownDisposingSecond.Outcome == ConnectionTeardownOutcome.ReusedPending &&
    task6TeardownDisposingFinal.Observed && task6TeardownDisposingFinal.TeardownFailed &&
    !task6TeardownDisposingFinal.AttemptRecovery && task6TeardownDisposingFinal.Error?.Length <= 512 &&
    task6TeardownDisposingTransport.DisposeCount == 1,
    "a transport that throws while being disposed fails stop without claiming a successful teardown" );

// Task 6 remediation: recovery requires an explicit Leave, a completed gate, no
// pending connect, no terminal failure, and a currently successful archive.
var task6RecoveryCoordinator = new ConnectionTeardownCoordinator();
task6RecoveryCoordinator.BeginDetachedTeardown<IDisposable>( null, false );
// Before an explicit Leave the teardown is observed, but recovery is not required.
var task6RecoveryBeforeLeave = task6RecoveryCoordinator.ObserveTeardownCompletion( true, false );
task6RecoveryCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
// Leave requested but no archive recorded yet: recovery stays blocked.
var task6RecoveryUnarchived = task6RecoveryCoordinator.ObserveTeardownCompletion( true, false );
task6RecoveryCoordinator.ApplyArchiveMarker( PlayModeTransitionResult.Ok() );
// A current successful archive completes the decision.
var task6RecoveryArchived = task6RecoveryCoordinator.ObserveTeardownCompletion( true, false );
var task6RecoveryAgain = task6RecoveryCoordinator.ObserveTeardownCompletion( true, false );
var task6RecoveryThird = task6RecoveryCoordinator.ObserveTeardownCompletion( true, false );
Assert( task6RecoveryBeforeLeave.Observed && task6RecoveryBeforeLeave.Status == HotloadTeardownRecoveryStatus.NotRequired &&
    !task6RecoveryBeforeLeave.AttemptRecovery &&
    task6RecoveryUnarchived.Observed && !task6RecoveryUnarchived.AttemptRecovery &&
    task6RecoveryUnarchived.Status == HotloadTeardownRecoveryStatus.ArchiveFailed &&
    task6RecoveryArchived.Observed && task6RecoveryArchived.AttemptRecovery &&
    task6RecoveryArchived.Status == HotloadTeardownRecoveryStatus.Ready &&
    task6RecoveryCoordinator.DeferredHotloadRecovery &&
    task6RecoveryAgain.AttemptRecovery && !task6RecoveryAgain.EnableDeferredRecovery &&
    task6RecoveryAgain.Status == task6RecoveryThird.Status &&
    task6RecoveryAgain.AttemptRecovery == task6RecoveryThird.AttemptRecovery,
    "hotload recovery requires an explicit Leave and a current successful archive, and repeated frames keep deferred recovery stable" );
var task6RecoveryMissingArchiveCoordinator = new ConnectionTeardownCoordinator();
task6RecoveryMissingArchiveCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
task6RecoveryMissingArchiveCoordinator.BeginDetachedTeardown<IDisposable>( null, false );
var task6RecoveryMissingArchiveBefore = task6RecoveryMissingArchiveCoordinator.ObserveTeardownCompletion( true, false );
task6RecoveryMissingArchiveCoordinator.ApplyArchiveMarker( PlayModeTransitionResult.Rejected( "archive rejected" ) );
var task6RecoveryInvalidated = task6RecoveryMissingArchiveCoordinator.ObserveTeardownCompletion( true, false );
Assert( task6RecoveryMissingArchiveBefore.Observed && !task6RecoveryMissingArchiveBefore.AttemptRecovery &&
    task6RecoveryMissingArchiveBefore.Status == HotloadTeardownRecoveryStatus.ArchiveFailed &&
    !task6RecoveryInvalidated.AttemptRecovery &&
    task6RecoveryInvalidated.Status == HotloadTeardownRecoveryStatus.ArchiveFailed &&
    !task6RecoveryMissingArchiveCoordinator.HotloadArchiveRecorded,
    "an invalidated archive marker stops hotload recovery" );
var task6RecoveryOrdinaryCoordinator = new ConnectionTeardownCoordinator();
task6RecoveryOrdinaryCoordinator.BeginDetachedTeardown<IDisposable>( null, false );
var task6RecoveryOrdinary = task6RecoveryOrdinaryCoordinator.ObserveTeardownCompletion( false, false );
var task6RecoveryOrdinaryAgain = task6RecoveryOrdinaryCoordinator.ObserveTeardownCompletion( false, false );
Assert( task6RecoveryOrdinary.Observed && !task6RecoveryOrdinary.AttemptRecovery &&
    task6RecoveryOrdinary.Status == HotloadTeardownRecoveryStatus.NotRequired &&
    task6RecoveryOrdinary.Error is null && !task6RecoveryOrdinaryAgain.AttemptRecovery &&
    task6RecoveryOrdinaryAgain.Status == HotloadTeardownRecoveryStatus.NotRequired,
    "an ordinary reconnect teardown without hotload recovery is observed but never attempts recovery" );
var task6RecoveryBlockedCoordinator = new ConnectionTeardownCoordinator();
task6RecoveryBlockedCoordinator.TryBeginLeave( new SessionLeaveGuardRequest( false ) );
task6RecoveryBlockedCoordinator.ApplyArchiveMarker( PlayModeTransitionResult.Ok() );
task6RecoveryBlockedCoordinator.RecordTerminalTeardownFailure( "blocked teardown failure" );
var task6RecoveryBlockedBegin = task6RecoveryBlockedCoordinator.BeginDetachedTeardown<IDisposable>( null, true );
var task6RecoveryBlocked = task6RecoveryBlockedCoordinator.ObserveTeardownCompletion( true, false );
Assert( task6RecoveryBlockedBegin.Outcome == ConnectionTeardownOutcome.TerminalFailure &&
    task6RecoveryBlocked.Observed && !task6RecoveryBlocked.AttemptRecovery &&
    task6RecoveryBlocked.Status == HotloadTeardownRecoveryStatus.TerminalTeardownFailure &&
    task6RecoveryBlocked.Error?.Length <= 512,
    "a recorded terminal teardown failure blocks hotload recovery with a bounded diagnostic" );
var task6RecoveryConnectionGuard = typeof( ConnectionTeardownCoordinator ).GetMethod( nameof( ConnectionTeardownCoordinator.ObserveTeardownCompletion ) )?
    .GetParameters().Select( parameter => parameter.ParameterType ).ToArray() ?? Array.Empty<Type>();
Assert( task6RecoveryConnectionGuard.SequenceEqual( new[] { typeof( bool ), typeof( bool ) } ),
    "teardown completion observation requires an explicit pending-connection argument" );
var task6RecoveryGateMethods = typeof( ConnectionTeardownCoordinator ).GetMethods( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance )
    .Where( method => method.Name.Contains( "Teardown", StringComparison.Ordinal ) )
    .Select( method => method.Name ).ToArray();
Assert( task6RecoveryGateMethods.Contains( nameof( ConnectionTeardownCoordinator.BeginDetachedTeardown ) ) &&
    task6RecoveryGateMethods.Contains( nameof( ConnectionTeardownCoordinator.ObserveTeardownCompletion ) ),
    "connection teardown is driven only through the shared coordinator seam" );

// Task 6 remediation: every scene-bearing packet must name the tracked target's
// CURRENT canonical path. A queued packet must not apply after a rebind.
var task6TrackedAdmissions = new[]
{
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "snapshot", false, task6Source, null,
        task6ApprovedCoexistenceLifecycle, null, null ),
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "accepted", false, task6Source, "",
        task6ApprovedCoexistenceLifecycle, null, "" ),
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "snapshot", false, task6Source, "scenes/runtime.scene",
        task6ApprovedCoexistenceLifecycle, null, "Scenes/Runtime.Scene" ),
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "accepted", false, task6Source, "scenes/runtime.scene",
        task6ApprovedCoexistenceLifecycle, null, "scenes\\runtime.scene" ),
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "snapshot", false, task6Source, "scenes/rebound.scene",
        task6ApprovedCoexistenceLifecycle, null, "scenes/runtime.scene" ),
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "accepted", false, task6Source, "scenes/../secrets.scene",
        task6ApprovedCoexistenceLifecycle, null, "scenes/../secrets.scene" ),
    PlayModeSourceCoexistence.CanAdmitTrackedScenePacket( "presence", false, task6Source, "scenes/runtime.scene",
        task6ApprovedCoexistenceLifecycle, null, "scenes/runtime.scene" )
};
Assert( !task6TrackedAdmissions[0] && !task6TrackedAdmissions[1],
    "a tracked scene packet with no current canonical path is rejected fail-closed" );
Assert( task6TrackedAdmissions[2] && task6TrackedAdmissions[3],
    "tracked scene packet path comparison is normalized across case and separators" );
Assert( !task6TrackedAdmissions[4],
    "a queued packet must not apply after a tracked session is rebound to another path" );
Assert( !task6TrackedAdmissions[5],
    "an unsafe traversal path is never admitted as a tracked scene packet" );
Assert( !task6TrackedAdmissions[6],
    "a non scene-bearing packet kind is never admitted through the tracked scene packet seam" );
var task6TrackedSceneParameters = typeof( PlayModeSourceCoexistence ).GetMethod( nameof( PlayModeSourceCoexistence.CanAdmitTrackedScenePacket ) )?
    .GetParameters().Select( parameter => parameter.ParameterType ).ToArray() ?? Array.Empty<Type>();
var task6ProcessSceneParameters = typeof( PlayModeSourceCoexistence ).GetMethod( nameof( PlayModeSourceCoexistence.CanProcessSourcePackets ) )?
    .GetParameters().Select( parameter => parameter.ParameterType ).ToArray() ?? Array.Empty<Type>();
Assert( task6TrackedSceneParameters.Length == 7 && task6TrackedSceneParameters[5] == typeof( RuntimeSessionDescriptor ) &&
    task6TrackedSceneParameters[6] == typeof( string ) &&
    task6ProcessSceneParameters.Length == 5 && task6ProcessSceneParameters[4] == typeof( RuntimeSessionDescriptor ),
    "tracked scene packet admission requires a current runtime descriptor and the packet scene key" );

// Task 6 production-routing regression: the exact shared seam called by Frame
// canonicalizes before lookup and fails closed for missing/invalid targets.
var task6RouteTarget = new object();
var task6RouteTargets = new Dictionary<string, object>
{
    ["scenes/runtime.scene"] = task6RouteTarget
};
var task6RouteEquivalent = ScenePacketRouting.RouteTrackedPacket(
    "snapshot",
    "Scenes\\Runtime.Scene",
    task6RouteTargets,
    _ => false,
    _ => "scenes/runtime.scene",
    new PlayModeLifecycle( false ),
    _ => null );
var task6RouteMissing = ScenePacketRouting.RouteTrackedPacket(
    "accepted",
    "scenes/missing.scene",
    task6RouteTargets,
    _ => false,
    _ => "scenes/runtime.scene",
    new PlayModeLifecycle( false ),
    _ => null );
var task6RouteInvalid = ScenePacketRouting.RouteTrackedPacket(
    "snapshot",
    "scenes/../secret.scene",
    task6RouteTargets,
    _ => false,
    _ => "scenes/runtime.scene",
    new PlayModeLifecycle( false ),
    _ => null );
Assert( task6RouteEquivalent.Admitted &&
    task6RouteEquivalent.CanonicalSceneKey == "scenes/runtime.scene" &&
    !task6RouteMissing.Admitted && !string.IsNullOrWhiteSpace( task6RouteMissing.Error ) &&
    !task6RouteInvalid.Admitted && !string.IsNullOrWhiteSpace( task6RouteInvalid.Error ),
    "the production scene-packet route canonicalizes equivalent keys and fails visibly for missing or invalid targets" );
var task6RouteNoncanonicalTracked = ScenePacketRouting.RouteTrackedPacket(
    "snapshot",
    "scenes/runtime.scene",
    new Dictionary<string, object> { ["Scenes\\Runtime.Scene"] = task6RouteTarget },
    _ => false,
    _ => "scenes/runtime.scene",
    new PlayModeLifecycle( false ),
    _ => null );
Assert( task6RouteNoncanonicalTracked.Admitted,
    "the production scene-packet route canonicalizes both the packet key and tracked target key before lookup" );

// Task 6 remediation: both active resources must be attempted even when the
// first disposal throws. This exercises the shared production disposal seam.
var task6DisposalFirstThrows = new ThrowingDisposable();
var task6DisposalSecond = new TrackedDisposable();
var task6DisposalResult = ResourceDisposalRules.DisposePair(
    task6DisposalFirstThrows,
    "collaboration transport",
    task6DisposalSecond,
    "collaboration host server" );
Assert( !task6DisposalResult.Succeeded &&
    task6DisposalFirstThrows.DisposeCount == 1 && task6DisposalSecond.DisposeCount == 1 &&
    !string.IsNullOrWhiteSpace( task6DisposalResult.Failure ) &&
    task6DisposalResult.Failure.Length <= CollaborationDiagnostics.MaxDiagnosticLength,
    "a wire disposal failure still attempts server disposal and records one bounded fail-stop diagnostic" );

// Task 6 remediation BLOCKER 5: production Start must perform its real
// preparation inside the prepare callback, the host server must count as a busy
// resource, and an unpromoted connect must be disposed through the teardown
// coordinator rather than dropped. These drive the same coordinator production
// calls, with the same argument shapes production passes.
var task6StartServerOnly = CollaborationSessionResourceRules.IsBusy(
    wireAttached: false, connecting: false, hostServer: true, failStop: false );
Assert( task6StartServerOnly,
    "a bound host server with no attached transport and no pending connect still reports busy" );
Assert( !CollaborationSessionResourceRules.IsBusy( wireAttached: false, connecting: false, hostServer: false, failStop: false ) &&
    CollaborationSessionResourceRules.IsBusy( wireAttached: true, connecting: false, hostServer: false, failStop: false ) &&
    CollaborationSessionResourceRules.IsBusy( wireAttached: false, connecting: true, hostServer: false, failStop: false ) &&
    CollaborationSessionResourceRules.IsBusy( wireAttached: false, connecting: false, hostServer: false, failStop: true ),
    "the production busy predicate covers transport, pending connect, host server, and fail-stop" );
// With the host server counted, a second Start while a faulted host connect left
// the listener bound must be refused instead of hitting a port conflict.
var task6StartServerBoundCoordinator = new ConnectionTeardownCoordinator();
var task6StartServerBound = task6StartServerBoundCoordinator.TryBeginStart( new SessionStartGuardRequest(
    PlayModeTransitionResult.Ok(),
    ConnectionBusy: task6StartServerOnly,
    ConnectionTeardownPending: task6StartServerBoundCoordinator.IsPending,
    ConnectionTeardownFailed: task6StartServerBoundCoordinator.HasTerminalFailure,
    TerminalTeardownDiagnostic: task6StartServerBoundCoordinator.TerminalDiagnostic ) );
Assert( !task6StartServerBound.Accepted &&
    task6StartServerBound.ActionableError?.Contains( "Leave the current connection first", StringComparison.Ordinal ) == true,
    "a still-bound host server refuses a second Start with an actionable reason" );
// Start's prepare callback must be the one that creates the session resources,
// and a rejected commit must dispose whatever it produced.
var task6StartPrepareCoordinator = new ConnectionTeardownCoordinator();
var task6StartPrepareRan = new FlagDisposable();
var task6StartPreparedServerBindThrows = task6StartPrepareCoordinator.BeginStart(
    new SessionStartCommitRequest(
    task6StartPrepareCoordinator.ValidateStart( new SessionStartGuardRequest(
        PlayModeTransitionResult.Ok(),
        ConnectionBusy: false,
        ConnectionTeardownPending: false,
        ConnectionTeardownFailed: false,
        TerminalTeardownDiagnostic: null ) ),
    () =>
    {
        // Production binds the host server and opens the connect here, so a
        // failure in either must be reported as a typed reject, not escape.
        task6StartPrepareRan.Invoked = true;
        throw new IOException( "port already in use" );
    } ) );
Assert( !task6StartPreparedServerBindThrows.Accepted &&
    task6StartPrepareRan.Invoked &&
    task6StartPreparedServerBindThrows.ActionableError?.Contains( "port already in use", StringComparison.Ordinal ) == true &&
    task6StartPreparedServerBindThrows.ActionableError?.Length <= CollaborationDiagnostics.MaxDiagnosticLength,
    "a Start prepare failure surfaces a bounded actionable reject instead of an escaped exception" );
// An unpromoted or failed connect is routed through detached teardown so the
// produced transport is disposed rather than leaked.
var task6StartUnpromoted = new ConnectionTeardownCoordinator();
var task6StartUnpromotedResource = new TrackedDisposable();
var task6StartUnpromotedResult = task6StartUnpromoted.BeginDetachedTeardown(
    Task.FromResult<IDisposable>( task6StartUnpromotedResource ), recoverWhenComplete: false );
var task6StartUnpromotedDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6StartUnpromoted.IsPending && DateTime.UtcNow < task6StartUnpromotedDeadline ) await Task.Delay( 10 );
var task6StartUnpromotedObservation = task6StartUnpromoted.ObserveTeardownCompletion( false, false );
Assert( task6StartUnpromotedResult.Outcome == ConnectionTeardownOutcome.Detached &&
    task6StartUnpromotedResource.DisposeCount == 1 &&
    task6StartUnpromotedObservation.Observed &&
    !task6StartUnpromotedObservation.TeardownFailed,
    "an unpromoted connect routed through detached teardown disposes the transport it produced" );
var task6StartUnpromotedFaulted = new ConnectionTeardownCoordinator();
var task6StartUnpromotedFaultedResult = task6StartUnpromotedFaulted.BeginDetachedTeardown(
    Task.FromException<FlagDisposable>( new IOException( "connect refused" ) ), recoverWhenComplete: false );
var task6StartUnpromotedFaultedDeadline = DateTime.UtcNow.AddSeconds( 5 );
while ( task6StartUnpromotedFaulted.IsPending && DateTime.UtcNow < task6StartUnpromotedFaultedDeadline ) await Task.Delay( 10 );
var task6StartUnpromotedFaultedObservation = task6StartUnpromotedFaulted.ObserveTeardownCompletion( false, false );
Assert( task6StartUnpromotedFaultedResult.Outcome == ConnectionTeardownOutcome.Detached &&
    task6StartUnpromotedFaultedObservation.Observed &&
    task6StartUnpromotedFaultedObservation.TeardownFailed &&
    task6StartUnpromotedFaulted.HasTerminalFailure,
    "a failed connect routed through detached teardown is fail-stop, never a silently dropped connect" );

// Task 6 remediation finding 6: a refused connection promotion must hand the
// completed connect to the teardown coordinator, dispose the transport it
// produced, and not re-throw the same refusal on every later frame.
var task6PromotionAdmissible = ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
    SourcePacketsAdmissible: true, TeardownBusy: false, ConnectCompleted: true ) );
Assert( task6PromotionAdmissible,
    "an admissible completed connect with no teardown in flight may be promoted" );
Assert( !ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
        SourcePacketsAdmissible: false, TeardownBusy: false, ConnectCompleted: true ) ) &&
    !ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
        SourcePacketsAdmissible: true, TeardownBusy: true, ConnectCompleted: true ) ) &&
    !ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
        SourcePacketsAdmissible: true, TeardownBusy: false, ConnectCompleted: false ) ),
    "the production promotion rule refuses an inadmissible source, a busy teardown, and an incomplete connect" );
// The refused promotion path: detach the connect, let teardown dispose it, and
// never hand the same completed connect back to a later frame. `connect` models
// the session's `connecting` field, which the refusal path clears. This project
// compiles with nullable annotations disabled, so the cleared state is a plain
// null assignment rather than a `?` annotation (which would warn here).
var task6PromotionRefused = new ConnectionTeardownCoordinator();
var task6PromotionRefusedTransport = new TrackedDisposable();
Task<IDisposable> task6PromotionConnect = Task.FromResult<IDisposable>( task6PromotionRefusedTransport );
var task6PromotionRefusalFrames = 0;
var task6PromotionAdmittedAfterRefusal = false;
for ( var task6PromotionFrame = 0; task6PromotionFrame < 3; task6PromotionFrame++ )
{
    if ( task6PromotionConnect is null ) break;
    var admitted = ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
        SourcePacketsAdmissible: false,
        TeardownBusy: task6PromotionRefused.IsFailStop,
        ConnectCompleted: task6PromotionConnect.IsCompleted ) );
    if ( admitted )
    {
        task6PromotionAdmittedAfterRefusal = true;
        break;
    }
    task6PromotionRefusalFrames++;
    // Mirrors Frame: the connect is cleared and routed through teardown, which
    // disposes whatever it produced instead of leaving it attached.
    var detached = task6PromotionConnect;
    task6PromotionConnect = null;
    task6PromotionRefused.BeginDetachedTeardown( detached, recoverWhenComplete: false );
    var task6PromotionDeadline = DateTime.UtcNow.AddSeconds( 5 );
    while ( task6PromotionRefused.IsPending && DateTime.UtcNow < task6PromotionDeadline ) await Task.Delay( 10 );
    task6PromotionRefused.ObserveTeardownCompletion( false, false );
}
Assert( task6PromotionRefusalFrames == 1 &&
    !task6PromotionAdmittedAfterRefusal &&
    task6PromotionConnect is null &&
    task6PromotionRefusedTransport.DisposeCount == 1 &&
    !task6PromotionRefused.HasTerminalFailure,
    "a refused promotion detaches and disposes the produced transport once, and no later frame re-promotes it" );
// The same detachment keeps the next frame out of the promotion path entirely:
// an unobserved teardown still blocks promotion.
var task6PromotionUnobserved = new ConnectionTeardownCoordinator();
var task6PromotionUnobservedTransport = new TrackedDisposable();
task6PromotionUnobserved.BeginDetachedTeardown( Task.FromResult<IDisposable>( task6PromotionUnobservedTransport ), false );
Assert( !ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
        SourcePacketsAdmissible: true,
        TeardownBusy: !task6PromotionUnobserved.CanPromoteConnection,
        ConnectCompleted: true ) ),
    "an unobserved completed teardown keeps blocking connection promotion" );

// The frame pump's own mapping from the coordinator to the request. CanPromoteConnection
// is a promotion-PERMITTED flag, so the request field it feeds is the NEGATION; this
// helper is the single place that inversion is written down, because the production call
// site once passed the flag positionally — which reads plausibly, inverts the rule, and
// leaves a healthy connect refused while the session reports "Connecting…" forever.
foreach ( var task6GateAdmissible in new[] { true, false } )
foreach ( var task6GateCanPromote in new[] { true, false } )
    Assert( ConnectionPromotionRules.ShouldPromote( task6GateAdmissible, task6GateCanPromote ) ==
        ConnectionPromotionRules.MayPromote( new ConnectionPromotionRequest(
            SourcePacketsAdmissible: task6GateAdmissible,
            TeardownBusy: !task6GateCanPromote,
            ConnectCompleted: true ) ),
        "the frame-pump helper composes with MayPromote for admissible=" + task6GateAdmissible +
        ", canPromoteConnection=" + task6GateCanPromote );
var task6GateHealthy = new ConnectionTeardownCoordinator();
Assert( task6GateHealthy.CanPromoteConnection &&
    ConnectionPromotionRules.ShouldPromote( sourcePacketsAdmissible: true,
        canPromoteConnection: task6GateHealthy.CanPromoteConnection ),
    "an admissible completed connect against a fresh coordinator is promoted" );
var task6GateBusy = new ConnectionTeardownCoordinator();
task6GateBusy.BeginDetachedTeardown( Task.FromResult<IDisposable>( new TrackedDisposable() ), false );
Assert( !task6GateBusy.CanPromoteConnection &&
    !ConnectionPromotionRules.ShouldPromote( sourcePacketsAdmissible: true,
        canPromoteConnection: task6GateBusy.CanPromoteConnection ),
    "a coordinator with a teardown still in flight refuses promotion" );
Assert( !ConnectionPromotionRules.ShouldPromote( sourcePacketsAdmissible: false,
        canPromoteConnection: new ConnectionTeardownCoordinator().CanPromoteConnection ),
    "an inadmissible source refuses promotion even against a fresh coordinator" );

// Task 6 remediation finding 7: two long disposal failures must still name BOTH
// resources. Bounding per entry and then per entry after composition is what
// keeps the host-server diagnostic from being erased by the transport one.
var task6DisposalBothLong = ResourceDisposalRules.DisposePair(
    new ThrowingDisposable( new string( 'w', 700 ) ),
    "collaboration transport",
    new ThrowingDisposable( new string( 's', 700 ) ),
    "collaboration host server" );
Assert( !task6DisposalBothLong.Succeeded &&
    !string.IsNullOrWhiteSpace( task6DisposalBothLong.Failure ) &&
    task6DisposalBothLong.Failure?.Contains( "collaboration transport", StringComparison.Ordinal ) == true &&
    task6DisposalBothLong.Failure?.Contains( "collaboration host server", StringComparison.Ordinal ) == true &&
    task6DisposalBothLong.Failure?.Length <= CollaborationDiagnostics.MaxDiagnosticLength,
    "two long disposal failures still name both resources in one bounded fail-stop diagnostic" );
var task6DisposalFirstLongOnly = ResourceDisposalRules.DisposePair(
    new ThrowingDisposable( new string( 'w', 700 ) ),
    "collaboration transport",
    new TrackedDisposable(),
    "collaboration host server" );
Assert( task6DisposalFirstLongOnly.Failure?.Contains( "collaboration transport", StringComparison.Ordinal ) == true &&
    task6DisposalFirstLongOnly.Failure?.Length <= CollaborationDiagnostics.MaxDiagnosticLength,
    "a single long disposal failure stays bounded and keeps its resource name" );

// Task 6 remediation finding 8: a recovery path must only be published after the
// file is actually written, so the dock never shows a path to a missing archive.
int task6RecoveryWrittenBytes = 0;
var task6RecoveryPath = RecoveryPathRules.CreatePath( "C:/project/.collaboration/recovery", "20260101-000000-0000000" );
var task6RecoveryPublished = RecoveryPathRules.PublishAfterWrite(
    task6RecoveryPath,
    write: () => throw new IOException( "disk full" ) );
Assert( task6RecoveryPublished.Published is null && !task6RecoveryPublished.Succeeded,
    "a failed recovery write publishes no recovery path" );
var task6RecoveryWritten = RecoveryPathRules.PublishAfterWrite(
    task6RecoveryPath,
    write: () => { task6RecoveryWrittenBytes = 1; } );
Assert( task6RecoveryWritten.Succeeded && task6RecoveryWrittenBytes == 1 &&
    task6RecoveryWritten.Published == task6RecoveryPath,
    "a successful recovery write publishes exactly the path it wrote" );
Assert( task6RecoveryPath.Contains( "recovery", StringComparison.Ordinal ) &&
    task6RecoveryPath.EndsWith( ".json", StringComparison.Ordinal ),
    "the recovery path is a bounded json file inside the recovery folder" );

// Task 6 remediation: externally supplied and exception diagnostics are bounded at
// storage, composition, and log boundaries.
var task6LongExternalText = new string( 'p', 2000 );
var task6FailureOutcome = CollaborationFailureRules.Compose( null, task6LongExternalText, null );
var task6FailureRecoveryOutcome = CollaborationFailureRules.Compose( null, task6LongExternalText, task6LongExternalText );
var task6FailureExistingOutcome = CollaborationFailureRules.Compose( "first failure", task6LongExternalText, null );
var task6FailureStatus = task6FailureOutcome.Status + task6LongExternalText;
Assert( task6FailureOutcome.Error.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    task6FailureRecoveryOutcome.Error.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    task6FailureExistingOutcome.Error == "first failure" && task6FailureExistingOutcome.Status is null &&
    task6FailureStatus.Length > CollaborationDiagnostics.MaxDiagnosticLength,
    "a raw peer or exception failure is stored and composed bounded at the storage boundary" );
var task6BoundedFromOversized = CollaborationDiagnostics.Bounded( task6LongExternalText );
var task6BoundedFromEmpty = CollaborationDiagnostics.Bounded( null );
var task6BoundedFromExact = CollaborationDiagnostics.Bounded( new string( 'b', CollaborationDiagnostics.MaxDiagnosticLength ) );
var task6BoundedDescribe = CollaborationDiagnostics.DescribeExternalFailure(
    "Rejected offline edit: ", new Exception( task6LongExternalText ) );
Assert( task6BoundedFromOversized.Length == CollaborationDiagnostics.MaxDiagnosticLength &&
    task6BoundedFromEmpty == string.Empty &&
    task6BoundedFromExact.Length == CollaborationDiagnostics.MaxDiagnosticLength &&
    !task6FailureOutcome.Error.EndsWith( task6LongExternalText, StringComparison.Ordinal ) &&
    task6BoundedDescribe.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    task6BoundedDescribe.StartsWith( "Rejected offline edit: ", StringComparison.Ordinal ),
    "the shared diagnostic boundary bounds oversized external text and empty values" );
var task6TransportException = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(
    "Transport failed: ", new Exception( task6LongExternalText ) );
var task6ComposedTransportException = CollaborationDiagnostics.Bounded( "Disconnected: " + task6TransportException );
Assert( task6TransportException.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    task6ComposedTransportException.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    task6ComposedTransportException.StartsWith( "Disconnected: ", StringComparison.Ordinal ) &&
    CollaborationTransportDiagnostics.BoundedStoredDiagnostic( task6LongExternalText ).Length <= CollaborationDiagnostics.MaxDiagnosticLength,
    "production transport/server storage helpers bound exception-derived values and re-bound composed diagnostics" );

var journal = new List<Packet>();
var authority = new Authority(journal.Add);
var first = Obj("first"); var second = Obj("second");
authority.Subscribe(new() { Scene="scenes/test.scene", Objects=[first,second] });
var a = Edit("A",new Change(first.Id,first,Rename(first,"A wins")));
Assert(authority.Apply(a).Kind == "accepted", "first edit accepted");
var sameObjectReceipt = authority.Apply(Edit("B",new Change(first.Id,first,Rename(first,"B later wins"))));
Assert(sameObjectReceipt.Kind == "accepted" && sameObjectReceipt.Merged && sameObjectReceipt.Notice == "Concurrent edits to \"A wins\" were combined; the later edit was kept for: Name." &&
    authority.Scenes["scenes/test.scene"][first.Id].Data["Name"]!.GetValue<string>() == "B later wins",
    "same-object concurrent edit of the same property is merged: the later edit wins and the clash is reported, not refused: " + sameObjectReceipt.Kind + " " + sameObjectReceipt.Error);
var receipt = authority.Apply(a);
Assert(receipt.Revision == 1 && journal.Count == 3, "replay deduplicated without journaling twice (the journal holds the seed, edit A and the merged edit B)");
Assert(authority.Apply(Edit("B",new Change(second.Id,second,Rename(second,"independent")))).Kind == "accepted", "independent object edits merge");
var atomicReceipt = authority.Apply(Edit("B",new Change(first.Id,first,Rename(first,"stale")),new Change(second.Id,null,second)));
Assert(atomicReceipt.Kind == "reject" && atomicReceipt.Error.StartsWith("Conflict on " + second.Id, StringComparison.Ordinal) && authority.Scenes["scenes/test.scene"].Count==2 &&
    authority.Scenes["scenes/test.scene"][first.Id].Data["Name"]!.GetValue<string>() == "B later wins", "multi-object rejection is atomic: the unmergeable creation of an existing id refuses the whole operation, including the mergeable change before it");
var child = Obj("child",first.Id);
Assert(authority.Apply(Edit("A",new Change(child.Id,null,child))).Kind == "accepted", "child creation");
var currentFirst = authority.Scenes["scenes/test.scene"][first.Id];
Assert(authority.Apply(Edit("A",new Change(first.Id,currentFirst,currentFirst with {Parent=child.Id}))).Kind == "reject", "parent cycles rejected");
Assert(authority.Apply(Edit("A",new Change(first.Id,currentFirst,null))).Kind == "reject", "orphaning a child rejected");
Assert(authority.Apply(Edit("A",new Change(first.Id,currentFirst,null),new Change(child.Id,child,null))).Kind == "accepted", "atomic subtree deletion");
var snapshot = authority.Subscribe(new() { Scene="scenes/test.scene", Objects=[first,second] });
Assert(snapshot.Objects.Count==1 && snapshot.Objects[0].Data["Name"]!.GetValue<string>()=="independent", "late join gets authority, not obsolete baseline");
var badData = (JsonObject)snapshot.Objects[0].Data.DeepClone(); badData["NetworkMode"]=1;
Assert(authority.Apply(Edit("B",new Change(second.Id,snapshot.Objects[0],snapshot.Objects[0] with {Data=badData}))).Kind == "accepted", "a network settings edit is synchronised (it was refused while edits were allowlisted)");
var brokenDisk = new Authority(_ => throw new IOException("disk full"));
try { brokenDisk.Subscribe(new() {Scene="scenes/test.scene",Objects=[first]}); Assert(false,"disk failure"); } catch(IOException) { Assert(brokenDisk.Scenes.Count==0,"seed not accepted when persistence fails"); }
bool failWrites=false;
var disk = new Authority(_ => {if(failWrites) throw new IOException("disk full");});
disk.Subscribe(new() {Scene="scenes/test.scene",Objects=[first]}); failWrites=true;
Assert(disk.Apply(a).Kind=="reject" && disk.Revision==0 && StateRules.Equal(disk.Scenes["scenes/test.scene"][first.Id],first),"operation not acknowledged on journal failure");
// Ask the OS for a free port so concurrent test runs (parallel worktrees) never collide on a fixed one.
int freePort; { var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start(); freePort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
using var listener = new Listener("127.0.0.1",freePort);
using var client = await Wire.Connect("127.0.0.1",freePort);
Wire accepted = null;
var deadline=DateTime.UtcNow.AddSeconds(5);
while(accepted is null && DateTime.UtcNow<deadline) {listener.Accepted.TryDequeue(out accepted); await Task.Delay(10);}
Assert(accepted is not null,"real TCP connection");
using(accepted) {
 client.Send(a);
 Packet received=null;
 while(received is null && DateTime.UtcNow<deadline) {accepted.Incoming.TryDequeue(out received); await Task.Delay(10);}
 Assert(received?.Id==a.Id && received.Changes.Count==1,"framed JSON round trip");
 accepted.Send(receipt);
 Packet ack=null;
 while(ack is null && DateTime.UtcNow<deadline) {client.Incoming.TryDequeue(out ack); await Task.Delay(10);}
 Assert(ack?.Revision==1,"acknowledgement round trip");
}
// Task: Steam account identity. SteamID 0 is the signed-out sentinel and must be
// rejected rather than accepted as an anonymous peer.
var steamParsed = default( SteamIdentity );
Assert( SteamIdentityRules.TryParse( "76561198000000001", out steamParsed ) &&
    steamParsed.Value == 76561198000000001UL,
    "a decimal SteamID parses to the exact account value" );
Assert( SteamIdentityRules.IsValid( 76561198000000001UL ) &&
    !SteamIdentityRules.IsValid( 0UL ),
    "SteamID 0 is rejected as the signed-out sentinel while a real account is valid" );
Assert( !SteamIdentityRules.TryParse( null, out _ ) &&
    !SteamIdentityRules.TryParse( "", out _ ) &&
    !SteamIdentityRules.TryParse( "0", out _ ) &&
    !SteamIdentityRules.TryParse( "-1", out _ ) &&
    !SteamIdentityRules.TryParse( "12.5", out _ ) &&
    !SteamIdentityRules.TryParse( " 76561198000000001", out _ ),
    "absent, sentinel, negative, non-integer, and whitespace-padded SteamIDs all fail closed" );
Assert( SteamIdentityRules.Canonical( steamParsed ) == "76561198000000001" &&
    SteamIdentityRules.DisplayLabel( steamParsed ) != SteamIdentityRules.Canonical( steamParsed ),
    "the canonical form is the exact decimal SteamID and the display label is distinct from it" );
// Task: host-side Steam token verification. The endpoint returns {SteamId, Status} and
// BOTH must agree with the claim: accepting on Status alone would let a valid token be
// replayed against a different claimed identity.
Assert( SteamTokenRules.Decide( 76561198000000001L, "tok", 200, 76561198000000001L, "ok" ) == SteamTokenVerdict.Verified,
    "a token that verifies for the claimed account is accepted" );
Assert( SteamTokenRules.Decide( 76561198000000001L, "tok", 200, 76561198000000002L, "ok" ) == SteamTokenVerdict.SteamIdMismatch,
    "a valid token replayed against a different claimed SteamID is refused" );
Assert( SteamTokenRules.Decide( 76561198000000001L, null, 200, 76561198000000001L, "ok" ) == SteamTokenVerdict.TokenMissing &&
    SteamTokenRules.Decide( 76561198000000001L, "", 200, 76561198000000001L, "ok" ) == SteamTokenVerdict.TokenMissing &&
    SteamTokenRules.Decide( 76561198000000001L, "   ", 200, 76561198000000001L, "ok" ) == SteamTokenVerdict.TokenMissing,
    "a missing or blank token is refused rather than treated as anonymous" );
Assert( SteamTokenRules.Decide( 76561198000000001L, new string( 'a', SteamTokenRules.MaxTokenLength + 1 ), 200, 76561198000000001L, "ok" ) == SteamTokenVerdict.TokenMalformed,
    "an oversized token is refused as malformed" );
Assert( SteamTokenRules.Decide( 76561198000000001L, "tok", null, null, null ) == SteamTokenVerdict.ServiceUnavailable &&
    SteamTokenRules.Decide( 76561198000000001L, "tok", 500, null, null ) == SteamTokenVerdict.ServiceRejected &&
    SteamTokenRules.Decide( 76561198000000001L, "tok", 200, null, "ok" ) == SteamTokenVerdict.SteamIdMismatch &&
    SteamTokenRules.Decide( 76561198000000001L, "tok", 200, 76561198000000001L, "denied" ) == SteamTokenVerdict.ServiceRejected,
    "an unreachable service, a server error, an absent SteamId, and a non-ok status are each distinguished" );
// SteamID 0 is the signed-out sentinel. Without an explicit guard, a peer claiming
// account 0 whose token verifies as 0 would satisfy 0 == 0 and be admitted as Verified —
// the exact anonymous-peer hole this library must never allow.
Assert( SteamTokenRules.Decide( 0L, "tok", 200, 0L, "ok" ) == SteamTokenVerdict.SteamIdMismatch,
    "a signed-out peer claiming SteamID 0 is refused even when the service echoes 0 back" );
Assert( SteamTokenRules.Decide( -1L, "tok", 200, -1L, "ok" ) == SteamTokenVerdict.SteamIdMismatch,
    "a negative SteamID is refused even when the service echoes the same value back" );
// A credential made only of control or invisible characters must never pass the
// plausibility pre-filter. IsNullOrWhiteSpace does not treat NUL, U+0001, or a
// zero-width space as whitespace, so a one-byte token used to reach Verified.
Assert( !SteamTokenRules.IsPlausibleToken( "\0" ) &&
    !SteamTokenRules.IsPlausibleToken( "" ) &&
    !SteamTokenRules.IsPlausibleToken( "" ) &&
    !SteamTokenRules.IsPlausibleToken( "﻿" ),
    "a token made only of control or invisible characters is refused as implausible" );
Assert( SteamTokenRules.Decide( 1L, "\0", 200, 1L, "ok" ) == SteamTokenVerdict.TokenMalformed,
    "a one-byte control-character token never reaches a verified verdict" );
Assert( SteamTokenRules.IsPlausibleToken( "abc123XYZ" ),
    "an ordinary printable token is still accepted as plausible" );
// Task: Steam identity on the wire. The handshake is fail-closed: no account ID, a
// signed-out account ID, or a missing identity token must all be refused, because an
// unverified account ID is just a self-asserted string. Every fixture below also carries
// the protocol-4 fields, so each assertion reaches the check it is named after rather than
// stopping earlier at the older-library refusal.
static Packet IdentityHello( string steamId, string steamToken ) => new()
{
    Kind = "hello",
    ProtocolVersion = Protocol.CurrentVersion,
    Mode = CollaborationMode.Source,
    SteamId = steamId,
    SteamToken = steamToken,
    OrgIdent = "cyn",
    LibraryVersion = "1.1.1",
    ProjectManifest = "a1b2c3d4"
};
Assert( Protocol.GetHandshakeError( IdentityHello( null, "tok" ) ) != null, "a hello with no Steam account ID is rejected" );
Assert( Protocol.GetHandshakeError( IdentityHello( "0", "tok" ) ) != null, "a hello with the signed-out sentinel SteamID is rejected" );
Assert( Protocol.GetHandshakeError( IdentityHello( "76561198000000001", null ) ) != null, "a hello with no identity token is rejected" );
Assert( Protocol.GetHandshakeError( IdentityHello( " 76561198000000001", "tok" ) ) != null, "a hello with a whitespace-padded SteamID is rejected rather than trimmed" );
Assert( Protocol.GetHandshakeError( IdentityHello( "76561198000000001", "tok" ) ) is null, "a hello carrying a valid Steam account ID and token is accepted" );
Assert( Protocol.CurrentVersion == 4 && Protocol.CurrentVersionName == "teamcreate-4",
    "the handshake protocol is at the version this library ships" );
// Task: account admission. One account gets at most one roster slot, and an
// unverifiable peer is never admitted just because a check could not be run.
Assert( SteamIdentityAdmission.GetDuplicateError( "76561198000000001", new[] { "76561198000000001" } ) != null,
    "a second session from an account already in the session is refused" );
Assert( SteamIdentityAdmission.GetDuplicateError( "76561198000000001", new[] { "76561198000000002" } ) is null,
    "a different account may join the same session" );
Assert( SteamIdentityAdmission.GetDuplicateError( "76561198000000001", new string[0] ) is null &&
    SteamIdentityAdmission.GetDuplicateError( "76561198000000001", null ) is null,
    "an empty or absent roster never rejects the first account" );
// Proven by running the real parser: leading zeros denote the SAME account, so admission
// must compare canonical forms or one account could occupy two roster slots.
Assert( SteamIdentityAdmission.GetDuplicateError( "0076561198000000001", new[] { "76561198000000001" } ) != null,
    "a zero-padded spelling of an account already present is still refused as a duplicate" );
// ALSO proven: a trailing NUL parses successfully and yields the SAME canonical form as the
// clean string while the raw strings differ. That is a second, independent way to spell one
// account on the wire, and it is exactly why this comparison is canonical-only.
Assert( SteamIdentityAdmission.GetDuplicateError( "76561198000000001\0", new[] { "76561198000000001" } ) != null,
    "a NUL-suffixed spelling of an account already present is still refused as a duplicate" );
Assert( SteamIdentityAdmission.GetDuplicateError( "0", new string[0] ) != null,
    "the signed-out sentinel account is refused even when the roster is empty" );
Assert( SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.Verified, false ) is null,
    "a verified peer is admitted" );
Assert( SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.ServiceUnavailable, true ) is null &&
    SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.ServiceUnavailable, false ) != null,
    "an unreachable identity service is admitted only when unverified peers are explicitly allowed" );
Assert( SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.SteamIdMismatch, true ) != null &&
    SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.TokenMalformed, true ) != null &&
    SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.TokenMissing, true ) != null &&
    SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.ServiceRejected, true ) != null,
    "allowing unverified peers never excuses a mismatched, malformed, missing, or rejected token" );
// A refused token has exactly ONE sentence, and it is the same value the dock recognises to
// arm its single automatic re-request: the verdict's wording and the recognition being the
// same constant is what keeps a wording change from silently disabling that retry.
Assert( string.Equals( SteamIdentityAdmission.GetVerificationError( SteamTokenVerdict.ServiceRejected, false ),
        SteamIdentityAdmission.TokenRejectedMessage, StringComparison.Ordinal ) &&
    SteamIdentityAdmission.IsTokenRejection( SteamIdentityAdmission.TokenRejectedMessage ),
    "a refused token is reported as the exact sentence the dock recognises" );
// Recognition is exact, because everything else that reaches the user must be REPORTED
// rather than retried: a composed failure, a different refusal, or a sentence that merely
// contains this one are all some other problem the user has to see.
Assert( !SteamIdentityAdmission.IsTokenRejection( null ) &&
    !SteamIdentityAdmission.IsTokenRejection( "" ) &&
    !SteamIdentityAdmission.IsTokenRejection( SteamIdentityAdmission.TokenRejectedMessage + " Recovery write also failed: disk full" ) &&
    !SteamIdentityAdmission.IsTokenRejection( "prefix " + SteamIdentityAdmission.TokenRejectedMessage ) &&
    !SteamIdentityAdmission.IsTokenRejection( "This peer's Steam identity could not be confirmed." ),
    "only the exact refusal sentence is recognised as a refused token" );
// ---- Task 1.1: typed collaboration configuration ---------------------------
// The reader must fail closed on every malformed field while still returning whatever it
// could read, so the caller can refuse to start with one specific message instead of a crash.
var configTempRoot = Path.Combine( Path.GetTempPath(), "teamcreate-config-" + Guid.NewGuid().ToString( "N" ) );
try
{
    Directory.CreateDirectory( configTempRoot );

    var validConfigPath = Path.Combine( configTempRoot, "valid.json" );
    File.WriteAllText( validConfigPath,
        "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\", \"Org\": \"acme\", \"LibraryIdent\": \"acme/collab\", \"LibraryVersion\": \"1.2.3\", \"RequireSteamFriend\": false, \"RequireOrgMember\": false, \"RequirePublished\": false, \"OrgMemberAllowlist\": [ \"76561198000000001\", \"0076561198000000002\" ] }" );
    var validConfig = CollaborationConfig.LoadFrom( validConfigPath, out var validConfigError );
    Assert( validConfigError is null &&
        validConfig.ProjectId == "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f" &&
        validConfig.Org == "acme" && validConfig.LibraryIdent == "acme/collab" &&
        validConfig.LibraryVersion == new Version( 1, 2, 3 ) &&
        !validConfig.RequireSteamFriend && !validConfig.RequireOrgMember && !validConfig.RequirePublished &&
        validConfig.OrgMemberAllowlist.SequenceEqual( new[] { "76561198000000001", "76561198000000002" } ),
        "a fully specified collaboration config loads every documented field with canonical allowlist entries" );

    var missingConfig = CollaborationConfig.LoadFrom( Path.Combine( configTempRoot, "absent.json" ), out var missingConfigError );
    Assert( missingConfigError is null && missingConfig.ProjectId is null && missingConfig.Org == "cyn" &&
        missingConfig.LibraryIdent is null && missingConfig.LibraryVersion is null &&
        missingConfig.RequireSteamFriend && missingConfig.RequireOrgMember && missingConfig.RequirePublished &&
        missingConfig.OrgMemberAllowlist.Length == 0,
        "a missing config file returns the documented defaults without an error" );

    var malformedConfigPath = Path.Combine( configTempRoot, "malformed.json" );
    File.WriteAllText( malformedConfigPath, "{ \"ProjectId\": " );
    var malformedConfig = CollaborationConfig.LoadFrom( malformedConfigPath, out var malformedConfigError );
    Assert( malformedConfigError is not null && malformedConfigError.Contains( "malformed.json", StringComparison.Ordinal ) &&
        malformedConfigError.Contains( "ProjectId", StringComparison.Ordinal ) == false &&
        malformedConfig.ProjectId is null && malformedConfig.Org == "cyn" && malformedConfig.RequirePublished,
        "unparsable JSON returns defaults and an error naming the file" );

    var badProjectIdPath = Path.Combine( configTempRoot, "bad-project-id.json" );
    File.WriteAllText( badProjectIdPath, "{ \"ProjectId\": \"not-a-guid\", \"Org\": \"acme\" }" );
    var badProjectIdConfig = CollaborationConfig.LoadFrom( badProjectIdPath, out var badProjectIdError );
    Assert( badProjectIdError is not null && badProjectIdError.Contains( "ProjectId", StringComparison.Ordinal ) &&
        badProjectIdConfig.ProjectId is null && badProjectIdConfig.Org == "acme",
        "a non-GUID ProjectId is reported by field name and kept null while the rest still loads" );

    var badVersionPath = Path.Combine( configTempRoot, "bad-version.json" );
    File.WriteAllText( badVersionPath, "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\", \"LibraryVersion\": \"not.a.version\", \"Org\": \"acme\" }" );
    var badVersionConfig = CollaborationConfig.LoadFrom( badVersionPath, out var badVersionError );
    Assert( badVersionError is not null && badVersionError.Contains( "LibraryVersion", StringComparison.Ordinal ) &&
        badVersionConfig.LibraryVersion is null && badVersionConfig.Org == "acme",
        "an unparsable LibraryVersion is reported by field name and left null" );

    var allowlistPath = Path.Combine( configTempRoot, "allowlist.json" );
    File.WriteAllText( allowlistPath,
        "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\", \"OrgMemberAllowlist\": [ \"76561198000000001\", \"not-a-number\", \"-5\", \"\", \" 7 \", \"42\" ] }" );
    var allowlistConfig = CollaborationConfig.LoadFrom( allowlistPath, out var allowlistError );
    Assert( allowlistError is not null && allowlistError.Contains( "OrgMemberAllowlist", StringComparison.Ordinal ) &&
        allowlistConfig.OrgMemberAllowlist.SequenceEqual( new[] { "76561198000000001", "42" } ),
        "invalid allowlist entries are dropped and reported while the valid entries survive" );

    var unknownFieldPath = Path.Combine( configTempRoot, "unknown-field.json" );
    File.WriteAllText( unknownFieldPath,
        "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\", \"FutureOption\": { \"nested\": [ 1, 2 ] }, \"AnotherFutureFlag\": true }" );
    var unknownFieldConfig = CollaborationConfig.LoadFrom( unknownFieldPath, out var unknownFieldError );
    Assert( unknownFieldError is null && unknownFieldConfig.ProjectId == "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f" &&
        unknownFieldConfig.Org == "cyn" && unknownFieldConfig.RequireSteamFriend,
        "unknown config fields are ignored for forward compatibility" );

    // The session server compares ProjectIds ordinally, so every spelling of one GUID must
    // collapse onto the single canonical value or three peers would run three projects.
    var canonicalProjectIdPath = Path.Combine( configTempRoot, "canonical-project-id.json" );
    File.WriteAllText( canonicalProjectIdPath, "{ \"ProjectId\": \"{2E9F8F81-8B38-4207-AD3E-4E7C0BEA567F}\" }" );
    var bracedProjectId = CollaborationConfig.LoadFrom( canonicalProjectIdPath, out var bracedProjectIdError );
    File.WriteAllText( canonicalProjectIdPath, "{ \"ProjectId\": \"2e9f8f818b384207ad3e4e7c0bea567f\" }" );
    var compactProjectId = CollaborationConfig.LoadFrom( canonicalProjectIdPath, out var compactProjectIdError );
    File.WriteAllText( canonicalProjectIdPath, "{ \"ProjectId\": \"2E9F8F81-8B38-4207-AD3E-4E7C0BEA567F\" }" );
    var upperProjectId = CollaborationConfig.LoadFrom( canonicalProjectIdPath, out var upperProjectIdError );
    Assert( bracedProjectIdError is null && compactProjectIdError is null && upperProjectIdError is null &&
        bracedProjectId.ProjectId == "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f" &&
        compactProjectId.ProjectId == bracedProjectId.ProjectId &&
        upperProjectId.ProjectId == bracedProjectId.ProjectId,
        "braced, compact, and uppercase ProjectId spellings all load as one canonical value" );

    var whitespaceOrgPath = Path.Combine( configTempRoot, "whitespace-org.json" );
    File.WriteAllText( whitespaceOrgPath, "{ \"Org\": \"   \" }" );
    var whitespaceOrgConfig = CollaborationConfig.LoadFrom( whitespaceOrgPath, out var whitespaceOrgError );
    Assert( whitespaceOrgError is not null && whitespaceOrgError.Contains( "Org", StringComparison.Ordinal ) &&
        whitespaceOrgConfig.Org == "cyn",
        "a whitespace-only organization is reported and replaced by the fail-closed default" );

    var paddedOrgPath = Path.Combine( configTempRoot, "padded-org.json" );
    File.WriteAllText( paddedOrgPath, "{ \"Org\": \"  acme  \" }" );
    var paddedOrgConfig = CollaborationConfig.LoadFrom( paddedOrgPath, out var paddedOrgError );
    Assert( paddedOrgError is null && paddedOrgConfig.Org == "acme",
        "an organization ident is stored trimmed of surrounding whitespace" );

    var defaultsConfig = CollaborationConfig.Defaults();
    Assert( defaultsConfig.ProjectId is null && defaultsConfig.Org == "cyn" && defaultsConfig.LibraryIdent is null &&
        defaultsConfig.LibraryVersion is null && defaultsConfig.RequireSteamFriend && defaultsConfig.RequireOrgMember &&
        defaultsConfig.RequirePublished && defaultsConfig.OrgMemberAllowlist.Length == 0,
        "collaboration config defaults are the documented fail-closed values" );
}
finally
{
    try { if ( Directory.Exists( configTempRoot ) ) Directory.Delete( configTempRoot, true ); }
    catch ( IOException ) { }
}

// ---- Task 1.2: deterministic project content manifest ----------------------
// The manifest is compared between two machines, so it may only depend on relative paths and
// file bytes: build-order directories, IDE files, and the collaboration state directory must
// never reach the hash.
static string NewManifestTree()
{
    var root = Path.Combine( Path.GetTempPath(), "teamcreate-manifest-" + Guid.NewGuid().ToString( "N" ) );
    Directory.CreateDirectory( Path.Combine( root, "Assets", "scenes" ) );
    Directory.CreateDirectory( Path.Combine( root, "Assets", "obj", "Debug" ) );
    Directory.CreateDirectory( Path.Combine( root, "Code", "bin" ) );
    Directory.CreateDirectory( Path.Combine( root, "Libraries", "collaboration", "Editor" ) );
    Directory.CreateDirectory( Path.Combine( root, "ProjectSettings" ) );
    Directory.CreateDirectory( Path.Combine( root, "Editor" ) );
    Directory.CreateDirectory( Path.Combine( root, "obj", "Debug" ) );
    File.WriteAllText( Path.Combine( root, "project.sbproj" ), "<project />" );
    File.WriteAllText( Path.Combine( root, "Assets", "scenes", "main.scene" ), "scene-data" );
    File.WriteAllText( Path.Combine( root, "Assets", "scratch.user" ), "local editor state" );
    File.WriteAllText( Path.Combine( root, "Assets", "obj", "Debug", "build.cache" ), "build output" );
    File.WriteAllText( Path.Combine( root, "Code", "Foo.cs" ), "class Foo {}" );
    File.WriteAllText( Path.Combine( root, "Code", "Foo.csproj" ), "<project />" );
    File.WriteAllText( Path.Combine( root, "Code", "bin", "Foo.dll" ), "assembly" );
    File.WriteAllText( Path.Combine( root, "Code", "bin", "Foo.pdb" ), "symbols" );
    File.WriteAllText( Path.Combine( root, "Libraries", "collaboration", "Editor", "Core.cs" ), "class Core {}" );
    File.WriteAllText( Path.Combine( root, "ProjectSettings", "collaboration.json" ), "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\" }" );
    File.WriteAllText( Path.Combine( root, "Editor", "EditorProbe.cs" ), "class EditorProbe {}" );
    return root;
}

static void RemoveManifestTree( string root )
{
    try { if ( Directory.Exists( root ) ) Directory.Delete( root, true ); }
    catch ( IOException ) { }
}

static bool IsEightLowercaseHex( string value )
    => value is { Length: 8 } && value.All( c => c is >= '0' and <= '9' or >= 'a' and <= 'f' );

var manifestBase = NewManifestTree();
var manifestByteChange = NewManifestTree();
var manifestCollaborationState = NewManifestTree();
var manifestRenamed = NewManifestTree();
var manifestGit = NewManifestTree();
try
{
    var baseHash = ProjectManifest.Compute( manifestBase );
    Assert( IsEightLowercaseHex( baseHash ) &&
        ProjectManifest.Compute( manifestCollaborationState ) == baseHash,
        "two identical project trees produce the same eight-character lowercase manifest hash" );

    var manifestInputs = ProjectManifest.EnumerateInputs( manifestBase ).ToList();
    Assert( manifestInputs.SequenceEqual( manifestInputs.OrderBy( x => x, StringComparer.Ordinal ) ) &&
        manifestInputs.All( x => !x.Contains( '\\' ) ) &&
        manifestInputs.SequenceEqual( new[]
        {
            "Assets/scenes/main.scene",
            "Code/Foo.cs",
            "Editor/EditorProbe.cs",
            "Libraries/collaboration/Editor/Core.cs",
            "ProjectSettings/collaboration.json",
            "project.sbproj"
        } ),
        "manifest inputs are sorted ordinal, slash separated, and limited to the documented include set" );
    Assert( !manifestInputs.Any( x => x.Contains( "obj/" ) || x.Contains( "bin/" ) || x.Contains( ".csproj" ) ||
        x.EndsWith( ".user", StringComparison.Ordinal ) || x.EndsWith( ".dll", StringComparison.Ordinal ) ||
        x.EndsWith( ".pdb", StringComparison.Ordinal ) || x.EndsWith( ".cache", StringComparison.Ordinal ) ),
        "build output, IDE files, and binaries never reach the manifest inputs" );

    // One byte of a scene changes the content hash, so a stale peer copy is detectable.
    File.WriteAllText( Path.Combine( manifestByteChange, "Assets", "scenes", "main.scene" ), "scene-datb" );
    Assert( ProjectManifest.Compute( manifestByteChange ) != baseHash,
        "changing one byte in a scene changes the manifest hash" );

    // Local session state lives under .collaboration/ and must not invalidate a peer's copy.
    Directory.CreateDirectory( Path.Combine( manifestCollaborationState, ".collaboration" ) );
    File.WriteAllText( Path.Combine( manifestCollaborationState, ".collaboration", "session.json" ), "{ \"peer\": \"abc\" }" );
    Assert( ProjectManifest.Compute( manifestCollaborationState ) == baseHash,
        "adding a file under .collaboration/ leaves the manifest hash unchanged" );

    // Same bytes under a different path are a different project layout.
    File.Move( Path.Combine( manifestRenamed, "Assets", "scenes", "main.scene" ),
        Path.Combine( manifestRenamed, "Assets", "scenes", "renamed.scene" ) );
    Assert( ProjectManifest.Compute( manifestRenamed ) != baseHash,
        "renaming a file changes the manifest hash even when its bytes are unchanged" );

    Assert( ProjectManifest.Compute( Path.Combine( manifestBase, "does-not-exist" ) ) == "",
        "a missing project root hashes to the empty cannot-verify marker" );

    Assert( IsEightLowercaseHex( ProjectManifest.HashLines( new[] { "Assets/a.scene\n0123456789abcdef", "Assets/b.scene\nfedcba9876543210" } ) ) &&
        ProjectManifest.HashLines( new[] { "Assets/a.scene\n0123456789abcdef" } ) ==
            ProjectManifest.HashLines( new[] { "Assets/a.scene\n0123456789abcdef" } ) &&
        ProjectManifest.HashLines( new[] { "Assets/a.scene\n0123456789abcdef" } ) !=
            ProjectManifest.HashLines( new[] { "Assets/b.scene\n0123456789abcdef" } ),
        "hash lines are hashed deterministically and include the path" );

    // A file that cannot be read means this hash would describe a different set of bytes
    // than the peer's, so no hash may be produced at all: partial evidence must never look
    // like complete evidence.
    string lockedHash;
    using ( new FileStream( Path.Combine( manifestBase, "Assets", "scenes", "main.scene" ),
        FileMode.Open, FileAccess.Read, FileShare.None ) )
    {
        lockedHash = ProjectManifest.Compute( manifestBase );
    }
    Assert( lockedHash == "" && ProjectManifest.Compute( manifestBase ) == baseHash,
        "a file that cannot be read yields the empty cannot-verify marker instead of a partial hash" );

    // A nested library carries its own repository, and its packfiles are rewritten by every
    // fetch or gc, so the object store can never be an input to the project hash.
    Directory.CreateDirectory( Path.Combine( manifestGit, "Libraries", "dep", ".git" ) );
    File.WriteAllText( Path.Combine( manifestGit, "Libraries", "dep", ".git", "config" ),
        "[core] repositoryformatversion = 0" );
    Assert( !ProjectManifest.EnumerateInputs( manifestGit ).Contains( "Libraries/dep/.git/config" ) &&
        ProjectManifest.Compute( manifestGit ) == baseHash,
        "a nested library's .git object store is never an input to the manifest hash" );

    // The walk cannot be made to hit a genuinely unreadable directory without changing real
    // ACLs, so the listing is injected through the internal seam: a directory that cannot be
    // listed anywhere in the walk marks the evidence incomplete, a clean walk does not.
    var cleanListing = ProjectManifest.Compute( manifestBase, null, out var cleanIncomplete );
    var deniedListing = ProjectManifest.Compute( manifestBase, directory =>
        Path.GetFileName( directory ) == "Assets"
            ? throw new UnauthorizedAccessException( "simulated permission denial" )
            : Directory.GetDirectories( directory ), out var deniedIncomplete );
    Assert( cleanListing == baseHash && !cleanIncomplete &&
        deniedListing == "" && deniedIncomplete,
        "an unreadable directory during enumeration yields the empty cannot-verify marker" );
}
finally
{
    RemoveManifestTree( manifestBase );
    RemoveManifestTree( manifestByteChange );
    RemoveManifestTree( manifestCollaborationState );
    RemoveManifestTree( manifestRenamed );
    RemoveManifestTree( manifestGit );
}

// ---- Task 1.3: pre-flight readiness checklist --------------------------------
// The dock renders this list before a session can start, so every rule is a pure function of
// the injected facts: no engine object is read, and a Warning must never block the host.
static ReadinessInput HealthyReadinessInput() => new ReadinessInput(
    SteamSignedIn: true,
    SteamPersona: "Tester",
    SceneSupported: true,
    ProjectPublished: true,
    PackageReachable: true,
    LibraryInstalled: true,
    LibraryInstalledVersion: new Version( 1, 0, 0 ),
    LibraryRequiredVersion: new Version( 1, 0, 0 ),
    IsOrgMember: true,
    Config: CollaborationConfig.Defaults(),
    ScenePath: "scenes/main.scene" );

static CollaborationConfig RelaxedConfig( Action<CollaborationConfig> relax )
{
    var config = CollaborationConfig.Defaults();
    relax( config );
    return config;
}

var healthyReadiness = ReadinessRules.Evaluate( HealthyReadinessInput() );
Assert( healthyReadiness.Select( x => x.Key ).SequenceEqual( new[] { "steam", "scene", "published", "library", "org" } ) &&
    healthyReadiness.All( x => x.Level == ReadinessLevel.Ok ) &&
    ReadinessRules.CanStart( healthyReadiness ) && ReadinessRules.BlockReason( healthyReadiness ) is null,
    "a fully ready project reports every checklist key as Ok in order and can start" );

var steamBlocked = ReadinessRules.Evaluate( HealthyReadinessInput() with { SteamSignedIn = false } );
Assert( steamBlocked.Single( x => x.Key == "steam" ).Level == ReadinessLevel.Blocked &&
    steamBlocked.Single( x => x.Key == "steam" ).Detail.Contains( "Sign in to Steam", StringComparison.Ordinal ) &&
    steamBlocked.Where( x => x.Key != "steam" ).All( x => x.Level == ReadinessLevel.Ok ) &&
    !ReadinessRules.CanStart( steamBlocked ) &&
    ReadinessRules.BlockReason( steamBlocked ) == "Hosting is unavailable: steam sign-in required.",
    "a signed-out Steam account blocks hosting and nothing else" );

Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with { SteamPersona = "Ada" } )
        .Single( x => x.Key == "steam" ).Detail == "Ada",
    "a signed-in Steam account reports the persona as its detail" );

var sceneBlocked = ReadinessRules.Evaluate( HealthyReadinessInput() with { SceneSupported = false, ScenePath = null } );
Assert( sceneBlocked.Single( x => x.Key == "scene" ).Level == ReadinessLevel.Blocked &&
    sceneBlocked.Single( x => x.Key == "scene" ).Detail.Contains( "saved scene under scenes/", StringComparison.Ordinal ) &&
    !ReadinessRules.CanStart( sceneBlocked ),
    "an unsupported scene blocks hosting" );
Assert( ReadinessRules.Evaluate( HealthyReadinessInput() ).Single( x => x.Key == "scene" ).Detail == "scenes/main.scene",
    "a supported scene reports the scene path as its detail" );

var unpublishedBlocked = ReadinessRules.Evaluate( HealthyReadinessInput() with { ProjectPublished = false } );
Assert( unpublishedBlocked.Single( x => x.Key == "published" ).Level == ReadinessLevel.Blocked &&
    unpublishedBlocked.Single( x => x.Key == "published" ).Detail.Contains( "Publish this project to s&box", StringComparison.Ordinal ) &&
    !ReadinessRules.CanStart( unpublishedBlocked ),
    "an unpublished project blocks hosting when publication is required" );

var unreachableWarning = ReadinessRules.Evaluate( HealthyReadinessInput() with { PackageReachable = false } );
Assert( unreachableWarning.Single( x => x.Key == "published" ).Level == ReadinessLevel.Warning &&
    unreachableWarning.Single( x => x.Key == "published" ).Detail.Contains( "could not be fetched from Asset Party", StringComparison.Ordinal ) &&
    ReadinessRules.CanStart( unreachableWarning ) && ReadinessRules.BlockReason( unreachableWarning ) is null,
    "an unfetchable published copy warns without blocking the host" );

Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with
        { ProjectPublished = false, Config = RelaxedConfig( x => x.RequirePublished = false ) } )
        .Single( x => x.Key == "published" ).Level == ReadinessLevel.Ok,
    "publication is Ok when the project does not require it" );

var missingLibrary = ReadinessRules.Evaluate( HealthyReadinessInput() with { LibraryInstalled = false } );
Assert( missingLibrary.Single( x => x.Key == "library" ).Level == ReadinessLevel.Blocked &&
    missingLibrary.Single( x => x.Key == "library" ).Detail.Contains( "Install the collaboration library from Asset Party.", StringComparison.Ordinal ) &&
    !ReadinessRules.CanStart( missingLibrary ),
    "a missing collaboration library blocks hosting" );

var outdatedLibrary = ReadinessRules.Evaluate( HealthyReadinessInput() with
{
    LibraryInstalledVersion = new Version( 0, 9, 0 ),
    LibraryRequiredVersion = new Version( 1, 1, 0 )
} );
Assert( outdatedLibrary.Single( x => x.Key == "library" ).Level == ReadinessLevel.Blocked &&
    outdatedLibrary.Single( x => x.Key == "library" ).Detail.Contains( "0.9.0", StringComparison.Ordinal ) &&
    outdatedLibrary.Single( x => x.Key == "library" ).Detail.Contains( "1.1.0", StringComparison.Ordinal ) &&
    outdatedLibrary.Single( x => x.Key == "library" ).Fix == "Update" &&
    ReadinessRules.BlockReason( outdatedLibrary ) == "Hosting is unavailable: library update required.",
    "an outdated library blocks hosting, names both versions, and offers the Update fix" );

Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with { LibraryInstalledVersion = null } )
        .Single( x => x.Key == "library" ).Level == ReadinessLevel.Blocked,
    "an unknown installed version is treated as older than any requirement" );

Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with { LibraryRequiredVersion = null } )
        .Single( x => x.Key == "library" ).Level == ReadinessLevel.Ok &&
    ReadinessRules.Evaluate( HealthyReadinessInput() ).Single( x => x.Key == "library" ).Detail.Contains( "1.0.0", StringComparison.Ordinal ),
    "an equal or unconstrained library version is Ok and reports the installed version" );

var strangerOrg = ReadinessRules.Evaluate( HealthyReadinessInput() with { IsOrgMember = false } );
Assert( strangerOrg.Single( x => x.Key == "org" ).Level == ReadinessLevel.Blocked &&
    strangerOrg.Single( x => x.Key == "org" ).Detail.Contains( "cyn", StringComparison.Ordinal ) &&
    !ReadinessRules.CanStart( strangerOrg ),
    "a non-member of the organization blocks hosting when membership is required" );

Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with
        { IsOrgMember = false, Config = RelaxedConfig( x => x.RequireOrgMember = false ) } )
        .Single( x => x.Key == "org" ).Level == ReadinessLevel.Ok,
    "organization membership is Ok when the project does not require it" );

Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with { Config = null, IsOrgMember = false } )
        .Single( x => x.Key == "org" ).Level == ReadinessLevel.Blocked,
    "an absent config falls back to the fail-closed defaults instead of throwing" );

// The block reason must name the FIRST blocked item, which is the one the dock highlights.
var everythingBlocked = ReadinessRules.Evaluate( HealthyReadinessInput() with
{
    SteamSignedIn = false,
    SceneSupported = false,
    LibraryInstalled = false,
    IsOrgMember = false
} );
Assert( ReadinessRules.BlockReason( everythingBlocked ) == "Hosting is unavailable: steam sign-in required." &&
    ReadinessRules.CanStart( everythingBlocked ) == false,
    "the block reason names the first blocked checklist item in order" );

// "No checks ran" must never be read as "may start": hosting stays refused until
// something has actually been evaluated.
Assert( !ReadinessRules.CanStart( null ),
    "an absent readiness checklist does not allow hosting" );

Assert( !ReadinessRules.CanStart( Array.Empty<ReadinessItem>() ),
    "an empty readiness checklist does not allow hosting" );

// A missing build or revision component is not a different release: System.Version would
// order 1.2 below 1.2.0, and a handshake must not refuse a peer over a trailing zero.
Assert( ReadinessRules.Evaluate( HealthyReadinessInput() with
        { LibraryInstalledVersion = new Version( 1, 2 ), LibraryRequiredVersion = new Version( 1, 2, 0 ) } )
        .Single( x => x.Key == "library" ).Level == ReadinessLevel.Ok,
    "an installed 1.2 satisfies a required 1.2.0 because trailing zero components are not a difference" );

// The shared settings file is the authoritative statement of the requirement, so readiness
// must reach the same verdict whether the caller passes the value or leaves it to the config.
var sharedLibraryRequirement = CollaborationConfig.Defaults();
sharedLibraryRequirement.LibraryVersion = new Version( 1, 2, 0 );
var requiredFromConfig = ReadinessRules.Evaluate( HealthyReadinessInput() with
    { Config = sharedLibraryRequirement, LibraryRequiredVersion = null } );
var requiredFromInput = ReadinessRules.Evaluate( HealthyReadinessInput() with
    { Config = sharedLibraryRequirement, LibraryRequiredVersion = sharedLibraryRequirement.LibraryVersion } );
Assert( requiredFromConfig.Single( x => x.Key == "library" ) == requiredFromInput.Single( x => x.Key == "library" ) &&
    requiredFromConfig.Single( x => x.Key == "library" ).Level == ReadinessLevel.Blocked &&
    requiredFromConfig.Single( x => x.Key == "library" ).Detail.Contains( "1.2.0", StringComparison.Ordinal ),
    "the required library version comes from one source: both routes yield the same checklist item" );

// ---- Task 1.4: host-side admission gate order --------------------------------
// Admission is a fixed sequence of pure checks over facts the caller already gathered, so the
// refusal and the failed gate are the same on every host and the caller can log the gate
// without parsing prose.
static PeerFacts AdmittedPeerFacts() => new PeerFacts(
    SteamId: "76561198000000001",
    IdentityVerified: true,
    IsFriend: true,
    IsOrgMember: true,
    PeerLibraryVersion: "1.0.0",
    PeerProjectManifest: "abcd1234",
    PeerOrgIdent: "cyn" );

static CollaborationConfig RequiringLibraryConfig( string version )
{
    var config = CollaborationConfig.Defaults();
    config.LibraryVersion = new Version( version );
    return config;
}

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts(), CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var admittedGate ) is null &&
    admittedGate is null,
    "a fully qualified peer is admitted with no failed gate" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IdentityVerified = false },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var identityGate ) ==
        "could not be verified by s&box's auth service." &&
    identityGate == AdmissionRules.VerifiedGate,
    "an unverified peer is refused at the identity gate" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IdentityVerified = false },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out _, peerName: "Ada" ) ==
        "Ada could not be verified by s&box's auth service.",
    "a supplied display name prefixes the refusal sentence" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var friendGate ) ==
        "is not on your Steam friends list. Add them on Steam and reconnect." &&
    friendGate == AdmissionRules.FriendGate,
    "a stranger is refused at the friend gate when friends are required" );

// Hosting dials the host's OWN listener (StartCore self-dial), so the loopback peer
// carries the host's account — and no account is a friend of itself. The friend gate
// must not refuse the hosting editor's own connection.
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false, IsHostSelf = true },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var selfFriendGate ) is null &&
    selfFriendGate is null,
    "the host's own loopback connection passes the friend gate without being a friend" );

// The exemption yields the FRIEND gate and nothing else: every later gate still speaks
// for itself, which is what keeps "this peer is the host" from becoming "trusted".
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false, IsHostSelf = true, IdentityVerified = false },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var selfIdentityGate ) is not null &&
    selfIdentityGate == AdmissionRules.VerifiedGate,
    "the host's own connection is still refused at the identity gate when it is unverified" );
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false, IsHostSelf = true, IsOrgMember = false },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var selfOrgGate ) is not null &&
    selfOrgGate == AdmissionRules.OrgGate,
    "the host's own connection is still refused at the org gate when it is not a member" );
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false, IsHostSelf = true },
        RequiringLibraryConfig( "1.1.0" ), "1.1.0", "abcd1234", out var selfLibraryGate ) is not null &&
    selfLibraryGate == AdmissionRules.LibraryGate,
    "the host's own connection is still refused at the library gate on an old library" );
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false, IsHostSelf = true, PeerProjectManifest = "ffff9999" },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var selfManifestGate ) is not null &&
    selfManifestGate == AdmissionRules.ManifestGate,
    "the host's own connection is still refused at the manifest gate on a different project copy" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsOrgMember = false },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var orgGate ) ==
        "is not a member of the organization cyn." &&
    orgGate == AdmissionRules.OrgGate,
    "a non-member is refused at the org gate when membership is required" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerOrgIdent = "rival" },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var orgMismatchGate ) ==
        "is not a member of the organization cyn." &&
    orgMismatchGate == AdmissionRules.OrgGate,
    "a peer reporting a different organization is refused at the org gate" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts(), RequiringLibraryConfig( "1.1.0" ), "1.1.0", "abcd1234", out var libraryGate ) ==
        "runs collaboration library 1.0.0; this session requires 1.1.0. Update the library and reconnect." &&
    libraryGate == AdmissionRules.LibraryGate,
    "an older peer library is refused at the library gate and names both versions" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerLibraryVersion = null },
        RequiringLibraryConfig( "1.1.0" ), "1.1.0", "abcd1234", out var missingLibraryGate ) ==
        "runs collaboration library an unknown version; this session requires 1.1.0. Update the library and reconnect." &&
    missingLibraryGate == AdmissionRules.LibraryGate,
    "a peer reporting no library version is refused at the library gate" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerLibraryVersion = "2.0.0" },
        RequiringLibraryConfig( "1.1.0" ), "1.1.0", "abcd1234", out var newerPeerGate ) is null &&
    newerPeerGate is null,
    "a newer peer library is accepted because a more recent peer is compatible" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerProjectManifest = "ffff9999" },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var manifestGate ) ==
        "has a different copy of this project (ffff9999 vs abcd1234). Sync the project (Git and Asset Party) before joining." &&
    manifestGate == AdmissionRules.ManifestGate,
    "a mismatched project copy is refused at the manifest gate and names both hashes" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerProjectManifest = "ffff9999" },
        CollaborationConfig.Defaults(), "1.0.0", "", out var unenforcedManifestGate ) is null &&
    unenforcedManifestGate is null,
    "the manifest gate is not enforced when the host has no manifest to compare" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { IsFriend = false, IsOrgMember = false },
        null, "1.0.0", "abcd1234", out var unenforcedConfigGate ) is null &&
    unenforcedConfigGate is null,
    "an absent config enforces nothing beyond identity and the manifest" );

// Several facts can be wrong at once; the refusal must always be the FIRST failing gate.
var admissionAllWrong = AdmittedPeerFacts() with
{
    IdentityVerified = false,
    IsFriend = false,
    IsOrgMember = false,
    PeerLibraryVersion = "0.9.0",
    PeerProjectManifest = "ffff9999"
};
var admissionConfig = RequiringLibraryConfig( "1.0.0" );
Assert( AdmissionRules.Evaluate( admissionAllWrong, admissionConfig, "1.0.0", "abcd1234", out var orderedGate ) != null &&
    orderedGate == AdmissionRules.VerifiedGate,
    "identity is evaluated before every other gate" );
Assert( AdmissionRules.Evaluate( admissionAllWrong with { IdentityVerified = true }, admissionConfig, "1.0.0", "abcd1234", out orderedGate ) != null &&
    orderedGate == AdmissionRules.FriendGate,
    "friend is evaluated before org, library, and manifest" );
Assert( AdmissionRules.Evaluate( admissionAllWrong with { IdentityVerified = true, IsFriend = true }, admissionConfig, "1.0.0", "abcd1234", out orderedGate ) != null &&
    orderedGate == AdmissionRules.OrgGate,
    "org is evaluated before library and manifest" );
Assert( AdmissionRules.Evaluate( admissionAllWrong with { IdentityVerified = true, IsFriend = true, IsOrgMember = true },
        admissionConfig, "1.0.0", "abcd1234", out orderedGate ) != null &&
    orderedGate == AdmissionRules.LibraryGate,
    "library is evaluated before manifest" );
Assert( AdmissionRules.Evaluate( admissionAllWrong with
        { IdentityVerified = true, IsFriend = true, IsOrgMember = true, PeerLibraryVersion = "1.0.0" },
        admissionConfig, "1.0.0", "abcd1234", out orderedGate ) != null &&
    orderedGate == AdmissionRules.ManifestGate,
    "manifest is the final gate" );

// The same trailing-zero rule applies on the admission side: a peer that reports 1.1 runs
// the release the settings file pins as 1.1.0.
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerLibraryVersion = "1.1" },
        RequiringLibraryConfig( "1.1.0" ), "1.1.0", "abcd1234", out var shortVersionGate ) is null &&
    shortVersionGate is null,
    "a peer reporting 1.1 satisfies a session requiring 1.1.0" );

// When the shared settings file pins no version, the host's own library version is the
// requirement, so hostLibraryVersion is read rather than merely accepted.
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerLibraryVersion = "1.0.0" },
        CollaborationConfig.Defaults(), "2.0.0", "abcd1234", out var hostVersionGate ) ==
        "runs collaboration library 1.0.0; this session requires 2.0.0. Update the library and reconnect." &&
    hostVersionGate == AdmissionRules.LibraryGate,
    "hostLibraryVersion drives the library gate when config.LibraryVersion is null" );

Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerLibraryVersion = "2.0.0" },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var matchingHostVersionGate ) is null &&
    matchingHostVersionGate is null,
    "the host's own library version admits a peer that reports at least that version" );

// Organization idents are the same organization whatever the capitalization on each side.
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts() with { PeerOrgIdent = "CYN" },
        CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var caseOnlyOrgGate ) is null &&
    caseOnlyOrgGate is null,
    "an organization ident differing only in case is the same organization" );

// Peer-supplied text reaches these refusal sentences, so every interpolated peer value is
// bounded and stripped of control characters before it is composed: a name that is 5000
// characters long, or one carrying a line break, must not become a 5000-character or
// multi-line log entry.
var oversizedPeerName = new string( 'n', 5000 ) + "\r\nsecond line";
var boundedNameRefusal = AdmissionRules.Evaluate( AdmittedPeerFacts() with { IdentityVerified = false },
    CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var boundedNameGate, peerName: oversizedPeerName );
Assert( boundedNameRefusal.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    !boundedNameRefusal.Contains( '\r' ) && !boundedNameRefusal.Contains( '\n' ) &&
    boundedNameGate == AdmissionRules.VerifiedGate,
    "a 5000-character peer name yields a bounded single-line refusal" );

var oversizedVersionRefusal = AdmissionRules.Evaluate( AdmittedPeerFacts() with
        { PeerLibraryVersion = new string( 'v', 5000 ) },
    RequiringLibraryConfig( "1.1.0" ), "1.1.0", "abcd1234", out var oversizedVersionGate );
Assert( oversizedVersionRefusal is not null &&
    oversizedVersionRefusal.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    !oversizedVersionRefusal.Contains( '\r' ) && !oversizedVersionRefusal.Contains( '\n' ) &&
    oversizedVersionGate == AdmissionRules.LibraryGate,
    "an oversized peer library version is bounded in the refusal" );

var oversizedManifestRefusal = AdmissionRules.Evaluate( AdmittedPeerFacts() with
        { PeerProjectManifest = new string( 'h', 5000 ) },
    CollaborationConfig.Defaults(), "1.0.0", "abcd1234", out var oversizedManifestGate );
Assert( oversizedManifestRefusal is not null &&
    oversizedManifestRefusal.Length <= CollaborationDiagnostics.MaxDiagnosticLength &&
    !oversizedManifestRefusal.Contains( '\r' ) && !oversizedManifestRefusal.Contains( '\n' ) &&
    oversizedManifestGate == AdmissionRules.ManifestGate,
    "an oversized peer project manifest is bounded in the refusal" );

// A blank host manifest is a project that never produced one, so there is nothing to
// compare and the gate stays open instead of refusing every peer over an empty value.
Assert( AdmissionRules.Evaluate( AdmittedPeerFacts(), CollaborationConfig.Defaults(),
        "1.0.0", "   ", out var blankManifestGate ) is null &&
    blankManifestGate is null,
    "a whitespace-only host manifest counts as no manifest and is not enforced" );

// Task: discovery wire format. A datagram arrives from an unknown machine on the local
// network, so parsing is total and fail-closed: every malformed shape must be refused with
// false rather than interpreted, and no input may throw.
string GoodDiscoveryReply() => "TC1!pid=deadbeef;port=32123;name=box;secret=00112233445566778899aabbccddeeff;rev=0a1b2c3d\n";
const string discoverySecret = "00112233445566778899aabbccddeeff";
var discoveryQuery = DiscoveryProtocol.BuildQuery( "deadbeef" );
Assert( discoveryQuery == "TC1?pid=deadbeef\n" &&
    DiscoveryProtocol.TryParseQuery( discoveryQuery, out var discoveryPid ) && discoveryPid == "deadbeef",
    "a built discovery query round-trips" );
Assert( !DiscoveryProtocol.TryParseQuery( null, out _ ) && !DiscoveryProtocol.TryParseQuery( "", out _ ) &&
    !DiscoveryProtocol.TryParseQuery( "TC1?pid=DEADBEEF\n", out _ ) &&
    !DiscoveryProtocol.TryParseQuery( "TC1?pid=deadbee\n", out _ ) &&
    !DiscoveryProtocol.TryParseQuery( "TC1?pid=deadbeef1\n", out _ ) &&
    !DiscoveryProtocol.TryParseQuery( "TC1?pid=deadbeef;port=1\n", out _ ) &&
    !DiscoveryProtocol.TryParseQuery( "TC2?pid=deadbeef\n", out _ ) &&
    !DiscoveryProtocol.TryParseQuery( "TC1?port=1\n", out _ ),
    "a query is accepted only in its exact single-key lowercase form" );
var discoveryReply = DiscoveryProtocol.BuildReply( "deadbeef", 32123, "build-box", discoverySecret, "0a1b2c3d" );
Assert( discoveryReply == "TC1!pid=deadbeef;port=32123;name=build-box;secret=00112233445566778899aabbccddeeff;rev=0a1b2c3d\n" &&
    DiscoveryProtocol.TryParseReply( discoveryReply, out var builtReply ) &&
    builtReply.ProjectIdHash8 == "deadbeef" && builtReply.SessionPort == 32123 && builtReply.HostName == "build-box" &&
    builtReply.SecretHex == discoverySecret && builtReply.Manifest8 == "0a1b2c3d",
    "a built discovery reply round-trips every field in its fixed key order" );
var hostileNameReply = DiscoveryProtocol.BuildReply( "deadbeef", 1, "a;b\npid=00000000", discoverySecret, "0a1b2c3d" );
Assert( DiscoveryProtocol.TryParseReply( hostileNameReply, out var hostileName ) &&
    !hostileName.HostName.Contains( ';' ) && !hostileName.HostName.Contains( '\n' ) && hostileName.SessionPort == 1,
    "a hostile host name cannot corrupt the message it is embedded in" );
var longNameReply = DiscoveryProtocol.BuildReply( "deadbeef", 1, new string( 'n', 300 ), discoverySecret, "0a1b2c3d" );
Assert( DiscoveryProtocol.TryParseReply( longNameReply, out var truncatedName ) && truncatedName.HostName.Length == 64,
    "an over-long host name is truncated to the parsed limit and still round-trips" );
var goodDiscovery = GoodDiscoveryReply();
Assert( !DiscoveryProtocol.TryParseReply( null, out var absentReply ) && absentReply is null &&
    !DiscoveryProtocol.TryParseReply( "", out _ ) &&
    !DiscoveryProtocol.TryParseReply( "\n", out _ ) &&
    !DiscoveryProtocol.TryParseReply( "XX1!" + goodDiscovery.Substring( 4 ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( "TC1?" + goodDiscovery.Substring( 4 ), out _ ),
    "absent, empty, misprefixed, and wrong-kind datagrams are refused" );
Assert( !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "pid=deadbeef;", "" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( ";port=32123", "" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( ";name=box", "" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( ";" + "secret=" + discoverySecret, "" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( ";rev=0a1b2c3d", "" ), out _ ),
    "a reply missing any one of its required keys is refused" );
Assert( !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=abc" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port= 5" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=+5" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=0" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=-1" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=70000" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "port=32123", "port=99999999999999999999" ), out _ ),
    "ports that are non-numeric, signed, blank, zero, negative, or above 65535 are refused" );
Assert( !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "pid=deadbeef", "pid=DEADBEEF" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "pid=deadbeef", "pid=deadbee" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "pid=deadbeef", "pid=deadbeef0" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "rev=0a1b2c3d", "rev=0A1B2C3D" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "rev=0a1b2c3d", "rev=0a1b2c3" ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( discoverySecret, discoverySecret.ToUpperInvariant() ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( discoverySecret, discoverySecret.Substring( 0, 31 ) ), out _ ),
    "project, revision, and secret fields must be exactly 8, 8, and 32 lowercase hex characters" );
Assert( !DiscoveryProtocol.TryParseReply( "TC1!pid=deadbeef;port=1;name=a\nb;secret=" + discoverySecret + ";rev=0a1b2c3d", out var newlineName ) && newlineName is null &&
    !DiscoveryProtocol.TryParseReply( "TC1!pid=00000000;port=1;name=a;b\npid=00000000;secret=" + discoverySecret + ";rev=0a1b2c3d", out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "name=box", "name=" + new string( 'n', 65 ) ), out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "name=box", "name=" + new string( 'n', 64 ) + "x" ), out _ ) &&
    DiscoveryProtocol.TryParseReply( goodDiscovery.Replace( "name=box", "name=" + new string( 'n', 64 ) ), out var maxLengthName ) &&
    maxLengthName.HostName.Length == 64,
    "a name with a control character, a smuggled key, or more than 64 characters is refused" );
Assert( DiscoveryProtocol.TryParseReply( goodDiscovery.Substring( 0, goodDiscovery.Length - 1 ) + ";extra=ignored\n", out var unknownKeyReply ) &&
    unknownKeyReply.SessionPort == 32123 &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Substring( 0, goodDiscovery.Length - 1 ) + ";pid=00000000\n", out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Substring( 0, goodDiscovery.Length - 1 ) + ";novalue\n", out _ ) &&
    !DiscoveryProtocol.TryParseReply( goodDiscovery.Substring( 0, goodDiscovery.Length - 1 ) + ";\n", out _ ),
    "unknown well-formed keys are ignored while duplicated and valueless segments are refused" );
var discoveryAtLimit = goodDiscovery.Substring( 0, goodDiscovery.Length - 1 ) + ";pad=" + new string( 'x', 512 - goodDiscovery.Length - 5 ) + "\n";
Assert( discoveryAtLimit.Length == 512 &&
    DiscoveryProtocol.TryParseReply( discoveryAtLimit, out _ ) &&
    !DiscoveryProtocol.TryParseReply( discoveryAtLimit + "x", out _ ),
    "the 512-byte datagram cap is enforced exactly" );

// Task: optional per-session transport encryption. Both ends derive the key from the invite
// secret before connecting, so every frame travels sealed — including the hello — and a key
// that does not match must surface as one specific failure rather than a hang or a packet
// nobody can read.
static async Task<Wire> AcceptedWireAsync( Listener listener )
{
    var deadline = DateTime.UtcNow.AddSeconds( 5 );
    while ( DateTime.UtcNow < deadline )
    {
        if ( listener.Accepted.TryDequeue( out var accepted ) ) return accepted;
        await Task.Delay( 10 );
    }
    return null;
}
static async Task<Packet> NextPacketAsync( Wire wire )
{
    var deadline = DateTime.UtcNow.AddSeconds( 5 );
    while ( DateTime.UtcNow < deadline )
    {
        if ( wire.Incoming.TryDequeue( out var packet ) ) return packet;
        await Task.Delay( 10 );
    }
    return null;
}
static async Task<string> FailureOfAsync( Wire wire )
{
    var deadline = DateTime.UtcNow.AddSeconds( 5 );
    while ( wire.Failure is null && DateTime.UtcNow < deadline ) await Task.Delay( 10 );
    return wire.Failure;
}
// One length-prefixed frame written by hand: how a peer that is not a Wire — an older build,
// a plaintext endpoint, a probe — presents itself to a keyed host.
static async Task SendRawFrameAsync( TcpClient socket, byte[] body )
{
    var stream = socket.GetStream();
    var header = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian( header, body.Length );
    await stream.WriteAsync( header ).AsTask().WaitAsync( TimeSpan.FromSeconds( 5 ) );
    await stream.WriteAsync( body ).AsTask().WaitAsync( TimeSpan.FromSeconds( 5 ) );
}
// True when the far end has closed: the read either returns nothing or faults. A peer that
// refuses a session says nothing at all, so the close is the only signal it leaves behind.
static async Task<bool> PeerClosedAsync( TcpClient socket )
{
    try
    {
        var probe = new byte[8];
        return await socket.GetStream().ReadAsync( probe, 0, probe.Length ).WaitAsync( TimeSpan.FromSeconds( 5 ) ) == 0;
    }
    catch ( IOException ) { return true; }
}
// Everything a keyed dialer puts on the wire: the preamble frame, then the first frame it
// seals under the key that preamble names. A raw listener stands in for the accepting side
// so the exact bytes two connections produce can be laid side by side.
static async Task<(byte[] Preamble, byte[] Frame)> CaptureHandshakeAsync( byte[] baseKey, Packet packet )
{
    var listener = new TcpListener( IPAddress.Loopback, GetFreeLoopbackPort() );
    listener.Start();
    try
    {
        var port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        using var wire = await Wire.Connect( "127.0.0.1", port, baseKey, initiator: true );
        wire.Send( packet );
        using var raw = await listener.AcceptTcpClientAsync().WaitAsync( TimeSpan.FromSeconds( 5 ) );
        var stream = raw.GetStream();
        var preamble = await ReadRawFrameAsync( stream );
        return (preamble, await ReadRawFrameAsync( stream ));
    }
    finally { listener.Stop(); }
}
static async Task<byte[]> ReadRawFrameAsync( NetworkStream stream )
{
    var header = new byte[4];
    await stream.ReadExactlyAsync( header ).AsTask().WaitAsync( TimeSpan.FromSeconds( 5 ) );
    var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian( header )];
    await stream.ReadExactlyAsync( body ).AsTask().WaitAsync( TimeSpan.FromSeconds( 5 ) );
    return body;
}
byte[] SessionKey( byte seed ) => Enumerable.Repeat( seed, 32 ).ToArray();
const string decryptionWording = "invite code or session key does not match";
var sessionKey = SessionKey( 0x42 );
var mismatchedKey = SessionKey( 0x43 );

// Two wires over a connected pair with the same key and opposite initiator flags exchange
// packets both ways, over more than one frame each so the counters are exercised repeatedly.
var exchangePort = GetFreeLoopbackPort();
using var exchangeListener = new Listener( "127.0.0.1", exchangePort, sessionKey );
using var exchangeClient = await Wire.Connect( "127.0.0.1", exchangePort, sessionKey, initiator: true );
var exchangeHost = await AcceptedWireAsync( exchangeListener );
Assert( exchangeHost is not null, "an encrypted session accepts a connection" );
using ( exchangeHost )
{
    for ( var i = 0; i < 3; i++ ) exchangeClient.Send( new Packet { Kind = "hello", Peer = "client-" + i } );
    var received = new List<string>();
    for ( var i = 0; i < 3; i++ ) received.Add( ( await NextPacketAsync( exchangeHost ) )?.Peer );
    if ( exchangeHost.Failure is not null || exchangeClient.Failure is not null || received.Any( p => p is null ) )
        Console.WriteLine( $"INFO: encrypted exchange failure client=[{exchangeClient.Failure}] host=[{exchangeHost.Failure}] received=[{string.Join( "|", received )}]" );
    Assert( received.SequenceEqual( new[] { "client-0", "client-1", "client-2" } ) && exchangeHost.Failure is null,
        "sealed frames open in order across repeated messages" );
    exchangeHost.Send( new Packet { Kind = "welcome", Peer = "host" } );
    var hostReply = await NextPacketAsync( exchangeClient );
    Assert( hostReply?.Kind == "welcome" && exchangeClient.Failure is null && exchangeClient.Incoming.IsEmpty,
        "the host's sealed reply opens on the initiator in the opposite direction" );
}

// The direction tag inside the nonce is what keeps one end from reading its own stream back
// as a peer's, and BytesSealed proves the counter is per instance and monotonic.
var sealedProbe = new AeadFraming( sessionKey, initiator: true );
var openProbe = new AeadFraming( sessionKey, initiator: false );
var probeFrameOne = sealedProbe.Seal( System.Text.Encoding.UTF8.GetBytes( "one" ) );
var probeAfterFirst = sealedProbe.BytesSealed;
var probeFrameTwo = sealedProbe.Seal( System.Text.Encoding.UTF8.GetBytes( "two" ) );
var probeFrameThree = sealedProbe.Seal( System.Text.Encoding.UTF8.GetBytes( "three" ) );
Assert( probeAfterFirst == 3 && sealedProbe.BytesSealed == 11 &&
    probeFrameOne.Length == 3 + AeadFraming.FrameOverhead &&
    System.Text.Encoding.UTF8.GetString( openProbe.Open( probeFrameOne ) ) == "one" &&
    System.Text.Encoding.UTF8.GetString( openProbe.Open( probeFrameTwo ) ) == "two" &&
    System.Text.Encoding.UTF8.GetString( openProbe.Open( probeFrameThree ) ) == "three",
    "the framing counter advances across messages and the opposite direction opens every one" );
try { new AeadFraming( sessionKey, initiator: true ).Open( probeFrameOne ); Assert( false, "a frame sealed with the same direction tag is refused" ); }
catch ( System.Security.Cryptography.CryptographicException ) { Assert( true, "a frame sealed with the same direction tag is refused" ); }

// A peer holding a different key proves the failure is specific and that nothing it sent is
// ever enqueued as a packet.
var wrongKeyPort = GetFreeLoopbackPort();
using var wrongKeyListener = new Listener( "127.0.0.1", wrongKeyPort, sessionKey );
using var wrongKeyClient = await Wire.Connect( "127.0.0.1", wrongKeyPort, mismatchedKey, initiator: true );
var wrongKeyHost = await AcceptedWireAsync( wrongKeyListener );
Assert( wrongKeyHost is not null, "a connection with a mismatched key is still accepted at the socket" );
using ( wrongKeyHost )
{
    wrongKeyClient.Send( new Packet { Kind = "hello", Peer = "mismatched" } );
    var mismatchFailure = await FailureOfAsync( wrongKeyHost );
    Assert( mismatchFailure is not null && mismatchFailure.Contains( decryptionWording ) && wrongKeyHost.Incoming.IsEmpty,
        "a wrong session key produces the specific decryption failure and enqueues no packet" );
}

// A key present on only one end must fail the same specific way in both directions instead
// of hanging while one side waits for plaintext the other side sealed.
var keyedReceiverPort = GetFreeLoopbackPort();
using var keyedReceiverListener = new Listener( "127.0.0.1", keyedReceiverPort, sessionKey );
using var clearSender = await Wire.Connect( "127.0.0.1", keyedReceiverPort );
var keyedReceiver = await AcceptedWireAsync( keyedReceiverListener );
Assert( keyedReceiver is not null, "a keyed host accepts a plaintext peer at the socket" );
using ( keyedReceiver )
{
    clearSender.Send( new Packet { Kind = "hello", Peer = "cleartext" } );
    var clearFailure = await FailureOfAsync( keyedReceiver );
    Assert( clearFailure is not null && clearFailure.Contains( decryptionWording ) && keyedReceiver.Incoming.IsEmpty,
        "a plaintext frame arriving at a keyed endpoint fails with the specific decryption failure" );
}
var clearReceiverPort = GetFreeLoopbackPort();
using var clearReceiverListener = new Listener( "127.0.0.1", clearReceiverPort );
using var sealedSender = await Wire.Connect( "127.0.0.1", clearReceiverPort, sessionKey, initiator: true );
var clearReceiver = await AcceptedWireAsync( clearReceiverListener );
Assert( clearReceiver is not null, "a plaintext host accepts a keyed peer at the socket" );
using ( clearReceiver )
{
    sealedSender.Send( new Packet { Kind = "hello", Peer = "sealed" } );
    var sealedFailure = await FailureOfAsync( clearReceiver );
    Assert( sealedFailure is not null && sealedFailure.Contains( decryptionWording ) && clearReceiver.Incoming.IsEmpty,
        "a sealed frame arriving at a plaintext endpoint fails with the same decryption failure" );
}

// A single flipped bit on the wire must be caught by the authentication tag before any
// deserialization happens; the frame is forged here because a Wire never emits one.
var tamperedPort = GetFreeLoopbackPort();
using var tamperedListener = new Listener( "127.0.0.1", tamperedPort, sessionKey );
using var tamperedRawSocket = new TcpClient();
await tamperedRawSocket.ConnectAsync( IPAddress.Loopback, tamperedPort );
var tamperedHost = await AcceptedWireAsync( tamperedListener );
Assert( tamperedHost is not null, "a raw socket connection to a keyed host is accepted" );
using ( tamperedHost )
{
    var forged = new AeadFraming( sessionKey, initiator: true ).Seal( System.Text.Encoding.UTF8.GetBytes( "{\"kind\":\"hello\",\"peer\":\"forged\"}" ) );
    forged[forged.Length - 1] ^= 0x5a;
    var forgedHeader = new byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian( forgedHeader, forged.Length );
    var rawStream = tamperedRawSocket.GetStream();
    await rawStream.WriteAsync( forgedHeader );
    await rawStream.WriteAsync( forged );
    var tamperFailure = await FailureOfAsync( tamperedHost );
    Assert( tamperFailure is not null && tamperFailure.Contains( decryptionWording ) && tamperedHost.Incoming.IsEmpty,
        "a tampered frame is rejected with the same specific failure and enqueues no packet" );

// Task: a failure both ends can see, and a reconnect that cannot reuse a nonce. Three
// shapes matter here: the joiner holding the wrong invite key, the peer that never speaks
// the encrypted handshake, and two connections sharing one session key.

// The wrong-key joiner is the operator this transport is written for. The host's decryption
// failure closes the socket, so the joiner reports a closed connection instead of carrying
// on until its queue fills and blaming the queue for a session that never existed.
var wrongBothPort = GetFreeLoopbackPort();
using var wrongBothListener = new Listener( "127.0.0.1", wrongBothPort, sessionKey );
using var wrongBothJoiner = await Wire.Connect( "127.0.0.1", wrongBothPort, mismatchedKey, initiator: true );
var wrongBothHost = await AcceptedWireAsync( wrongBothListener );
Assert( wrongBothHost is not null, "a joiner holding a different key reaches the host at the socket" );
using ( wrongBothHost )
{
    wrongBothJoiner.Send( new Packet { Kind = "hello", Peer = "wrong-key-joiner" } );
    var wrongBothHostFailure = await FailureOfAsync( wrongBothHost );
    var wrongBothJoinerFailure = await FailureOfAsync( wrongBothJoiner );
    Assert( wrongBothHostFailure is not null && wrongBothHostFailure.Contains( decryptionWording ) && wrongBothHost.Incoming.IsEmpty,
        "the host whose key does not match reports the specific decryption failure" );
    Assert( wrongBothJoinerFailure is not null && !wrongBothJoinerFailure.Contains( "queue or message limit" ),
        "the wrong-key joiner learns that the session failed rather than that its queue is full" );
    Assert( wrongBothJoinerFailure == Wire.PeerClosedFailure,
        "the wrong-key joiner's own message names the closure instead of raw socket text" );
}

// A peer that sends a preamble from some other protocol cannot have derived this connection's
// key, so the host refuses it before anything is deserialized and closes — the close is the
// only signal the sender gets. This peer does send one; the peer that sends nothing at all is
// covered further down.
var badPreamblePort = GetFreeLoopbackPort();
using var badPreambleListener = new Listener( "127.0.0.1", badPreamblePort, sessionKey );
using var badPreambleSocket = new TcpClient();
await badPreambleSocket.ConnectAsync( IPAddress.Loopback, badPreamblePort );
var badPreambleHost = await AcceptedWireAsync( badPreambleListener );
Assert( badPreambleHost is not null, "a raw socket reaches a keyed host and waits for its preamble" );
using ( badPreambleHost )
{
    var foreignPreamble = new byte[20];
    for ( var i = 0; i < foreignPreamble.Length; i++ ) foreignPreamble[i] = (byte)'X';
    await SendRawFrameAsync( badPreambleSocket, foreignPreamble );
    var foreignFailure = await FailureOfAsync( badPreambleHost );
    Assert( foreignFailure == Wire.PreambleFailure && badPreambleHost.Incoming.IsEmpty,
        "a preamble with the wrong magic fails with the protocol wording and enqueues nothing" );
}
Assert( await PeerClosedAsync( badPreambleSocket ),
    "the sender of a refused preamble learns the session is over from the close" );

using var shortPreambleSocket = new TcpClient();
await shortPreambleSocket.ConnectAsync( IPAddress.Loopback, badPreamblePort );
var shortPreambleHost = await AcceptedWireAsync( badPreambleListener );
Assert( shortPreambleHost is not null, "a second connection is accepted after the first was refused" );
using ( shortPreambleHost )
{
    await SendRawFrameAsync( shortPreambleSocket, new byte[] { (byte)'T', (byte)'C', (byte)'S', (byte)'1', 1, 2, 3, 4, 5 } );
    var shortFailure = await FailureOfAsync( shortPreambleHost );
    Assert( shortFailure == Wire.PreambleFailure && shortPreambleHost.Incoming.IsEmpty,
        "a preamble too short to carry a salt is refused with the same protocol wording" );
}
Assert( await PeerClosedAsync( shortPreambleSocket ),
    "the sender of a truncated preamble learns the session is over from the close" );

// A peer that connects and then never says a word, which is the shape the tests above only
// approximate: those send a foreign preamble, this one sends nothing at all. The accepting
// wire has no key until that preamble arrives, so a send it accepts is parked behind keyReady
// rather than sealed. What frees that parked write is pinned here instead of assumed — the
// peer's silence completes nothing and invents no diagnosis, the peer's close gives the
// acceptor its failure inside the same bounded deadline every other failure uses, and the
// parked write itself is released only by an external Dispose (SessionServer disposes a
// member whose StoredFailure is set), because a Wire never cancels itself for a peer closure.
static async Task<bool> ReceivedNothingWithinAsync( TcpClient socket, int milliseconds )
{
    try { return await socket.GetStream().ReadAsync( new byte[16], 0, 16 ).WaitAsync( TimeSpan.FromMilliseconds( milliseconds ) ) == 0; }
    catch ( TimeoutException ) { return true; }
}

var silentPort = GetFreeLoopbackPort();
using var silentListener = new Listener( "127.0.0.1", silentPort, sessionKey );
using var silentSocket = new TcpClient();
await silentSocket.ConnectAsync( IPAddress.Loopback, silentPort );
var silentHost = await AcceptedWireAsync( silentListener );
Assert( silentHost is not null, "a keyed host accepts a peer that sends no preamble at all" );
using ( silentHost )
{
    silentHost.Send( new Packet { Kind = "hello", Peer = "silent-dialer" } );
    Assert( await ReceivedNothingWithinAsync( silentSocket, 500 ) && silentHost.Failure is null && silentHost.Incoming.IsEmpty,
        "a send queued ahead of a preamble stays parked: nothing reaches the peer and no failure is invented" );

    silentSocket.Close();
    Assert( await FailureOfAsync( silentHost ) == Wire.PeerClosedFailure && silentHost.Incoming.IsEmpty,
        "the peer's close ends the silent connection with the clear peer-closed failure inside the bounded deadline" );

    // The close recorded the diagnosis but released nothing: keyReady can only be completed by
    // the preamble that never came, so the write loop is still parked at this point.
    var silentKeyReady = (System.Threading.Tasks.TaskCompletionSource)typeof( Wire )
        .GetField( "keyReady", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )
        ?.GetValue( silentHost );
    Assert( silentKeyReady is not null && !silentKeyReady.Task.IsCompleted,
        "the close does not release the parked write: only that peer's preamble could complete the key it waits on" );

    // Which is why the release is external and must be pinned as such: an owner that tears the
    // wire down cancels the very token the parked write is awaiting, so it cannot outlive the
    // teardown even though nothing ever completed its key.
    silentHost.Dispose();
    var silentStop = (System.Threading.CancellationTokenSource)typeof( Wire )
        .GetField( "stop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )
        ?.GetValue( silentHost );
    Assert( silentStop is { IsCancellationRequested: true },
        "an external Dispose cancels the token the parked write waits on, so the write loop cannot hang past teardown" );
}

// A keyed wire paired with a plaintext wire must fail on BOTH ends, each with a message its
// own operator can act on: one side holds a key the other does not, and neither may sit
// there believing it is connected.
var mixedPort = GetFreeLoopbackPort();
using var mixedListener = new Listener( "127.0.0.1", mixedPort, sessionKey );
using var mixedClearDialer = await Wire.Connect( "127.0.0.1", mixedPort );
var mixedKeyedHost = await AcceptedWireAsync( mixedListener );
Assert( mixedKeyedHost is not null, "a keyed host accepts a plaintext dialer at the socket" );
using ( mixedKeyedHost )
{
    mixedClearDialer.Send( new Packet { Kind = "hello", Peer = "plaintext-dialer" } );
    var mixedHostFailure = await FailureOfAsync( mixedKeyedHost );
    var mixedDialerFailure = await FailureOfAsync( mixedClearDialer );
    Assert( mixedHostFailure is not null && mixedHostFailure.Contains( decryptionWording ) && mixedKeyedHost.Incoming.IsEmpty,
        "a keyed host reads a plaintext packet as the key mistake it is" );
    Assert( mixedDialerFailure == Wire.PeerClosedFailure,
        "the plaintext dialer learns of the refusal from the close, with a reason it can read" );
}
var mixedSecondPort = GetFreeLoopbackPort();
using var mixedClearListener = new Listener( "127.0.0.1", mixedSecondPort );
using var mixedKeyedDialer = await Wire.Connect( "127.0.0.1", mixedSecondPort, sessionKey, initiator: true );
var mixedClearHost = await AcceptedWireAsync( mixedClearListener );
Assert( mixedClearHost is not null, "a plaintext host accepts a keyed dialer at the socket" );
using ( mixedClearHost )
{
    mixedKeyedDialer.Send( new Packet { Kind = "hello", Peer = "sealed-dialer" } );
    var mixedClearFailure = await FailureOfAsync( mixedClearHost );
    var mixedKeyedFailure = await FailureOfAsync( mixedKeyedDialer );
    Assert( mixedClearFailure is not null && mixedClearFailure.Contains( decryptionWording ) && mixedClearHost.Incoming.IsEmpty,
        "a plaintext host reads a keyed preamble as the key mistake it is" );
    Assert( mixedKeyedFailure == Wire.PeerClosedFailure,
        "the keyed dialer learns of the refusal from the close, with a reason it can read" );
}

// The nonce contract, pinned without leaning on randomness: with fixed salts the connection
// key is a pure function of the session key and the salt, and identical ciphertext across
// two framings needs an identical key — which a different salt denies.
var fixedSaltOne = Convert.FromHexString( "000102030405060708090a0b0c0d0e0f" );
var fixedSaltTwo = Convert.FromHexString( "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff" );
var fixedKeyOne = Wire.DeriveConnectionKey( sessionKey, fixedSaltOne );
var fixedKeyTwo = Wire.DeriveConnectionKey( sessionKey, fixedSaltTwo );
Assert( fixedKeyOne.Length == AeadFraming.KeyBytes &&
    fixedKeyOne.SequenceEqual( Wire.DeriveConnectionKey( sessionKey, fixedSaltOne ) ) &&
    !fixedKeyOne.SequenceEqual( fixedKeyTwo ) &&
    !fixedKeyOne.SequenceEqual( Wire.DeriveConnectionKey( mismatchedKey, fixedSaltOne ) ) &&
    !fixedKeyOne.SequenceEqual( sessionKey ),
    "the connection key is HKDF over the session key and the salt: reproducible for one salt, distinct for every other input" );

var noncePayload = System.Text.Encoding.UTF8.GetBytes( "{\"kind\":\"hello\",\"peer\":\"nonce-probe\"}" );
var saltOneFrame = new AeadFraming( fixedKeyOne, initiator: true ).Seal( noncePayload );
var saltTwoFrame = new AeadFraming( fixedKeyTwo, initiator: true ).Seal( noncePayload );
var repeatOne = new AeadFraming( fixedKeyOne, initiator: true ).Seal( noncePayload );
var repeatTwo = new AeadFraming( fixedKeyOne, initiator: true ).Seal( noncePayload );
Assert( repeatOne.SequenceEqual( repeatTwo ) && !saltOneFrame.SequenceEqual( saltTwoFrame ) &&
    System.Text.Encoding.UTF8.GetString( new AeadFraming( fixedKeyOne, initiator: false ).Open( saltOneFrame ) ) == System.Text.Encoding.UTF8.GetString( noncePayload ),
    "identical ciphertext needs an identical framing key, which a fresh connection salt denies" );

// The same claim over a real socket: two connections under one session key, each capturing
// its preamble and its first sealed frame. The ciphertext is driven by the preamble salt and
// by nothing else — written as an equivalence rather than "these two differ" so the assertion
// holds whatever the two random salts happen to be, and still fails the moment a reconnect
// reproduces an earlier connection's bytes.
var reconnectPacket = new Packet { Kind = "hello", Peer = "reconnect-probe" };
var firstConnection = await CaptureHandshakeAsync( sessionKey, reconnectPacket );
var secondConnection = await CaptureHandshakeAsync( sessionKey, reconnectPacket );
Assert( firstConnection.Preamble.Length == 20 && firstConnection.Frame.Length > AeadFraming.FrameOverhead,
    "a keyed connection opens with a twenty-byte preamble followed by a sealed frame" );
Assert( firstConnection.Frame.SequenceEqual( secondConnection.Frame ) == firstConnection.Preamble.SequenceEqual( secondConnection.Preamble ),
    "a reconnect's ciphertext is driven by the connection salt and by nothing else" );
// The equivalence above is satisfied by two identical preambles as well, so a wire that had
// settled on one fixed salt would pass it: the two captured preambles are also required to
// differ. Two draws of sixteen cryptographically random bytes collide with probability 2^-128,
// so this cannot flake, and it is what fails a regression to a constant salt.
Assert( !firstConnection.Preamble.SequenceEqual( secondConnection.Preamble ),
    "two connections under one session key open with different preambles: the salt is fresh every time" );

// And the derivation the wire actually used, read back out of the captured preamble: the
// frame opens under the session key combined with that connection's own salt, and that key
// is not the session key the caller passed in.
var capturedSalt = firstConnection.Preamble.AsSpan( 4 ).ToArray();
var capturedKey = Wire.DeriveConnectionKey( sessionKey, capturedSalt );
string openedCapture = null;
try { openedCapture = System.Text.Encoding.UTF8.GetString( new AeadFraming( capturedKey, initiator: false ).Open( firstConnection.Frame ) ); }
catch ( System.Security.Cryptography.CryptographicException ) { }
Assert( !capturedKey.SequenceEqual( sessionKey ) && openedCapture == System.Text.Encoding.UTF8.GetString( JsonSerializer.SerializeToUtf8Bytes( reconnectPacket ) ),
    "the frame a connection seals opens under HKDF of the session key and that connection's preamble salt" );

// Task: invite codes. One copy-pasteable string replaces the typed address, port and session
// key: it carries the port, a fingerprint of the project it belongs to, up to four candidate
// addresses and the 16-byte secret every session key is derived from. Only a four-byte
// fingerprint of the project travels — a GUID will not fit in a code that has to stay short —
// so the decoder that is handed the project id it expects verifies that fingerprint and can
// report the id back, while a decoder without one reports no project at all.
string InviteErrorOf( string text ) => InviteCode.TryDecode( text, out _, out var reason ) ? null : reason;

var inviteProjectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";
var inviteSecret = Enumerable.Range( 0, 16 ).Select( i => (byte)(i * 16 + 1) ).ToArray();
var fullInvite = new InviteData
{
    Port = 27845,
    ProjectId = inviteProjectId,
    Secret = inviteSecret,
    Endpoints = new List<byte[]>
    {
        new byte[] { 192, 168, 1, 10 },
        new byte[] { 10, 0, 0, 7 },
        new byte[] { 203, 0, 113, 5 }
    }
};
var inviteCode = InviteCode.Encode( fullInvite );
var inviteBody = inviteCode.Substring( InviteCode.Prefix.Length );
var inviteGroups = inviteBody.Split( '-' );
Assert( inviteCode.StartsWith( InviteCode.Prefix, StringComparison.Ordinal ) &&
    inviteGroups.Take( inviteGroups.Length - 1 ).All( g => g.Length == 4 ) &&
    inviteGroups[^1].Length is >= 1 and <= 4 &&
    inviteBody.All( c => c == '-' || "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains( c ) ),
    "an encoded invite code is CL1- prefixed, Crockford base32 and grouped into four-character blocks" );

Assert( InviteCode.TryDecode( inviteCode, inviteProjectId, out var decodedInvite, out var inviteError ) && inviteError is null &&
    decodedInvite.Port == fullInvite.Port &&
    decodedInvite.ProjectId == fullInvite.ProjectId &&
    decodedInvite.Secret.SequenceEqual( fullInvite.Secret ) &&
    decodedInvite.Endpoints.Count == fullInvite.Endpoints.Count &&
    decodedInvite.Endpoints[0].SequenceEqual( fullInvite.Endpoints[0] ) &&
    decodedInvite.Endpoints[1].SequenceEqual( fullInvite.Endpoints[1] ) &&
    decodedInvite.Endpoints[2].SequenceEqual( fullInvite.Endpoints[2] ),
    "an invite code round-trips its port, project, secret and endpoints in order" );

var projectFingerprint = System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.ASCII.GetBytes( inviteProjectId ) );
Assert( InviteCode.ProjectFingerprint( inviteProjectId ).Length == 4 &&
    InviteCode.ProjectFingerprint( inviteProjectId ).SequenceEqual( projectFingerprint.Take( 4 ) ),
    "the project fingerprint is the first four bytes of SHA256 over the ASCII project id" );

Assert( InviteCode.TryDecode( inviteCode, out var bareInvite, out var bareError ) && bareError is null &&
    bareInvite.ProjectId is null && bareInvite.Port == fullInvite.Port,
    "a decoder that was not handed a project id reports no project rather than a guess" );

Assert( !InviteCode.TryDecode( inviteCode, "00000000-0000-0000-0000-000000000000", out var otherProject, out var otherProjectError ) &&
    otherProject is null && otherProjectError is { Length: > 0 },
    "a code carrying another project's fingerprint is refused with a reason" );

// The code is read by eye and pasted through chat clients, so case, spacing, the dashes and
// the O/I/L look-alikes must all be absorbed before anything is derived from it.
var spacedInvite = "  " + inviteCode.ToLowerInvariant().Replace( "-", "   " ) + " \t";
Assert( InviteCode.TryDecode( spacedInvite, out var spacedInviteData, out _ ) &&
    spacedInviteData.Port == fullInvite.Port &&
    spacedInviteData.Secret.SequenceEqual( fullInvite.Secret ) &&
    spacedInviteData.Endpoints.Count == fullInvite.Endpoints.Count,
    "decoding tolerates lower case, surrounding whitespace and spaces in place of dashes" );

Assert( InviteCode.TryDecode( "CL1" + inviteBody.Replace( "-", "" ), out var undashedInvite, out _ ) && undashedInvite.Port == fullInvite.Port,
    "decoding tolerates a code whose dashes have been removed" );

// The alphabet leaves out O, I, L and U on purpose, and a reader still types them: O means 0
// and I or L means 1. This code is built to contain both — a zero secret fills its tail with
// zero characters and port 512 puts a one in the third block — and the not-equal checks below
// fail loudly if that ever stops being true.
var lookAlikeCode = InviteCode.Encode( new InviteData { Port = 512, ProjectId = inviteProjectId, Secret = new byte[16], Endpoints = new List<byte[]>() } );
var lookAlikeBody = lookAlikeCode.Substring( InviteCode.Prefix.Length );
var withO = lookAlikeBody.Replace( '0', 'O' );
var withI = lookAlikeBody.Replace( '1', 'I' );
var withL = lookAlikeBody.Replace( '1', 'L' );
Assert( withO != lookAlikeBody && withI != lookAlikeBody && withL != lookAlikeBody &&
    InviteCode.TryDecode( InviteCode.Prefix + withO, out var oInvite, out _ ) && oInvite.Port == 512 && oInvite.Secret.SequenceEqual( new byte[16] ) &&
    InviteCode.TryDecode( InviteCode.Prefix + withI, out var iInvite, out _ ) && iInvite.Port == 512 &&
    InviteCode.TryDecode( InviteCode.Prefix + withL, out var lInvite, out _ ) && lInvite.Port == 512,
    "O, I and L are read as 0 and 1 so a hand-typed look-alike still decodes" );

Assert( !InviteCode.TryDecode( null, out var nullInvite, out var nullInviteError ) && nullInvite is null && nullInviteError is { Length: > 0 },
    "a null invite code is refused without throwing" );

var junkAccepted = false;
foreach ( var junk in new[] { "", "   ", "CL1-", "CL1-UUUU", "CL1-!!!!!!!!", "not an invite code", "CL1-" + new string( 'Z', 400 ) } )
{
    try
    {
        if ( InviteCode.TryDecode( junk, out _, out var junkError ) || string.IsNullOrWhiteSpace( junkError ) ) junkAccepted = true;
    }
    catch { junkAccepted = true; }
}
Assert( !junkAccepted, "garbage input never throws and always yields a specific reason" );

// Every refusal carries its own formal reason, so the dock can tell a truncated paste from a
// mistyped character from a code that was never a code at all.
var truncatedError = InviteErrorOf( InviteCode.Prefix + inviteBody.Replace( "-", "" ).Substring( 0, 39 ) );
var extraCharError = InviteErrorOf( inviteCode + "A" );
var middleChar = inviteCode.Length / 2;
while ( inviteCode[middleChar] == '-' ) middleChar++;
var checksumText = inviteCode.Substring( 0, middleChar ) + ( inviteCode[middleChar] == 'A' ? 'B' : 'A' ) + inviteCode.Substring( middleChar + 1 );
var checksumError = InviteErrorOf( checksumText );
var prefixError = InviteErrorOf( "CL2-" + inviteBody );

// The payload length is driven by the endpoint count byte, so flipping that byte by hand is
// how a code whose secret would come out the wrong size is produced from a valid one.
var zeroPortPayload = InviteCode.BuildPayload( fullInvite );
zeroPortPayload[1] = 0;
zeroPortPayload[2] = 0;
var zeroPortError = InviteErrorOf( InviteCode.EncodePayload( zeroPortPayload ) );

var fiveEndpointPayload = InviteCode.BuildPayload( fullInvite );
fiveEndpointPayload[7] = 5;
var fiveEndpointError = InviteErrorOf( InviteCode.EncodePayload( fiveEndpointPayload ) );

var shortPayload = InviteCode.BuildPayload( fullInvite );
shortPayload[7] = 4;
var shortSecretError = InviteErrorOf( InviteCode.EncodePayload( shortPayload ) );

Assert( truncatedError is { Length: > 0 }, "a truncated invite code is refused with a reason" );
Assert( extraCharError is { Length: > 0 }, "an invite code with one character too many is refused with a reason" );
Assert( checksumError is { Length: > 0 }, "an invite code whose checksum does not match is refused with a reason" );
Assert( prefixError is { Length: > 0 }, "an invite code without the CL1- prefix is refused with a reason" );
Assert( zeroPortError is { Length: > 0 }, "an invite code carrying port zero is refused with a reason" );
Assert( fiveEndpointError is { Length: > 0 }, "an invite code declaring five endpoints is refused with a reason" );
Assert( shortSecretError is { Length: > 0 }, "an invite code whose secret would be the wrong size is refused with a reason" );
var inviteRejections = new[] { truncatedError, extraCharError, checksumError, prefixError, zeroPortError, fiveEndpointError, shortSecretError };
Assert( inviteRejections.All( e => e is { Length: > 0 } ) && inviteRejections.Distinct().Count() == inviteRejections.Length,
    "every invite code rejection carries its own distinct reason" );

// A port above 65535 cannot exist in the two bytes a code reserves for it, so the range is
// refused on the way in instead — a malformed code must never be produced at all.
try
{
    InviteCode.Encode( new InviteData { Port = 70000, ProjectId = inviteProjectId, Secret = inviteSecret, Endpoints = new List<byte[]>() } );
    Assert( false, "encoding refuses a port above 65535 such as 70000" );
}
catch ( ArgumentOutOfRangeException ) { Assert( true, "encoding refuses a port above 65535 such as 70000" ); }

try
{
    InviteCode.Encode( new InviteData { Port = 27845, ProjectId = inviteProjectId, Secret = inviteSecret, Endpoints = new List<byte[]> { new byte[4], new byte[4], new byte[4], new byte[4], new byte[4] } } );
    Assert( false, "encoding refuses five endpoints" );
}
catch ( ArgumentOutOfRangeException ) { Assert( true, "encoding refuses five endpoints" ); }

var freshSecret = InviteCode.NewSecret();
var secondSecret = InviteCode.NewSecret();
Assert( freshSecret.Length == 16 && secondSecret.Length == 16 && !freshSecret.SequenceEqual( secondSecret ),
    "NewSecret returns sixteen fresh random bytes" );

// RFC 5869 Appendix A, test case 1: the published vector is what pins this HKDF to the
// standard rather than to whatever this file happens to produce.
var rfcIkm = Enumerable.Repeat( (byte)0x0b, 22 ).ToArray();
var rfcSalt = Enumerable.Range( 0, 13 ).Select( i => (byte)i ).ToArray();
var rfcInfo = Enumerable.Range( 0, 10 ).Select( i => (byte)(0xf0 + i) ).ToArray();
var rfcOkm = Convert.FromHexString( "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865" );
Assert( InviteCode.HkdfSha256( rfcIkm, rfcSalt, rfcInfo, 42 ).SequenceEqual( rfcOkm ),
    "DeriveKey's HKDF-SHA256 reproduces RFC 5869 test case 1" );

var derivedKey = InviteCode.DeriveKey( inviteSecret, inviteProjectId );
Assert( derivedKey.Length == 32 && derivedKey.SequenceEqual( InviteCode.DeriveKey( inviteSecret, inviteProjectId ) ) &&
    !derivedKey.SequenceEqual( InviteCode.DeriveKey( inviteSecret, "00000000-0000-0000-0000-000000000000" ) ) &&
    !derivedKey.SequenceEqual( InviteCode.DeriveKey( InviteCode.NewSecret(), inviteProjectId ) ),
    "DeriveKey is stable for one project and secret and distinct when either changes" );

// The framing contract the transport tests above only exercise indirectly: key size, the tag
// over every byte of a frame, and the counter that must never wrap into a reused nonce.
try { new AeadFraming( new byte[16] ); Assert( false, "a session key that is not 32 bytes is refused" ); }
catch ( ArgumentOutOfRangeException ) { Assert( true, "a session key that is not 32 bytes is refused" ); }

var tamperSealer = new AeadFraming( sessionKey, initiator: true );
var tamperOpener = new AeadFraming( sessionKey, initiator: false );
var intactFrame = tamperSealer.Seal( System.Text.Encoding.UTF8.GetBytes( "the quick brown fox" ) );
var everyFlipRejected = true;
foreach ( var position in new[] { 0, 3, 4, 11, 12, intactFrame.Length / 2, intactFrame.Length - 1 } )
{
    var flipped = (byte[])intactFrame.Clone();
    flipped[position] ^= 0x01;
    try { tamperOpener.Open( flipped ); everyFlipRejected = false; }
    catch ( System.Security.Cryptography.CryptographicException ) { }
}
Assert( everyFlipRejected, "flipping a bit anywhere in a sealed frame raises CryptographicException" );

try { new AeadFraming( SessionKey( 0x44 ), initiator: false ).Open( intactFrame ); Assert( false, "opening a frame with the wrong key raises CryptographicException" ); }
catch ( System.Security.Cryptography.CryptographicException ) { Assert( true, "opening a frame with the wrong key raises CryptographicException" ); }

var exhaustedFraming = new AeadFraming( sessionKey, initiator: true );
exhaustedFraming.SetCounterForTests( long.MaxValue );
try { exhaustedFraming.Seal( Array.Empty<byte>() ); Assert( false, "a framing counter at long.MaxValue refuses to seal again" ); }
catch ( InvalidOperationException ) { Assert( true, "a framing counter at long.MaxValue refuses to seal again" ); }

// Strict receive sequence. A relay can sit between the two ends, so an intact tag is no longer
// enough: the frame must also be the very next one. Each case below fails if the rule is removed.
var seqSealer = new AeadFraming( sessionKey, initiator: true );
var seqOpener = new AeadFraming( sessionKey, initiator: false );
var seqOne = seqSealer.Seal( System.Text.Encoding.UTF8.GetBytes( "one" ) );
var seqTwo = seqSealer.Seal( System.Text.Encoding.UTF8.GetBytes( "two" ) );
var seqThree = seqSealer.Seal( System.Text.Encoding.UTF8.GetBytes( "three" ) );
Assert( System.Text.Encoding.UTF8.GetString( seqOpener.Open( seqOne ) ) == "one", "the first frame of a stream opens" );
string SequenceRefusal( AeadFraming opener, byte[] frame )
{
    try { opener.Open( frame ); return null; }
    catch ( System.Security.Cryptography.CryptographicException ex ) { return ex.Message; }
}
Assert( SequenceRefusal( seqOpener, seqOne ) == AeadFraming.SequenceFailure, "a replayed frame is refused as out of sequence" );
Assert( SequenceRefusal( seqOpener, seqThree ) == AeadFraming.SequenceFailure, "a frame that skips one is refused as out of sequence" );
Assert( System.Text.Encoding.UTF8.GetString( seqOpener.Open( seqTwo ) ) == "two",
    "refused frames do not move the window, so the genuine next frame still opens" );
var seqForged = (byte[])seqThree.Clone();
seqForged[seqForged.Length / 2] ^= 0x01;
Assert( SequenceRefusal( seqOpener, seqForged ) != null && SequenceRefusal( seqOpener, seqForged ) != AeadFraming.SequenceFailure,
    "a forged frame carrying the right counter fails on its tag, not on its sequence" );
Assert( System.Text.Encoding.UTF8.GetString( seqOpener.Open( seqThree ) ) == "three",
    "a forged frame does not consume its counter either, so the genuine frame still opens" );
Assert( SequenceRefusal( seqOpener, seqTwo ) == AeadFraming.SequenceFailure, "an earlier frame stays refused after later ones opened" );
var seqLate = new AeadFraming( sessionKey, initiator: false );
Assert( SequenceRefusal( seqLate, seqTwo ) == AeadFraming.SequenceFailure,
    "a stream cannot start part-way: a fresh opener refuses a frame numbered above one" );
}

// ---- T4b: zero-config connect. These are the pure parts of the new layer — the parsers,
// builders, ordering and expiry rules. Sockets are exercised live, not here.

// The project hash a rendezvous datagram carries instead of the project id. Eight lowercase
// hex characters, because BuildQuery and BuildReply both refuse anything else before a
// datagram goes out, and the hash has to be right in both directions or discovery is silent.
var t4bPid = "11111111-2222-3333-4444-555555555555";
var t4bHash = DiscoveryProtocol.ProjectHash8( t4bPid );
Assert( t4bHash == "666ff6cc", "ProjectHash8 is the first eight hex characters of SHA-256 over the project id" );
Assert( t4bHash.Length == 8 && t4bHash.All( Uri.IsHexDigit ),
    "ProjectHash8 is exactly eight hex characters, which is what BuildQuery accepts" );
Assert( t4bHash == DiscoveryProtocol.ProjectHash8( t4bPid ) &&
    t4bHash != DiscoveryProtocol.ProjectHash8( "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" ) &&
    t4bHash != DiscoveryProtocol.ProjectHash8( "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" ),
    "ProjectHash8 is stable for one id and distinct for another" );
Assert( DiscoveryProtocol.BuildQuery( t4bHash ).Contains( "pid=" + t4bHash ),
    "the hash a host queries with is the hash the query carries" );
try { DiscoveryProtocol.ProjectHash8( "" ); Assert( false, "ProjectHash8 refuses an empty project id" ); }
catch ( ArgumentException ) { Assert( true, "ProjectHash8 refuses an empty project id" ); }

// Expiry: a host silent for longer than the window goes, one that replied recently stays,
// and a clock that jumped backwards empties nothing. Pure so it needs neither a clock nor
// a socket.
var t4bNow = new DateTime( 2026, 1, 1, 12, 0, 0, DateTimeKind.Utc );
DiscoveredHost T4bHost( string name, DateTime seen ) => new( "10.0.0.2", 27845, name, new string( 'a', 32 ), "deadbeef", seen );
var t4bHosts = new Dictionary<string, DiscoveredHost>
{
    ["fresh"] = T4bHost( "fresh", t4bNow.AddSeconds( -1 ) ),
    ["edge"] = T4bHost( "edge", t4bNow - TimeSpan.FromSeconds( 5 ) ),
    ["stale"] = T4bHost( "stale", t4bNow - TimeSpan.FromSeconds( 6 ) ),
};
Assert( LanDiscovery.Prune( t4bHosts, t4bNow, TimeSpan.FromSeconds( 5 ) ) == 1 && t4bHosts.Count == 2 && !t4bHosts.ContainsKey( "stale" ),
    "Prune drops a host silent past the window, and keeps one silent for exactly the window" );
Assert( LanDiscovery.Prune( t4bHosts, t4bNow.AddSeconds( -10 ), TimeSpan.FromSeconds( 5 ) ) == 0 && t4bHosts.Count == 2,
    "Prune with a clock that jumped backwards keeps everything rather than emptying the table" );
Assert( LanDiscovery.Prune( new Dictionary<string, DiscoveredHost>(), t4bNow, TimeSpan.FromSeconds( 5 ) ) == 0,
    "Prune of an empty table reports nothing to drop" );

// The flood cap: a socket that is being flooded must stop spending its budget when the
// budget is gone, even though more is waiting, and must report what it actually spent.
var t4bWaiting = 100;
var t4bDrained = new List<int>();
var t4bSpent = LanDiscovery.Drain( LanDiscovery.MaxDatagramsPerPoll, () => t4bWaiting > 0, () => --t4bWaiting, t4bDrained.Add );
Assert( t4bSpent == LanDiscovery.MaxDatagramsPerPoll && t4bDrained.Count == LanDiscovery.MaxDatagramsPerPoll &&
    t4bWaiting == 100 - LanDiscovery.MaxDatagramsPerPoll,
    "Drain spends exactly its cap while datagrams are still waiting" );
Assert( LanDiscovery.Drain( LanDiscovery.MaxDatagramsPerPoll, () => t4bWaiting > 0, () => --t4bWaiting, t4bDrained.Add ) ==
    LanDiscovery.MaxDatagramsPerPoll && t4bWaiting == 100 - 2 * LanDiscovery.MaxDatagramsPerPoll,
    "Drain's accounting adds up across calls" );
t4bSpent = LanDiscovery.Drain( LanDiscovery.MaxDatagramsPerPoll, () => false, () => --t4bWaiting, t4bDrained.Add );
Assert( t4bSpent == 0 && t4bWaiting == 100 - 2 * LanDiscovery.MaxDatagramsPerPoll,
    "Drain with nothing waiting reads nothing" );

// A canned Binding Success Response, laid out the way RFC 5389 lays one out: message type
// 0x0101, the magic cookie, then one XOR-MAPPED-ADDRESS attribute whose port and address are
// hidden behind that cookie. 203.0.113.7 : 54321.
byte[] T4bStunSuccess( ushort port, byte[] address )
{
    var cookie = 0x2112A442u;                              // not const: the compiler will not narrow a constant shift to a byte
    var message = new byte[20 + 12];
    message[0] = 0x01; message[1] = 0x01;                  // Binding Success Response
    message[2] = 0x00; message[3] = 0x0c;                  // attribute length: 4 header + 8 value
    message[4] = (byte)( cookie >> 24 ); message[5] = (byte)( cookie >> 16 );
    message[6] = (byte)( cookie >> 8 ); message[7] = (byte)cookie;
    for ( var i = 0; i < 12; i++ ) message[8 + i] = (byte)( 0xA0 + i );
    message[20] = 0x00; message[21] = 0x20;                // XOR-MAPPED-ADDRESS
    message[22] = 0x00; message[23] = 0x08;                // eight bytes follow
    message[24] = 0x00;                                    // reserved
    message[25] = 0x01;                                    // family: IPv4
    var obscuredPort = port ^ (ushort)( cookie >> 16 );
    message[26] = (byte)( obscuredPort >> 8 ); message[27] = (byte)obscuredPort;
    message[28] = (byte)( address[0] ^ (byte)( cookie >> 24 ) );
    message[29] = (byte)( address[1] ^ (byte)( cookie >> 16 ) );
    message[30] = (byte)( address[2] ^ (byte)( cookie >> 8 ) );
    message[31] = (byte)( address[3] ^ (byte)cookie );
    return message;
}
byte[] t4bAddress = { 203, 0, 113, 7 };
var t4bSuccess = T4bStunSuccess( 54321, t4bAddress );
Assert( StunClient.TryReadMappedAddress( t4bSuccess, out var t4bStunAddr, out var t4bStunErr ) &&
    t4bStunAddr == "203.0.113.7" && t4bStunErr is null,
    "TryReadMappedAddress decodes a canned XOR-MAPPED-ADDRESS response" );

var t4bPlain = (byte[])t4bSuccess.Clone();
var t4bPlainPort = (ushort)54321;
t4bPlain[20] = 0x00; t4bPlain[21] = 0x01;                  // MAPPED-ADDRESS: not obfuscated
t4bPlain[26] = (byte)( t4bPlainPort >> 8 ); t4bPlain[27] = (byte)t4bPlainPort;
t4bPlain[28] = 203; t4bPlain[29] = 0; t4bPlain[30] = 113; t4bPlain[31] = 7;
Assert( StunClient.TryReadMappedAddress( t4bPlain, out var t4bPlainAddr, out _ ) && t4bPlainAddr == "203.0.113.7",
    "TryReadMappedAddress reads the unobfuscated MAPPED-ADDRESS some servers still send" );

// Every case below is something an ordinary network produces, and none of them may throw.
void T4bStunRefusal( byte[] message, string name )
{
    var refused = !StunClient.TryReadMappedAddress( message, out var got, out var why );
    Assert( refused && got is null && why is not null, name );
}
T4bStunRefusal( null, "a missing STUN response is refused with a reason" );
T4bStunRefusal( new byte[8], "a datagram shorter than a STUN header is refused with a reason" );
var t4bNotSuccess = (byte[])t4bSuccess.Clone(); t4bNotSuccess[0] = 0x00; t4bNotSuccess[1] = 0x01;
T4bStunRefusal( t4bNotSuccess, "a response that is not a Binding Success is refused with a reason" );
var t4bNoCookie = (byte[])t4bSuccess.Clone(); t4bNoCookie[4] = 0x00;
T4bStunRefusal( t4bNoCookie, "a response without the magic cookie is refused with a reason" );
var t4bBadLength = (byte[])t4bSuccess.Clone(); t4bBadLength[3] = 0x00;
T4bStunRefusal( t4bBadLength, "a response that declares a length it does not have is refused with a reason" );
var t4bTruncated = (byte[])t4bSuccess.Clone(); t4bTruncated[23] = 0x40;
T4bStunRefusal( t4bTruncated, "a response with a truncated attribute is refused with a reason" );
var t4bNoAddress = (byte[])t4bSuccess.Clone(); t4bNoAddress[20] = 0x80; t4bNoAddress[21] = 0x20;
T4bStunRefusal( t4bNoAddress, "a response carrying only unknown attributes is refused with a reason" );
var t4bNotIpv4 = (byte[])t4bSuccess.Clone(); t4bNotIpv4[25] = 0x02;
T4bStunRefusal( t4bNotIpv4, "a mapped address that is not IPv4 is refused with a reason" );

var t4bRequest = StunClient.BuildBindingRequest( out var t4bTransaction );
Assert( t4bRequest.Length == 20 && t4bRequest[0] == 0x00 && t4bRequest[1] == 0x01 &&
    t4bRequest[4] == 0x21 && t4bRequest[5] == 0x12 && t4bRequest[6] == 0xA4 && t4bRequest[7] == 0x42 &&
    t4bTransaction.Length == 12 && t4bRequest.Skip( 8 ).SequenceEqual( t4bTransaction ),
    "BuildBindingRequest is a 20-byte Binding Request carrying the cookie and a random transaction id" );
Assert( !StunClient.BuildBindingRequest( out var t4bOtherTransaction ).Skip( 8 ).SequenceEqual( t4bTransaction ),
    "two Binding Requests never share a transaction id" );

// The SOAP bodies a router is asked for: the same port inside and out, TCP, enabled, named,
// and leased for zero. A zero lease is permanent, which is exactly why Unmap has to delete
// it — a finite lease would expire mid-session and quietly stop new joiners from arriving.
var t4bWan = UpnpPortMapper.ServiceTypes[0];
var t4bAdd = UpnpPortMapper.BuildAddPortMappingBody( t4bWan, 27845, "192.168.1.10" );
Assert( t4bAdd.Contains( "<u:AddPortMapping xmlns:u=\"" + t4bWan + "\">" ) &&
    t4bAdd.Contains( "<NewExternalPort>27845</NewExternalPort>" ) &&
    t4bAdd.Contains( "<NewInternalPort>27845</NewInternalPort>" ) &&
    t4bAdd.Contains( "<NewInternalClient>192.168.1.10</NewInternalClient>" ) &&
    t4bAdd.Contains( "<NewProtocol>TCP</NewProtocol>" ) &&
    t4bAdd.Contains( "<NewEnabled>1</NewEnabled>" ) &&
    t4bAdd.Contains( "<NewPortMappingDescription>s&amp;box collaboration</NewPortMappingDescription>" ) &&
    t4bAdd.Contains( "<NewLeaseDuration>0</NewLeaseDuration>" ),
    "BuildAddPortMappingBody asks for TCP, one port both ways, enabled, named and permanent" );
Assert( !t4bAdd.Contains( "s&box collaboration" ),
    "the product name is XML-escaped, because it contains an ampersand" );
var t4bDelete = UpnpPortMapper.BuildDeletePortMappingBody( t4bWan, 27845 );
Assert( t4bDelete.Contains( "<u:DeletePortMapping xmlns:u=\"" + t4bWan + "\">" ) &&
    t4bDelete.Contains( "<NewExternalPort>27845</NewExternalPort>" ) &&
    t4bDelete.Contains( "<NewProtocol>TCP</NewProtocol>" ),
    "BuildDeletePortMappingBody deletes the same port it created" );
try { UpnpPortMapper.BuildAddPortMappingBody( "urn:schemas-upnp-org:service:NotAService:1", 1, "1.2.3.4" ); Assert( false, "a mapping through an unknown service type is refused" ); }
catch ( ArgumentException ) { Assert( true, "a mapping through an unknown service type is refused" ); }
try { UpnpPortMapper.BuildDeletePortMappingBody( t4bWan, 0 ); Assert( false, "a mapping for port zero is refused" ); }
catch ( ArgumentOutOfRangeException ) { Assert( true, "a mapping for port zero is refused" ); }

// The LOCATION header out of an SSDP reply, which is the only thing the multicast search
// has to get right before any HTTP happens.
var t4bSsdp = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\n\r\n";
Assert( UpnpPortMapper.TryReadLocation( t4bSsdp, out var t4bLocation, out var t4bLocationErr ) &&
    t4bLocation == "http://192.168.1.1:5000/rootDesc.xml" && t4bLocationErr is null,
    "TryReadLocation reads the LOCATION out of a canned SSDP reply" );
void T4bLocationRefusal( string reply, string name )
{
    var refused = !UpnpPortMapper.TryReadLocation( reply, out var got, out var why );
    Assert( refused && got is null && why is not null, name );
}
T4bLocationRefusal( "HTTP/1.1 404 Not Found\r\nLOCATION: http://192.168.1.1/x\r\n", "a non-2xx SSDP reply is refused with a reason" );
T4bLocationRefusal( "HTTP/1.1 200 OK\r\n", "an SSDP reply with no LOCATION is refused with a reason" );
T4bLocationRefusal( "HTTP/1.1 200 OK\r\nLOCATION: ftp://192.168.1.1/x\r\n", "a LOCATION that is not http is refused with a reason" );
T4bLocationRefusal( "HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1/x\r\nST: urn:schemas-upnp-org:service:Content:1\r\n", "an SSDP reply for a different search target is refused with a reason" );

// The control URL out of a router description, resolved against where the description came
// from: untrusted input from a device on the local network, so a URL that is not plain HTTP
// is refused with a reason instead of being posted to.
var t4bDescription =
    "<?xml version=\"1.0\"?><root><device><serviceList><service>" +
    "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
    "<controlURL>/upnp/control/WANIPConn1</controlURL>" +
    "</service></serviceList></device></root>";
Assert( UpnpPortMapper.TryResolveControlUrl( t4bDescription, "http://192.168.1.1:5000/rootDesc.xml",
    out var t4bServiceType, out var t4bControlUrl, out var t4bControlErr ) &&
    t4bServiceType == UpnpPortMapper.ServiceTypes[0] &&
    t4bControlUrl == "http://192.168.1.1:5000/upnp/control/WANIPConn1" && t4bControlErr is null,
    "TryResolveControlUrl turns a root-relative control URL into an absolute one" );
void T4bControlRefusal( string description, string location, string name )
{
    var refused = !UpnpPortMapper.TryResolveControlUrl( description, location, out _, out _, out var why );
    Assert( refused && why is not null, name );
}
T4bControlRefusal( "<root></root>", "http://192.168.1.1:5000/x.xml", "a description with no WAN service is refused with a reason" );
T4bControlRefusal( t4bDescription, "ftp://192.168.1.1/x.xml", "a location that is not http is refused with a reason" );
T4bControlRefusal( "", "http://192.168.1.1:5000/x.xml", "an empty description is refused with a reason" );

// The address a mapping forwards to, chosen from the control URL's host. A URL that already
// names an address is used exactly as written — which is the whole reason a router named by
// IP never reaches a resolver — while a name must have produced an IPv4 answer, because the
// SOAP body names a dotted quad. The lookup itself is deliberately not tested: it is the one
// step of this exchange that leaves the process, and the frame only ever reads its result
// behind an IsCompleted guard.
Assert( UpnpPortMapper.TrySelectRouter( "192.168.1.1", null, out var t4bRouterLiteral, out var t4bRouterLiteralErr ) &&
    t4bRouterLiteral.Equals( IPAddress.Parse( "192.168.1.1" ) ) && t4bRouterLiteralErr is null,
    "a control URL that already names an address selects it without consulting an answer" );
Assert( UpnpPortMapper.TrySelectRouter( "router.local",
        new[] { IPAddress.Parse( "fe80::1" ), IPAddress.Parse( "192.168.1.1" ) }, out var t4bRouterNamed, out var t4bRouterNamedErr ) &&
    t4bRouterNamed.Equals( IPAddress.Parse( "192.168.1.1" ) ) && t4bRouterNamedErr is null,
    "a hostname selects the first IPv4 answer the resolver produced" );
Assert( !UpnpPortMapper.TrySelectRouter( "router.local", new[] { IPAddress.Parse( "fe80::1" ) },
        out var t4bRouterRefused, out var t4bRouterRefusedErr ) &&
    t4bRouterRefused is null && t4bRouterRefusedErr is not null,
    "a hostname whose answers hold no IPv4 address is refused with a reason" );
Assert( !UpnpPortMapper.TrySelectRouter( "router.local", null, out _, out var t4bRouterAbsentErr ) &&
    t4bRouterAbsentErr is not null,
    "a hostname with no answer at all is refused rather than guessed at" );

Assert( UpnpPortMapper.CanTransition( UpnpPortMapper.State.Idle, UpnpPortMapper.State.Discovering ) &&
    UpnpPortMapper.CanTransition( UpnpPortMapper.State.Discovering, UpnpPortMapper.State.Mapping ) &&
    UpnpPortMapper.CanTransition( UpnpPortMapper.State.Mapping, UpnpPortMapper.State.Mapped ) &&
    UpnpPortMapper.CanTransition( UpnpPortMapper.State.Mapping, UpnpPortMapper.State.Failed ) &&
    UpnpPortMapper.CanTransition( UpnpPortMapper.State.Mapped, UpnpPortMapper.State.Idle ) &&
    UpnpPortMapper.CanTransition( UpnpPortMapper.State.Failed, UpnpPortMapper.State.Idle ),
    "the mapping state machine covers the happy path and every way out" );
Assert( !UpnpPortMapper.CanTransition( UpnpPortMapper.State.Idle, UpnpPortMapper.State.Mapped ) &&
    !UpnpPortMapper.CanTransition( UpnpPortMapper.State.Mapped, UpnpPortMapper.State.Mapping ) &&
    !UpnpPortMapper.CanTransition( UpnpPortMapper.State.Idle, UpnpPortMapper.State.Failed ),
    "a mapping that was never attempted cannot be Mapped, and Mapped cannot go back a step" );

// Candidate ordering: LAN first because it needs no router, then whatever address the code
// itself carries, then the address a person typed. Anything undialable is dropped rather
// than handed to a socket, and the list is capped because a join is a queue of timeouts.
var t4bSecret = InviteCode.NewSecret();
var t4bInvite = new InviteData
{
    Port = 27845,
    ProjectId = t4bPid,
    Secret = t4bSecret,
    Endpoints = new List<byte[]> { new byte[] { 203, 0, 113, 7 }, new byte[] { 1, 2, 3, 4, 5 }, new byte[] { 192, 168, 1, 20 } }
};
var t4bDiscovered = new List<DiscoveredHost>
{
    new( "192.168.1.20", 27845, "peer", new string( 'a', 32 ), "deadbeef", t4bNow ),
    new( "192.168.1.21", 99999, "impossible port", new string( 'a', 32 ), "deadbeef", t4bNow ),
};
var t4bOrdered = EndpointResolver.Order( t4bInvite, t4bDiscovered, "10.9.8.7", 27845 ).ToList();
Assert( t4bOrdered.Select( c => c.Source ).SequenceEqual( new[] { "lan", "code", "advanced" } ) &&
    t4bOrdered.Select( c => c.Address ).SequenceEqual( new[] { "192.168.1.20", "203.0.113.7", "10.9.8.7" } ) &&
    t4bOrdered.All( c => c.Port == 27845 ),
    "candidates run LAN, then the code's endpoints, then the advanced address, deduplicated" );
var t4bMany = Enumerable.Range( 1, 12 ).Select( i => new DiscoveredHost( "10.0.0." + i, 27845, "h" + i, new string( 'a', 32 ), "deadbeef", t4bNow ) ).ToList();
Assert( EndpointResolver.Order( null, t4bMany, "", 0 ).Count() == EndpointResolver.MaxCandidates,
    "at most MaxCandidates are ever produced, and an empty advanced address is never yielded" );
Assert( EndpointResolver.Order( t4bInvite, null, null, 0 ).Count() == 2,
    "with nothing discovered and nothing typed, only the code's own dialable endpoints are tried" );
Assert( EndpointResolver.FormatIpv4( new byte[] { 10, 0, 0, 1 } ) == "10.0.0.1" &&
    EndpointResolver.FormatIpv4( new byte[] { 1, 2, 3 } ) is null && EndpointResolver.FormatIpv4( null ) is null,
    "FormatIpv4 renders four bytes and refuses anything else" );

// A code is only useful if the joiner derives the same key the host sealed with: port,
// endpoints, project and secret must all survive the trip, and a code for another project
// must be refused rather than dialed.
var t4bCode = InviteCode.Encode( new InviteData
{
    Port = 27845,
    ProjectId = t4bPid,
    Secret = t4bSecret,
    Endpoints = new List<byte[]> { new byte[] { 203, 0, 113, 7 } }
} );
Assert( InviteCode.TryDecode( t4bCode, t4bPid, out var t4bDecoded, out _ ) &&
    t4bDecoded.Port == 27845 && t4bDecoded.Secret.SequenceEqual( t4bSecret ) &&
    EndpointResolver.FormatIpv4( t4bDecoded.Endpoints[0] ) == "203.0.113.7" &&
    InviteCode.DeriveKey( t4bDecoded.Secret, t4bPid ).SequenceEqual( InviteCode.DeriveKey( t4bSecret, t4bPid ) ),
    "a code round-trips its port, endpoints, project and the key a joiner derives" );
Assert( !InviteCode.TryDecode( t4bCode, "00000000-0000-0000-0000-000000000000", out _, out _ ),
    "a code issued for one project is refused by another" );

// Native save reconciliation: scene.saved is the only thing that can distinguish a save this
// session sanctioned from a file something else rewrote, so the decision is a pure function
// of the four facts the handler gathers.
Assert( SceneSaveRules.Decide( ownSave: true, sessionLive: true, sceneTracked: true, saveGuardHolds: true ) == SceneSaveOutcome.Ignore,
    "this session's own save is not news to the scene.saved reconciliation" );
Assert( SceneSaveRules.Decide( ownSave: true, sessionLive: true, sceneTracked: true, saveGuardHolds: false ) == SceneSaveOutcome.Ignore,
    "an own save is ignored even where the save guard would refuse" );
Assert( SceneSaveRules.Decide( ownSave: false, sessionLive: true, sceneTracked: true, saveGuardHolds: true ) == SceneSaveOutcome.Absorb,
    "a native save while the save guard holds is absorbed" );
Assert( SceneSaveRules.Decide( ownSave: false, sessionLive: true, sceneTracked: true, saveGuardHolds: false ) == SceneSaveOutcome.Warn,
    "a native save with edits in flight warns instead of blessing a checkpoint" );
Assert( SceneSaveRules.Decide( ownSave: false, sessionLive: false, sceneTracked: true, saveGuardHolds: true ) == SceneSaveOutcome.Ignore,
    "a save with no live session on record has nothing to reconcile" );
Assert( SceneSaveRules.Decide( ownSave: false, sessionLive: true, sceneTracked: false, saveGuardHolds: true ) == SceneSaveOutcome.Ignore,
    "a save of a scene this session does not track is ignored" );

// The frame scan's disk stamp: matching is never fatal, a first mismatch is a save whose own
// report has not landed yet, and a change with no report behind it still fails.
Assert( !SceneSaveRules.ShouldFailOnDiskStamp( true, TimeSpan.Zero ) &&
    !SceneSaveRules.ShouldFailOnDiskStamp( true, SceneSaveRules.ExternalChangeGrace + TimeSpan.FromSeconds( 1 ) ),
    "a matching disk stamp never fails the frame scan" );
Assert( !SceneSaveRules.ShouldFailOnDiskStamp( false, TimeSpan.Zero ) &&
    !SceneSaveRules.ShouldFailOnDiskStamp( false, SceneSaveRules.ExternalChangeGrace - TimeSpan.FromTicks( 1 ) ),
    "a stamp mismatch inside the grace waits for the save's own report" );
Assert( SceneSaveRules.ShouldFailOnDiskStamp( false, SceneSaveRules.ExternalChangeGrace ),
    "an external stamp change with no save report behind it still fails the session" );

// Automatic saves. A checkpoint needs the scene behind memory and a Save that would be
// accepted; the idle save needs those plus its own toggle and everything an unattended write
// must not disturb.
Assert( AutoSaveRules.CheckpointEligible( true, () => true ) &&
    !AutoSaveRules.CheckpointEligible( false, () => true ) &&
    !AutoSaveRules.CheckpointEligible( true, () => false ),
    "a checkpoint runs only when the scene is behind memory and Save would accept it" );
var tAutoCheckpointAsked = false;
Assert( !AutoSaveRules.CheckpointEligible( false, () => { tAutoCheckpointAsked = true; return true; } ) && !tAutoCheckpointAsked,
    "a scene already saved on disk never asks whether it can be saved" );

var tAutoNow = new DateTime( 2026, 9, 29, 12, 0, 0, DateTimeKind.Utc );
IdleSaveInput TAutoIdle( bool enabled, bool hasUnsavedChanges = true, bool peerEditing = false, bool inPlayMode = false,
    DateTime? lastLocalEdit = null, DateTime? lastAutoSave = null, Func<bool> canSave = null ) => new(
    Enabled: enabled,
    HasUnsavedChanges: hasUnsavedChanges,
    PeerEditing: peerEditing,
    InPlayMode: inPlayMode,
    LastLocalEdit: lastLocalEdit ?? tAutoNow.AddSeconds( -60 ),
    LastAutoSave: lastAutoSave.GetValueOrDefault(),
    Now: tAutoNow,
    CanSave: canSave ?? ( () => true ) );

Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: false ) ),
    "the idle auto-save is off by default" );
Assert( AutoSaveRules.Eligible( TAutoIdle( enabled: true ) ),
    "an armed, idle, unplayed, unattended scene with work to save is eligible" );
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, peerEditing: true ) ),
    "a collaborator reported editing blocks the idle save" );
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, inPlayMode: true ) ),
    "play mode blocks the idle save" );
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, hasUnsavedChanges: false ) ),
    "a scene already saved on disk has nothing to save" );
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, canSave: () => false ) ),
    "the idle save defers to Save's own refusals" );
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, lastLocalEdit: tAutoNow.AddSeconds( -AutoSaveRules.IdleSeconds + 1 ) ) ),
    "a scene edited seconds ago has not been idle long enough" );
Assert( AutoSaveRules.Eligible( TAutoIdle( enabled: true, lastLocalEdit: tAutoNow.AddSeconds( -AutoSaveRules.IdleSeconds ) ) ),
    "the idle threshold itself counts as idle" );
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, lastAutoSave: tAutoNow.AddSeconds( -AutoSaveRules.MinIntervalSeconds + 1 ) ) ),
    "an automatic save inside the minimum interval is refused" );
Assert( AutoSaveRules.Eligible( TAutoIdle( enabled: true, lastAutoSave: tAutoNow.AddSeconds( -AutoSaveRules.MinIntervalSeconds ) ) ),
    "once the minimum interval has elapsed the next automatic save is eligible" );
Assert( !AutoSaveRules.Eligible( new IdleSaveInput( Enabled: true, HasUnsavedChanges: true, PeerEditing: false,
    InPlayMode: false, LastLocalEdit: default, LastAutoSave: default, Now: tAutoNow, CanSave: () => true ) ),
    "an editor that has never been seen editing is not treated as idle" );

// The expensive half of the question — capturing the scene and diffing it — must not be asked
// once a cheaper fact has already said no.
var tAutoIdleNeverAsked = false;
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: false, canSave: () => { tAutoIdleNeverAsked = true; return true; } ) ) && !tAutoIdleNeverAsked,
    "an unarmed idle save never asks the expensive save question" );
var tAutoIdleTooFresh = false;
Assert( !AutoSaveRules.Eligible( TAutoIdle( enabled: true, lastLocalEdit: tAutoNow, canSave: () => { tAutoIdleTooFresh = true; return true; } ) ) && !tAutoIdleTooFresh,
    "a scene edited seconds ago never asks the expensive save question" );

// Wave B — automatic recovery. The classification of a recorded failure is decided ONCE,
// where the failure is recorded, from the exception behind it, so everything below is a
// decision from that exception alone: a transport or dial failure is something to retry, a
// policy refusal and a message with no exception behind it are something to stop on.
Assert( AutoRecoveryRules.Classify( new IOException( "disconnected" ) ) == SessionFailureClass.Transient,
    "a transport failure that stored its own fault is transient" );
Assert( AutoRecoveryRules.Classify( new System.Net.Sockets.SocketException() ) == SessionFailureClass.Transient,
    "a refused dial is transient" );
Assert( AutoRecoveryRules.Classify( new TimeoutException() ) == SessionFailureClass.Transient,
    "a dial that ran out of time is transient" );
Assert( AutoRecoveryRules.Classify( new AggregateException( new IOException( "disconnected" ) ) ) == SessionFailureClass.Transient,
    "a faulted connection task is classified by the failure it carries" );
Assert( AutoRecoveryRules.Classify( new AggregateException() ) == SessionFailureClass.Terminal,
    "an aggregate with nothing inside it is not a licence to retry" );
Assert( AutoRecoveryRules.Classify( new InvalidOperationException( "Stop play mode first" ) ) == SessionFailureClass.Terminal &&
    AutoRecoveryRules.Classify( new ArgumentException( "bad plan" ) ) == SessionFailureClass.Terminal,
    "a policy refusal is terminal" );
Assert( AutoRecoveryRules.Classify( null ) == SessionFailureClass.Terminal &&
    default( SessionFailureClass ) == SessionFailureClass.Terminal,
    "a failure with no exception behind it fails closed" );

// The backoff between attempts: one second, then four, then ten, so a host that is
// restarting is given a chance to come back before the last attempt is spent.
Assert( AutoRecoveryRules.Backoff( 0 ) == TimeSpan.FromSeconds( 1 ) &&
    AutoRecoveryRules.Backoff( 1 ) == TimeSpan.FromSeconds( 4 ) &&
    AutoRecoveryRules.Backoff( 2 ) == TimeSpan.FromSeconds( 10 ) &&
    AutoRecoveryRules.Backoff( 3 ) == TimeSpan.FromSeconds( 10 ),
    "attempts back off at one second, four seconds and ten seconds" );

// What a silent retry must never do, decided from injected facts rather than engine state.
AutoRecoveryInput TAutoRecover( bool enabled = true, SessionFailureClass failureClass = SessionFailureClass.Transient,
    bool terminal = false, bool hosted = false, bool unsaved = false, bool accepted = true,
    bool inFlight = false, int attemptsUsed = 0, bool errorRecorded = true,
    DateTime? now = null, DateTime? nextAttemptAt = null ) => new(
        enabled, errorRecorded, failureClass, terminal, hosted, unsaved,
        accepted, inFlight, attemptsUsed, now ?? DateTime.UnixEpoch, nextAttemptAt ?? DateTime.UnixEpoch );

Assert( !AutoRecoveryRules.Eligible( TAutoRecover( enabled: false ) ),
    "automatic recovery is off by default" );
Assert( AutoRecoveryRules.Eligible( TAutoRecover() ),
    "an armed transient outage the reconnect guard accepts is eligible" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( failureClass: SessionFailureClass.Terminal ) ),
    "a failure that never identified itself as transient is never retried" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( terminal: true ) ),
    "a fail-stop teardown is never retried" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( hosted: true ) ),
    "a hosted session is never retried in place" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( unsaved: true ) ),
    "unsaved local changes are never reloaded over without being asked" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( accepted: false ) ),
    "a reconnect the guard would refuse is never attempted automatically" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( errorRecorded: false ) ),
    "there is nothing to recover from without a failure on record" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( inFlight: true ) ),
    "one attempt at a time: a second request never overlaps the one already running" );
var tAutoRecoverNow = DateTime.UnixEpoch;
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( attemptsUsed: 1, now: tAutoRecoverNow,
        nextAttemptAt: tAutoRecoverNow.AddSeconds( 1 ) ) ),
    "an attempt inside its own backoff does not start a second one" );
Assert( AutoRecoveryRules.Eligible( TAutoRecover( attemptsUsed: 1, now: tAutoRecoverNow.AddSeconds( 4 ),
        nextAttemptAt: tAutoRecoverNow.AddSeconds( 1 ) ) ),
    "an attempt whose backoff has elapsed is eligible again" );
Assert( AutoRecoveryRules.Eligible( TAutoRecover( attemptsUsed: AutoRecoveryRules.MaxAttempts - 1 ) ),
    "the last attempt of the budget is still spent" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( attemptsUsed: AutoRecoveryRules.MaxAttempts ) ),
    "the attempt budget is three, and the third refusal is the end of it" );
Assert( !AutoRecoveryRules.Eligible( TAutoRecover( attemptsUsed: AutoRecoveryRules.MaxAttempts + 1 ) ),
    "a spent budget can never become eligible again" );

// The prompt that stands in for a retry while the toggle is off: exactly one per outage,
// only for a transient failure, and only when the action it names would be accepted.
Assert( AutoRecoveryRules.PromptOffered( autoRecoverEnabled: false, errorRecorded: true,
        failureClass: SessionFailureClass.Transient, terminalTeardown: false,
        reconnectAccepted: true, promptShown: false ),
    "a transient outage prompts first, because the toggle is off by default" );
Assert( !AutoRecoveryRules.PromptOffered( autoRecoverEnabled: false, errorRecorded: true,
        failureClass: SessionFailureClass.Transient, terminalTeardown: false,
        reconnectAccepted: true, promptShown: true ),
    "an outage prompts once, not once per frame" );
Assert( !AutoRecoveryRules.PromptOffered( autoRecoverEnabled: true, errorRecorded: true,
        failureClass: SessionFailureClass.Transient, terminalTeardown: false,
        reconnectAccepted: true, promptShown: false ),
    "an armed recovery reports its own outcome rather than asking" );
Assert( !AutoRecoveryRules.PromptOffered( autoRecoverEnabled: false, errorRecorded: true,
        failureClass: SessionFailureClass.Terminal, terminalTeardown: false,
        reconnectAccepted: true, promptShown: false ),
    "a terminal failure is not offered for recovery" );
Assert( !AutoRecoveryRules.PromptOffered( autoRecoverEnabled: false, errorRecorded: true,
        failureClass: SessionFailureClass.Transient, terminalTeardown: true,
        reconnectAccepted: true, promptShown: false ),
    "a fail-stop teardown is not offered for recovery" );
Assert( !AutoRecoveryRules.PromptOffered( autoRecoverEnabled: false, errorRecorded: true,
        failureClass: SessionFailureClass.Transient, terminalTeardown: false,
        reconnectAccepted: false, promptShown: false ),
    "the prompt never offers an action the reconnect guard would refuse" );
Assert( !AutoRecoveryRules.PromptOffered( autoRecoverEnabled: false, errorRecorded: false,
        failureClass: SessionFailureClass.Transient, terminalTeardown: false,
        reconnectAccepted: true, promptShown: false ),
    "a session with no failure on record has nothing to prompt about" );

// ---- Wave B residuals: refusal routing, and the line a refusal lands on ------
// A refusal of an action the session never entered is its own kind rather than a failure,
// which is what keeps Recover pressed on a healthy connected session off the status line.
Assert( ActionRefusalRules.IsRefusal( new ActionRefusal( "A hosted session cannot be recovered in place." ) ),
    "a guard refusal of an action never entered routes as a refusal" );
Assert( !ActionRefusalRules.IsRefusal( new InvalidOperationException( "the transport closed" ) ),
    "a failure an action really raised still routes as a failure" );
Assert( !ActionRefusalRules.IsRefusal( null ),
    "no exception at all is not a refusal" );
Assert( !ActionRefusalRules.IsRefusal( new Exception( "Already connected to a session." ) ),
    "the wording alone never decides the routing — only the type raised with it does" );

// The controls card's message line. A notice may fill a line that would be empty or extend
// the line a real failure already owns; it may never be spoken in a failure's place.
Assert( ControlsMessageRules.Compose( null, "transport failed", "/recovery/archive.json",
        null, "A hosted session cannot be recovered in place.", null, null )
        == "transport failed\nRecovery: /recovery/archive.json\nA hosted session cannot be recovered in place.",
    "a refusal notice is appended under a recorded error instead of displacing it" );
Assert( ControlsMessageRules.Compose( null, "transport failed", null, null, null, null, null )
        == "transport failed" && ControlsMessageRules.Compose( null, "transport failed", "", null, "note", null, null ) == "transport failed\nnote",
    "an error with no archive shows no empty Recovery label, and a refusal notice still follows it" );
Assert( ControlsMessageRules.Compose( null, null, null, null, "Already connected to a session.", null, null )
        == "Already connected to a session.",
    "with no failure on record the refusal notice carries the line" );
Assert( ControlsMessageRules.Compose( null, null, null, null, "refused", "undo notice", "save notice" )
        == "refused",
    "the refusal of the control just pressed outranks older transients" );
Assert( ControlsMessageRules.Compose( "fail-stop", "transport failed", "/archive", "recovery", "refused", "undo", "save" )
        == "fail-stop",
    "a terminal teardown failure outranks everything" );
Assert( ControlsMessageRules.Compose( null, null, null, "recovery notice", "refused", "undo", "save" )
        == "recovery notice",
    "a lifecycle recovery notice outranks a refusal notice" );
Assert( ControlsMessageRules.Compose( null, null, null, null, null, "undo notice", "save notice" ) == "undo notice",
    "the undo and save notices keep the order they had" );
Assert( ControlsMessageRules.Compose( null, null, null, null, null, null, null ) == "",
    "with nothing to say the line is empty, so the card hides it" );


// ---- Relay: derivations, control messages, frames, URL rules ---------------------------------
// The relay learns a room id and two proofs, all one-way derivations of the invite secret. These cases
// pin that none of them equals, contains or can reproduce the session key, and that the wire shapes a
// stranger can send are parsed strictly.
// Own names on purpose: the invite section above declares similar ones inside an inner block, and a top-level
// local of the same name would collide with it.
var relayProjectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";
var relayInviteSecret = Enumerable.Range( 0, 16 ).Select( i => (byte)(i * 16 + 1) ).ToArray();
var relayDirectInvite = new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]> { new byte[] { 10, 0, 0, 5 }, new byte[] { 192, 168, 1, 9 } } };
var relaySecret = Enumerable.Range( 0, 16 ).Select( i => (byte)(i * 7 + 3) ).ToArray();
var relayRoom = RelayProtocol.DeriveRoom( relaySecret );
var relayHostProof = RelayProtocol.DeriveHostProof( relaySecret );
var relayGuestProof = RelayProtocol.DeriveGuestProof( relaySecret );
var relaySessionKey = InviteCode.DeriveKey( relaySecret, relayProjectId );
Assert( relayRoom.Length == 16 && relayHostProof.Length == 32 && relayGuestProof.Length == 32, "relay derivations have the documented sizes" );
Assert( !relayHostProof.SequenceEqual( relayGuestProof ) && !relayHostProof.Take( 16 ).SequenceEqual( relayRoom ) && !relayGuestProof.Take( 16 ).SequenceEqual( relayRoom ),
    "the room id, the host proof and the guest proof are independent of each other" );
Assert( !relayHostProof.SequenceEqual( relaySessionKey ) && !relayGuestProof.SequenceEqual( relaySessionKey ) && !relayRoom.SequenceEqual( relaySessionKey.Take( 16 ).ToArray() ),
    "nothing the relay can see equals the session key" );
Assert( RelayProtocol.DeriveRoom( relaySecret ).SequenceEqual( relayRoom ) && !RelayProtocol.DeriveRoom( relayInviteSecret ).SequenceEqual( relayRoom ),
    "a room id is stable for one secret and different for another" );
try { RelayProtocol.DeriveRoom( new byte[8] ); Assert( false, "a room cannot be derived from a short secret" ); }
catch ( ArgumentException ) { Assert( true, "a room cannot be derived from a short secret" ); }

var hostHelloText = RelayProtocol.BuildHostHello( relaySecret, "the-key" );
Assert( RelayProtocol.TryParseControl( hostHelloText, out var hostHello, out _ ) && hostHello.Type == "host" && hostHello.AccessKey == "the-key" &&
    hostHello.Room == RelayProtocol.Hex( relayRoom ) && hostHello.HostProof == RelayProtocol.Hex( relayHostProof ) && hostHello.GuestProof == RelayProtocol.Hex( relayGuestProof ),
    "a host hello carries the room, both proofs and the access key" );
Assert( !hostHelloText.Contains( Convert.ToHexString( relaySecret ).ToLowerInvariant() ) && !hostHelloText.Contains( Convert.ToHexString( relaySessionKey ).ToLowerInvariant() ),
    "no hello ever contains the invite secret or the session key" );
Assert( !RelayProtocol.BuildHostHello( relaySecret, null ).Contains( "\"key\"" ), "a host without an access key sends no key field" );
var guestHelloText = RelayProtocol.BuildGuestHello( relaySecret );
Assert( RelayProtocol.TryParseControl( guestHelloText, out var guestHello, out _ ) && guestHello.Type == "guest" && guestHello.Proof == RelayProtocol.Hex( relayGuestProof ) && guestHello.HostProof == null,
    "a guest hello carries the room and only the guest proof, never the host proof" );
Assert( RelayProtocol.ValidateHello( hostHello, out var vRoom, out var vHostProof, out var vGuestProof, out _ ) == null &&
    vRoom.SequenceEqual( relayRoom ) && vHostProof.SequenceEqual( relayHostProof ) && vGuestProof.SequenceEqual( relayGuestProof ),
    "a well-formed host hello validates and yields the raw proofs" );
foreach ( var (label, mutate) in new (string, Action<RelayControl>)[]
{
    ("an upper-case room", h => h.Room = h.Room.ToUpperInvariant()),
    ("a short room", h => h.Room = h.Room.Substring( 2 )),
    ("a non-hex room", h => h.Room = new string( 'g', 32 )),
    ("a short host proof", h => h.HostProof = h.HostProof.Substring( 4 )),
    ("another protocol version", h => h.Version = 2),
    ("an unknown type", h => h.Type = "observer"),
} )
{
    RelayProtocol.TryParseControl( hostHelloText, out var mutated, out _ );
    mutate( mutated );
    Assert( RelayProtocol.ValidateHello( mutated, out _, out _, out _, out _ ) != null, "a hello with " + label + " is refused" );
}
Assert( !RelayProtocol.TryParseControl( "", out _, out _ ) && !RelayProtocol.TryParseControl( "{", out _, out _ ) && !RelayProtocol.TryParseControl( "{}", out _, out _ ) &&
    !RelayProtocol.TryParseControl( new string( 'x', RelayProtocol.MaxControlChars + 1 ), out _, out _ ), "empty, malformed, typeless and oversized control messages are refused" );

var frame = RelayProtocol.BuildFrame( RelayFrameType.Data, 0xA1B2C3D4u, new byte[] { 9, 8, 7 } );
Assert( RelayProtocol.TryParseFrame( frame, out var frameType, out var frameChannel, out var framePayload ) && frameType == RelayFrameType.Data && frameChannel == 0xA1B2C3D4u && framePayload.ToArray().SequenceEqual( new byte[] { 9, 8, 7 } ),
    "a relay frame round-trips its type, channel and payload" );
Assert( !RelayProtocol.TryParseFrame( new byte[] { 1, 0, 0 }, out _, out _, out _ ) && !RelayProtocol.TryParseFrame( new byte[] { 9, 0, 0, 0, 1 }, out _, out _, out _ ) &&
    !RelayProtocol.TryParseFrame( new byte[] { 0, 0, 0, 0, 1 }, out _, out _, out _ ) && !RelayProtocol.TryParseFrame( new byte[] { 2, 0, 0, 0, 1, 5 }, out _, out _, out _ ),
    "a truncated frame, an unknown type and an Open frame carrying a payload are all refused" );
Assert( RelayProtocol.TryParseFrame( RelayProtocol.BuildFrame( RelayFrameType.Close, 5, ReadOnlySpan<byte>.Empty ), out var closeType, out var closeChannel, out _ ) && closeType == RelayFrameType.Close && closeChannel == 5,
    "a Close frame carries no payload" );

string UrlError( string url, bool anyPort = false ) => RelayProtocol.TryValidateUrl( url, out _, out var e, anyPort ) ? null : e;
Assert( UrlError( "wss://relay.example.com/relay" ) == null && UrlError( "wss://relay.example.com" ) == null && UrlError( "wss://relay.example.com:8443/relay" ) == null, "a wss:// address on a DNS name is accepted" );
Assert( UrlError( "ws://localhost:8080/relay" ) == null && UrlError( "ws://127.0.0.1:443/relay" ) == null, "a loopback relay on an allowed port is accepted" );
Assert( UrlError( "ws://localhost:9000/relay" ) != null && UrlError( "ws://localhost:9000/relay", anyPort: true ) == null, "a loopback relay on any other port is refused unless the test switch is set" );
Assert( UrlError( "ws://relay.example.com/relay" ) != null, "plain ws:// to another machine is refused because the hello proofs would cross the internet in the clear" );
Assert( UrlError( "wss://203.0.113.9/relay" ) != null && UrlError( "wss://[2001:db8::1]/relay" ) != null, "an IP address is refused because the editor refuses IP addresses" );
Assert( UrlError( "https://relay.example.com/relay" ) != null && UrlError( "relay.example.com" ) != null && UrlError( "" ) != null && UrlError( null ) != null, "a non-WebSocket scheme, a bare host and an empty address are refused" );
Assert( UrlError( "wss://user:pass@relay.example.com/relay" ) != null && UrlError( "wss://relay.example.com/relay#x" ) != null, "credentials or a fragment inside the address are refused" );
Assert( UrlError( "wss://" + new string( 'a', 200 ) + ".example.com/relay" ) != null, "an address longer than the limit is refused" );
Assert( RelayProtocol.UserMessage( "no-host", null ).Contains( "host is not connected" ) && RelayProtocol.UserMessage( "bad-key", null ).Contains( "access key" ) &&
    RelayProtocol.UserMessage( "made-up", "detail" ).Contains( "detail" ) && RelayProtocol.UserMessage( "made-up", null ).Length > 0, "every relay refusal code has a sentence, and an unknown code still reads well" );
Assert( RelayProtocol.IsTerminal( "bad-key" ) && RelayProtocol.IsTerminal( "room-taken" ) && !RelayProtocol.IsTerminal( "limit" ) && !RelayProtocol.IsTerminal( "no-host" ),
    "a wrong key ends the host's retries while a capacity limit is retried" );

// ---- Invite code format 2 carries the relay ----------------------------------------------------
var relayInvite = new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]> { new byte[] { 94, 49, 37, 128 } }, Relay = "wss://relay.example.com/relay" };
var relayInviteCode = InviteCode.Encode( relayInvite );
Assert( InviteCode.TryDecode( relayInviteCode, relayProjectId, out var relayDecoded, out var relayDecodeError ) && relayDecodeError is null &&
    relayDecoded.Relay == "wss://relay.example.com/relay" && relayDecoded.Port == 27845 && relayDecoded.Secret.SequenceEqual( relayInviteSecret ) && relayDecoded.Endpoints.Count == 1 && relayDecoded.Endpoints[0].SequenceEqual( new byte[] { 94, 49, 37, 128 } ),
    "an invite code round-trips its relay address alongside the direct endpoint" );
Assert( InviteCode.Encode( new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]>() } ) ==
    InviteCode.Encode( new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]>(), Relay = null } ) &&
    InviteCode.TryDecode( InviteCode.Encode( relayDirectInvite ), out var directOnly, out _ ) && directOnly.Relay is null,
    "a direct-only invite still encodes as format 1 and decodes with no relay" );
Assert( relayInviteCode.Length <= 130, "a typical relay invite stays short enough to read out or paste (" + relayInviteCode.Length + " characters)" );
Assert( InviteCode.TryDecode( new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]>(), Relay = "wss://relay.example.com:8443/relay" } is var portRelay ? InviteCode.Encode( portRelay ) : "", out var portRelayData, out _ ) && portRelayData.Relay == "wss://relay.example.com:8443/relay",
    "a relay on a non-default port survives the code" );
Assert( InviteCode.TryDecode( InviteCode.Encode( new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]>(), Relay = "ws://localhost:8080/relay" } ), out var localRelayData, out _ ) && localRelayData.Relay == "ws://localhost:8080/relay",
    "a loopback ws:// relay survives the code" );
Assert( InviteCode.TryDecode( InviteCode.Encode( new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]>(), Relay = "wss://relay.example.com/custom/path" } ), out var pathRelayData, out _ ) && pathRelayData.Relay == "wss://relay.example.com/custom/path",
    "a relay with a non-default path survives the code" );
try { InviteCode.Encode( new InviteData { Port = 27845, ProjectId = relayProjectId, Secret = relayInviteSecret, Endpoints = new List<byte[]>(), Relay = "wss://203.0.113.9/relay" } ); Assert( false, "a code cannot be created for an IP-address relay" ); }
catch ( ArgumentException ) { Assert( true, "a code cannot be created for an IP-address relay" ); }
var relayBody = relayInviteCode.Substring( InviteCode.Prefix.Length ).Replace( "-", "" );
Assert( InviteCodeError( InviteCode.Prefix + relayBody.Substring( 0, relayBody.Length - 6 ) ) is { Length: > 0 } && InviteCodeError( relayInviteCode + "A" ) is { Length: > 0 },
    "a truncated or extended relay invite is refused with a reason" );
string InviteCodeError( string text ) => InviteCode.TryDecode( text, out _, out var e ) ? null : e;

// ---- Config ------------------------------------------------------------------------------------
var relayConfigPath = Path.Combine( Path.GetTempPath(), "relay-config-" + Guid.NewGuid().ToString( "N" ) + ".json" );
File.WriteAllText( relayConfigPath, "{ \"ProjectId\": \"" + relayProjectId + "\", \"RelayUrl\": \"wss://relay.example.com/relay\" }" );
var relayConfig = CollaborationConfig.LoadFrom( relayConfigPath, out var relayConfigError );
Assert( relayConfigError is null && relayConfig.RelayUrl == "wss://relay.example.com/relay", "the shared config carries the relay address" );
File.WriteAllText( relayConfigPath, "{ \"RelayUrl\": \"wss://203.0.113.9/relay\" }" );
var badRelayConfig = CollaborationConfig.LoadFrom( relayConfigPath, out var badRelayConfigError );
Assert( badRelayConfig.RelayUrl is null && badRelayConfigError is { Length: > 0 } && badRelayConfigError.Contains( "RelayUrl" ), "an unusable relay address is dropped and named in the config error instead of throwing" );
File.WriteAllText( relayConfigPath, "{ \"RelayUrl\": 5 }" );
CollaborationConfig.LoadFrom( relayConfigPath, out var numericRelayError );
Assert( numericRelayError is { Length: > 0 }, "a non-string relay address is reported" );
File.WriteAllText( relayConfigPath, "{ }" );
Assert( CollaborationConfig.LoadFrom( relayConfigPath, out _ ).RelayUrl is null && CollaborationConfig.Defaults().RelayUrl is null, "with no relay configured only the direct routes are offered" );
File.Delete( relayConfigPath );

// ---- RelayPipe ---------------------------------------------------------------------------------
var pipeSent = new List<byte[]>();
var pipeClosedCalls = 0;
var pipe = new RelayPipe( ( data, ct ) => { pipeSent.Add( data.ToArray() ); return ValueTask.CompletedTask; }, () => pipeClosedCalls++ );
await pipe.WriteAsync( new byte[RelayProtocol.MaxChunkBytes * 2 + 10] );
Assert( pipeSent.Count == 3 && pipeSent.All( c => c.Length <= RelayProtocol.MaxChunkBytes ) && pipeSent.Sum( c => c.Length ) == RelayProtocol.MaxChunkBytes * 2 + 10, "a write larger than a relay message is split into chunks that add up to the whole" );
pipe.Feed( new byte[] { 1, 2, 3, 4, 5 } );
var pipeBuffer = new byte[3];
Assert( await pipe.ReadAsync( pipeBuffer ) == 3 && pipeBuffer.SequenceEqual( new byte[] { 1, 2, 3 } ) && await pipe.ReadAsync( pipeBuffer ) == 2 && pipeBuffer[0] == 4 && pipeBuffer[1] == 5,
    "reads return buffered bytes in order across message boundaries" );
Assert( pipe.BufferedBytes == 0, "the buffered-byte count returns to zero once everything is read" );
pipe.CompleteFromPeer();
Assert( await pipe.ReadAsync( pipeBuffer ) == 0 && pipeClosedCalls == 0 && pipe.IsClosed, "a peer close ends reads with end-of-stream and does not echo a close back" );
try { await pipe.WriteAsync( new byte[1] ); Assert( false, "writing to a closed pipe fails" ); } catch ( IOException ) { Assert( true, "writing to a closed pipe fails" ); }
var closeOnce = new RelayPipe( ( d, c ) => ValueTask.CompletedTask, () => pipeClosedCalls++ );
closeOnce.Dispose();
closeOnce.Dispose();
Assert( pipeClosedCalls == 1, "disposing a pipe notifies its owner exactly once" );
var overflow = new RelayPipe( ( d, c ) => ValueTask.CompletedTask );
var overflowChunk = new byte[4 * 1024 * 1024];
var overflowAccepted = 0;
for ( var i = 0; i < 12; i++ ) if ( overflow.Feed( overflowChunk ) ) overflowAccepted++;
Assert( overflow.IsClosed && overflowAccepted == RelayPipe.MaxBufferedBytes / overflowChunk.Length, "a reader that never drains cannot make the pipe grow past its budget; the pipe closes instead" );
try { pipe.Read( new byte[1], 0, 1 ); Assert( false, "a synchronous read is refused" ); } catch ( NotSupportedException ) { Assert( true, "a synchronous read is refused" ); }


// ---- Route order, route failures, access key file, join-failure wording ------------------------
var routeInvite = new InviteData { Port = 27845, Secret = relayInviteSecret, Endpoints = new List<byte[]> { new byte[] { 94, 49, 37, 128 } }, Relay = "wss://relay.example.com/relay" };
var routeLan = new List<DiscoveredHost> { new( "192.168.1.20", 27845, "lan-host", new string( 'a', 32 ), "deadbeef", DateTime.UtcNow ) };
var routeOrder = EndpointResolver.Order( routeInvite, routeLan, "10.9.8.7", 27845 ).ToList();
Assert( routeOrder.Select( c => c.Source ).SequenceEqual( new[] { "lan", "relay", "code", "advanced" } ),
    "routes run the local network first, then the relay, then the code's public address, then the advanced address" );
var relayRoute = routeOrder.First( c => c.Source == "relay" );
Assert( relayRoute.Address == "wss://relay.example.com/relay" && relayRoute.Port == 0, "the relay route carries its full URL and no port" );
Assert( EndpointResolver.Order( new InviteData { Port = 27845, Secret = relayInviteSecret, Endpoints = new List<byte[]>() }, null, null, 0 ).Count() == 0,
    "a code with no relay, no address and nothing discovered yields no route at all" );
Assert( EndpointResolver.Order( routeInvite, null, null, 0 ).Select( c => c.Source ).SequenceEqual( new[] { "relay", "code" } ),
    "a relay invite offers the relay before its direct address when nothing is discovered" );

string RouteText( params RouteAttempt[] a ) => RouteFailureRules.Summarize( a );
var actively = "No connection could be made because the target machine actively refused it.";
var direct = RouteText( new RouteAttempt( "code", "203.0.113.42:27845", actively ) );
Assert( direct.Contains( "203.0.113.42:27845" ) && direct.Contains( "no relay route" ) && direct.Contains( "RelayUrl" ) && direct.Contains( "forward" ),
    "a refused direct dial with no relay says the code has no relay and names the three fixes" );
var noHost = RouteText( new RouteAttempt( "relay", "wss://relay.example.com/relay", "refused", "no-host" ), new RouteAttempt( "code", "203.0.113.42:27845", actively ) );
Assert( noHost.Contains( "host is not connected" ) && noHost.Contains( "restart" ) && !noHost.Contains( "no relay route" ),
    "a reachable relay with no host says the host is not connected and asks for a new code" );
var relayDown = RouteText( new RouteAttempt( "relay", "wss://relay.example.com/relay", "Could not reach the relay: timed out" ), new RouteAttempt( "code", "1.2.3.4:27845", actively ) );
Assert( relayDown.Contains( "relay route failed" ) && relayDown.Contains( "relay is running" ), "an unreachable relay is reported as such and the host is asked to confirm it runs" );
Assert( RouteText() .Contains( "no route" ) && RouteText( null ).Contains( "no route" ), "no routes at all is stated plainly" );
Assert( RouteText( new RouteAttempt( "code", "1.2.3.4:1", new string( 'x', 3000 ) ) ).Length <= RouteFailureRules.MaxLength, "a summary is bounded even for a hostile reason string" );
Assert( !RouteText( new RouteAttempt( "code", "1.2.3.4:1", "line one\r\nline two" ) ).Contains( "\n" ), "a multi-line reason is folded to one line" );

Assert( RelayAccessKeyFile.Parse( "# comment\n\n  abc123-KEY  \nignored", out var keyErr ) == "abc123-KEY" && keyErr is null, "the access key is the first non-blank, non-comment line, trimmed" );
Assert( RelayAccessKeyFile.Parse( "", out _ ) is null && RelayAccessKeyFile.Parse( null, out _ ) is null && RelayAccessKeyFile.Parse( "# only a comment\n", out _ ) is null, "an empty or comment-only file means no key" );
Assert( RelayAccessKeyFile.Parse( "has space", out var spaceErr ) is null && spaceErr is { Length: > 0 } && RelayAccessKeyFile.Parse( new string( 'k', 200 ), out var longErr ) is null && longErr is { Length: > 0 },
    "a key with a space or over the length limit is refused with a reason instead of being sent" );

Assert( CollaborationFailureRules.Compose( null, "refused", null, everConnected: false ).Status == "Could not connect: refused", "a join that never connected does not claim synchronization stopped" );
Assert( CollaborationFailureRules.Compose( null, "refused", null ).Status == "Synchronization stopped: refused" &&
    CollaborationFailureRules.Compose( null, "lost", null, everConnected: true ).Status == "Synchronization stopped: lost", "a session that was live still reports that synchronization stopped" );


// ---- Presence sphere style ---------------------------------------------------------------------
Assert( PresenceStyle.Radius( 0 ) == PresenceStyle.MinRadius && PresenceStyle.Radius( -5 ) == PresenceStyle.MinRadius && PresenceStyle.Radius( float.NaN ) == PresenceStyle.MinRadius && PresenceStyle.Radius( float.PositiveInfinity ) == PresenceStyle.MinRadius,
    "the sphere radius never collapses for a zero, negative or non-finite distance" );
Assert( Math.Abs( PresenceStyle.Radius( 1000 ) - 45f ) < 1e-3f && PresenceStyle.Radius( 1e9f ) == PresenceStyle.MaxRadius, "the sphere grows with distance to keep a steady size on screen and stops at its ceiling" );
var radiusSamples = new[] { 0f, 100f, 300f, 800f, 2000f, 5000f }.Select( PresenceStyle.Radius ).ToArray();
Assert( radiusSamples.Zip( radiusSamples.Skip( 1 ) ).All( pair => pair.Second >= pair.First ), "the radius never shrinks as a collaborator moves away" );
Assert( Math.Abs( PresenceStyle.Bob( 0, 10f ) ) < 1e-5f && Enumerable.Range( 0, 64 ).All( i => Math.Abs( PresenceStyle.Bob( i * 0.1, 10f ) ) <= PresenceStyle.BobAmplitude * 10f + 1e-4f ) &&
    Math.Abs( PresenceStyle.Bob( PresenceStyle.BobPeriodSeconds / 4.0, 10f ) - PresenceStyle.BobAmplitude * 10f ) < 1e-3f, "the bob is a bounded sine with its peak a quarter period in" );
Assert( PresenceStyle.Pulse( "idle", 3.3 ) == 1f && PresenceStyle.Pulse( "anything", 1.0 ) == 1f && PresenceStyle.Pulse( "playing", 0.4 ) > 1f && PresenceStyle.Pulse( "editing", 0.1 ) >= 1f &&
    Enumerable.Range( 0, 200 ).All( i => PresenceStyle.Pulse( "playing", i * 0.05 ) <= 1.081f && PresenceStyle.Pulse( "editing", i * 0.05 ) <= 1.051f ), "only active states pulse, and the pulse stays within a few percent" );
Assert( PresenceStyle.Fade( 0 ) == 1f && PresenceStyle.Fade( PresenceStyle.FullOpacityDistance ) == 1f && PresenceStyle.Fade( PresenceStyle.FarDistance ) == PresenceStyle.FarOpacity && PresenceStyle.Fade( 1e9f ) == PresenceStyle.FarOpacity &&
    PresenceStyle.Fade( 3000 ) < 1f && PresenceStyle.Fade( 3000 ) > PresenceStyle.FarOpacity && PresenceStyle.Fade( float.NaN ) == 1f, "labels are fully opaque up close, dim with distance, and never vanish" );

var layers = PresenceStyle.Layers;
Assert( layers.Length == 7, "the sphere is seven layers: three halo shells, the body, two lit faces and a glint" );
Assert( layers.Take( 3 ).All( l => l.RadiusScale > 1f && l.Alpha < 0.3f ) && layers.Take( 3 ).Zip( layers.Skip( 1 ).Take( 3 ) ).All( pair => pair.First.RadiusScale > pair.Second.RadiusScale ),
    "the halo shells are larger than the body, faint, and shrink toward it so they draw back to front" );
Assert( layers[3].RadiusScale == 1f && layers[3].Alpha == 1f && layers[3].Tone == 0f && layers[3].OffsetRight == 0f && layers[3].OffsetUp == 0f, "the body is a centred, opaque sphere of exactly the marker radius" );
Assert( layers.Skip( 4 ).All( l => l.OffsetRight < 0f && l.OffsetUp > 0f && l.OffsetToward > 0f ), "every lit layer sits toward the upper left and the viewer, so the light comes from one side" );
Assert( layers.Skip( 4 ).Zip( layers.Skip( 5 ) ).All( pair => pair.First.RadiusScale > pair.Second.RadiusScale && pair.First.Tone < pair.Second.Tone ), "lit layers get smaller and brighter toward the glint" );
Assert( layers.Skip( 4 ).Take( 2 ).All( l => Math.Sqrt( l.OffsetRight * l.OffsetRight + l.OffsetUp * l.OffsetUp + l.OffsetToward * l.OffsetToward ) + l.RadiusScale <= 1.06 ),
    "the lit faces stay inside the body silhouette, so the outline is always a clean circle" );
Assert( PresenceStyle.Deep.B > PresenceStyle.Deep.R && PresenceStyle.Base.B > PresenceStyle.Base.R + 0.5f && PresenceStyle.Base.B > PresenceStyle.Base.G && PresenceStyle.Light.B >= PresenceStyle.Light.G && PresenceStyle.Light.G > PresenceStyle.Light.R,
    "the palette is blue at every step, from the deep body to the light face" );
var bodyColor = PresenceStyle.LayerColor( layers[3], false );
var glintColor = PresenceStyle.LayerColor( layers[6], false );
Assert( bodyColor == PresenceStyle.Deep && glintColor == PresenceStyle.Glint && glintColor.R > bodyColor.R && glintColor.G > bodyColor.G, "the body is the deep blue and the glint is the near-white highlight" );
Assert( layers.All( l => { var c = PresenceStyle.LayerColor( l, false ); return c.B >= c.R && c.B >= c.G - 0.001f; } ), "no layer of an idle marker is anything but blue or white" );
var playingBody = PresenceStyle.LayerColor( layers[3], true );
var playingFace = PresenceStyle.LayerColor( layers[5], true );
Assert( playingBody == PresenceStyle.Deep && playingFace.G > PresenceStyle.LayerColor( layers[5], false ).G - 0.001f && playingFace.B > playingFace.R, "a playing marker keeps its blue body and only warms its lit face toward green" );
Assert( PresenceStyle.StateColor( "playing" ) == PresenceStyle.Playing && PresenceStyle.StateColor( "editing" ) == PresenceStyle.Light && PresenceStyle.StateColor( null ) == PresenceStyle.StateColor( "idle" ), "each state has its own status colour and an unknown state reads as idle" );
var ring = PresenceStyle.BadgeRing( 10f );
Assert( ring.Inner == 5f && ring.Outer > 5f * 1.4143f, "the badge ring starts at the picture's edge and reaches past its corners (half-diagonal 1.4142 times the half side)" );
Assert( PresenceStyle.Truncate( "  Fzx  ", 20 ) == "Fzx" && PresenceStyle.Truncate( null, 20 ) == "(unknown name)" && PresenceStyle.Truncate( "", 20 ) == "(unknown name)" &&
    PresenceStyle.Truncate( new string( 'a', 40 ), 12 ).Length == 12 && PresenceStyle.Truncate( new string( 'a', 40 ), 12 ).EndsWith( "…" ), "names are trimmed, empty names get a placeholder, and long names are cut with an ellipsis" );
Assert( PresenceStyle.NeedsBackdrop( false ) && !PresenceStyle.NeedsBackdrop( true ), "the dark backing disc is drawn only when there is no picture, because over a picture it washed the picture blue" );
Assert( PresenceStyle.AvatarHeightScale > 2f && PresenceStyle.NameHeightScale > PresenceStyle.AvatarHeightScale + PresenceStyle.AvatarSizeScale * 0.5f, "the name sits above the profile picture, which sits above the halo" );

// ---- Play and watch ----------------------------------------------------------------------------
Assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.Source, true, true ) == PlaySpectateDecision.Normal, "an editor that is not playing synchronizes normally" );
Assert( PlaySpectateRules.Decide( true, PlayModeLifecycleState.RuntimeDisabled, false, true ) == PlaySpectateDecision.PausedForPlay, "a player keeps the connection and pauses scene edits instead of ending the session" );
Assert( PlaySpectateRules.Decide( true, PlayModeLifecycleState.RuntimeDisabled, true, true ) == PlaySpectateDecision.Normal, "an approved runtime run keeps its existing path" );
Assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.RuntimeStarting, false, true ) == PlaySpectateDecision.PausedForPlay, "the moment between pressing Play and the game starting is a pause, not a failure" );
Assert( PlaySpectateRules.Decide( true, PlayModeLifecycleState.RuntimeBlocked, false, true ) == PlaySpectateDecision.FailStop && PlaySpectateRules.Decide( false, PlayModeLifecycleState.RuntimeBlocked, true, true ) == PlaySpectateDecision.FailStop,
    "a hotload that interrupted play still stops everything, because that path archives recovery data" );
Assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.Source, false, false ) == PlaySpectateDecision.NoScene, "closing every scene tab is a quiet state, not a session-ending error" );
Assert( PlaySpectateRules.Decide( false, PlayModeLifecycleState.Source, false, true ) == PlaySpectateDecision.FailStop, "a supported scene whose packets cannot be admitted, with no play involved, is still a stop" );
Assert( PlaySpectateRules.PresenceState( true, true, true ) == "saving" && PlaySpectateRules.PresenceState( false, true, true ) == "playing" && PlaySpectateRules.PresenceState( false, false, true ) == "editing" && PlaySpectateRules.PresenceState( false, false, false ) == "idle",
    "presence state is saving, then playing, then editing, then idle in that order of precedence" );
Assert( PlaySpectateRules.StatusText( "playing" ) == "Playing the game" && PlaySpectateRules.StatusText( "editing" ) == "Editing" && PlaySpectateRules.StatusText( "anything" ) == "Idle", "each presence state has plain status wording" );
var heldBuffer = new HeldPacketBuffer();
Assert( heldBuffer.IsEmpty && heldBuffer.TryHold( new Packet { Kind = "accepted", Id = "a" } ) && heldBuffer.TryHold( new Packet { Kind = "accepted", Id = "b" } ) && heldBuffer.Count == 2 &&
    heldBuffer.TryTake( out var firstHeld ) && firstHeld.Id == "a" && heldBuffer.TryTake( out var secondHeld ) && secondHeld.Id == "b" && !heldBuffer.TryTake( out _ ), "held changes come back out in the order the host accepted them" );
var fullBuffer = new HeldPacketBuffer();
var heldAccepted = 0;
for ( var i = 0; i < HeldPacketBuffer.Capacity + 5; i++ ) if ( fullBuffer.TryHold( new Packet { Kind = "accepted" } ) ) heldAccepted++;
Assert( heldAccepted == HeldPacketBuffer.Capacity && fullBuffer.Count == HeldPacketBuffer.Capacity, "the buffer refuses a change beyond its capacity, so the caller must stop rather than drop it silently" );
fullBuffer.Clear();
Assert( fullBuffer.IsEmpty && fullBuffer.TryHold( null ), "clearing empties the buffer and a null packet is ignored" );
Assert( HeldPacketBuffer.IsSceneChange( "snapshot" ) && HeldPacketBuffer.IsSceneChange( "accepted" ) && !HeldPacketBuffer.IsSceneChange( "presence" ) && !HeldPacketBuffer.IsSceneChange( "left" ) && !HeldPacketBuffer.IsSceneChange( "pong" ),
    "only scene-changing packets are held while playing; presence and roster changes flow through" );


// ---- The library version is stated in five places; they must agree --------------------------------
// The marker is the library's own VERSION file, not the demo project's settings: this repository
// ships the library and its tooling only, so a fresh clone has no ProjectSettings folder at all and
// the walk must still find the root from the test binary.
string FindLibraryRoot()
{
    var dir = new DirectoryInfo( AppContext.BaseDirectory );
    while ( dir != null && !File.Exists( Path.Combine( dir.FullName, "Libraries", "collaboration", "VERSION" ) ) ) dir = dir.Parent;
    return dir?.FullName;
}
var versionRoot = FindLibraryRoot();
Assert( versionRoot != null, "the root holding Libraries/collaboration is found from the test binary, so the version files can be checked" );
var versionFile = File.ReadAllText( Path.Combine( versionRoot, "Libraries", "collaboration", "VERSION" ) ).Trim();
var dotVersionFile = File.ReadAllText( Path.Combine( versionRoot, "Libraries", "collaboration", ".version" ) ).Trim();
// The demo project's shared settings exist only where the demo project exists. When they do exist they
// must still match exactly - a library-only checkout has nothing to compare against, which is not a pass
// for the project copy: this same test runs in the workspace, where the file is present and enforced.
var projectSettingsPath = Path.Combine( versionRoot, "ProjectSettings", "collaboration.json" );
var hasProjectSettings = File.Exists( projectSettingsPath );
var configuredVersion = hasProjectSettings ? System.Text.Json.Nodes.JsonNode.Parse( File.ReadAllText( projectSettingsPath ) )?["LibraryVersion"]?.GetValue<string>() : null;
Assert( versionFile == LibraryInfo.LibraryVersion && dotVersionFile == LibraryInfo.LibraryVersion,
    "VERSION and .version equal LibraryInfo.LibraryVersion (" + LibraryInfo.LibraryVersion + ")" );
Assert( !hasProjectSettings || configuredVersion == LibraryInfo.LibraryVersion, "the project's shared settings require exactly the library version this build carries" );
var sessionSource = File.ReadAllText( Path.Combine( versionRoot, "Libraries", "collaboration", "Editor", "CollaborationSession.cs" ) );
var strayVersionLiterals = Directory.EnumerateFiles( Path.Combine( versionRoot, "Libraries", "collaboration", "Editor" ), "*.cs", SearchOption.AllDirectories )
    .Where( f => !f.EndsWith( "LibraryInfo.cs" ) && System.Text.RegularExpressions.Regex.IsMatch( File.ReadAllText( f ), "\"(library |v)?\\d+\\.\\d+\\.\\d+\"" ) )
    .Select( Path.GetFileName ).ToList();
Assert( strayVersionLiterals.Count == 0, "no editor source restates the library version as a literal (a stale copy showed 1.1.1 in the dock header): " + string.Join( ", ", strayVersionLiterals ) );
Assert( !sessionSource.Contains( "preparedLibraryVersion = \"" ) && sessionSource.Contains( "preparedLibraryVersion = LibraryInfo.LibraryVersion" ),
    "the session announces the version from LibraryInfo instead of a second literal that could drift" );


// ---- Solo test harness fences ------------------------------------------------------------------
Assert( TestHarnessRules.IsDisposableCopyPath( @"C:\Users\me\Proj\.collaboration\test-copies\SoloHost" ) && TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/.collaboration/test-copies/SoloGuest/" ) && TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/.COLLABORATION/Test-Copies/A" ),
    "a folder inside .collaboration/test-copies is a disposable copy, whatever the slash style or case" );
Assert( !TestHarnessRules.IsDisposableCopyPath( null ) && !TestHarnessRules.IsDisposableCopyPath( "" ) && !TestHarnessRules.IsDisposableCopyPath( "  " ) && !TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj" ) &&
    !TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/.collaboration" ) && !TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/.collaboration/test-copies" ) && !TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/.collaboration/test-copies/" ),
    "the real project, its .collaboration folder and the copies folder itself are never disposable" );
Assert( !TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/.collaboration/test-copies/../../Real" ) && !TestHarnessRules.IsDisposableCopyPath( @"C:\Users\me\Proj\.collaboration\test-copies\A\.." ) && !TestHarnessRules.IsDisposableCopyPath( "C:/Users/me/Proj/notcollaboration/test-copies/A" ),
    "a path that climbs out of the copies folder, or only resembles it, is refused" );
Assert( TestHarnessRules.IsDisposableCopy( "C:/x/.collaboration/test-copies/A", true ) && !TestHarnessRules.IsDisposableCopy( "C:/x/.collaboration/test-copies/A", false ) && !TestHarnessRules.IsDisposableCopy( "C:/x/Real", true ),
    "the session honours the test verifier only when the marker file AND the copy path are both present" );
var soloRunToken = TestHarnessRules.NewRunToken();
Assert( TestHarnessRules.IsSyntheticIdentity( TestHarnessRules.SoloGuestSteamId ) && TestHarnessRules.IsSyntheticIdentity( " " + TestHarnessRules.SoloGuestSteamId + " " ) && TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId, soloRunToken, soloRunToken ),
    "the reserved identity presenting this run's token is the one the test verifier may vouch for" );
Assert( !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId, "solo-" + new string( 'a', 64 ), soloRunToken ) && !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId, null, soloRunToken ) && !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId, "", "" ) && !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId, soloRunToken, null ) && !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId, soloRunToken + "x", soloRunToken ),
    "the reserved identity with a wrong, empty or missing token is never vouched for, so knowing the account number opens nothing" );
Assert( TestHarnessRules.NewRunToken() != TestHarnessRules.NewRunToken() && soloRunToken.Length == 5 + 64 && SteamTokenRules.IsPlausibleToken( soloRunToken ) && TestHarnessRules.TokenMatches( soloRunToken, soloRunToken ) && !TestHarnessRules.TokenMatches( soloRunToken, soloRunToken.ToUpperInvariant() ),
    "each run gets a fresh, well-formed, case-sensitive token" );
Assert( !TestHarnessRules.MayVouchFor( "76561198964875726", soloRunToken, soloRunToken ) && !TestHarnessRules.MayVouchFor( null, soloRunToken, soloRunToken ) && !TestHarnessRules.MayVouchFor( "", soloRunToken, soloRunToken ) && !TestHarnessRules.MayVouchFor( "0", soloRunToken, soloRunToken ) && !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId + "0", soloRunToken, soloRunToken ) && !TestHarnessRules.MayVouchFor( "0" + TestHarnessRules.SoloGuestSteamId, soloRunToken, soloRunToken ) && !TestHarnessRules.MayVouchFor( TestHarnessRules.SoloGuestSteamId + "\0", soloRunToken, soloRunToken ),
    "no real account, empty value, or near-miss of the reserved identity is ever vouched for (a real person still goes through Steam)" );
Assert( SteamIdentityRules.TryParse( TestHarnessRules.SoloGuestSteamId, out var soloIdentity ) && soloIdentity.IsValid && SteamIdentityRules.Canonical( soloIdentity ) == TestHarnessRules.SoloGuestSteamId && SteamTokenRules.IsPlausibleToken( soloRunToken ) && !string.IsNullOrWhiteSpace( TestHarnessRules.SoloGuestName ) && TestHarnessRules.SoloGuestName.Length <= 64,
    "the reserved identity, name and token pass the handshake's own shape checks, so the guest is refused by policy and never by format" );


// ---- The solo test identity opens exactly one door ---------------------------------------------
// A real SessionServer whose verifier vouches ONLY for the reserved identity and defers every other account to a stand-in for Steam's
// service that answers ServiceRejected. This is the composition CollaborationSession installs in a disposable copy.
var soloServerRunToken = TestHarnessRules.NewRunToken();
async Task<(string Kind, string Error)> SoloHelloAsync( string steamId, string presentedToken = null )
{
    var journalPath = NewServerJournalPath();
    var directory = Path.GetDirectoryName( journalPath )!;
    SessionServer server = null;
    Wire client = null;
    try
    {
        Func<Packet, Task<SteamTokenVerdict>> verifier = hello =>
            Task.FromResult( TestHarnessRules.MayVouchFor( hello.SteamId, hello.SteamToken, soloServerRunToken ) ? SteamTokenVerdict.Verified : SteamTokenVerdict.ServiceRejected );
        (server, var port) = StartServer( journalPath, verifier );
        client = await Wire.Connect( "127.0.0.1", port );
        client.Send( new Packet
        {
            Kind = "hello", Peer = Guid.NewGuid().ToString(), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
            Name = TestHarnessRules.SoloGuestName, Token = "test-token", Project = "test-project", Baseline = "test-baseline",
            SteamId = steamId, SteamToken = presentedToken ?? soloServerRunToken, OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
        } );
        var deadline = DateTime.UtcNow.AddSeconds( 6 );
        while ( DateTime.UtcNow < deadline )
        {
            server.Pump();
            if ( client.Incoming.TryDequeue( out var response ) ) return ( response.Kind, response.Error );
            await Task.Delay( 10 );
        }
        return ( "timeout", null );
    }
    finally
    {
        client?.Dispose();
        server?.Dispose();
        try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); }
        catch ( IOException ) { }
    }
}
var soloReply = await SoloHelloAsync( TestHarnessRules.SoloGuestSteamId );
Assert( soloReply.Kind == "welcome", "a host that vouches for the reserved test identity admits it: " + soloReply.Kind + " " + soloReply.Error );
var wrongTokenReply = await SoloHelloAsync( TestHarnessRules.SoloGuestSteamId, TestHarnessRules.NewRunToken() );
Assert( wrongTokenReply.Kind != "welcome" && wrongTokenReply.Error != null && wrongTokenReply.Error.Contains( "rejected" ), "the reserved account number with any token but this run's is refused by the real verifier: " + wrongTokenReply.Kind + " " + wrongTokenReply.Error );
var impostorReply = await SoloHelloAsync( "76561198000000002" );
Assert( impostorReply.Kind != "welcome" && impostorReply.Error != null && impostorReply.Error.Contains( "rejected" ), "a different account presenting the same fake token is refused by the real verifier: " + impostorReply.Kind + " " + impostorReply.Error );
var lookalikeReply = await SoloHelloAsync( TestHarnessRules.SoloGuestSteamId.Substring( 0, TestHarnessRules.SoloGuestSteamId.Length - 1 ) + "2" );
Assert( lookalikeReply.Kind != "welcome" && lookalikeReply.Kind != "timeout", "an account one digit away from the reserved identity is refused: " + lookalikeReply.Kind );


// ---- Presence carries the sender's verified account, so every editor can show the right profile picture ----------
// The host relays presence as the sender wrote it, and a sender never writes its own Steam id: without the host stamping it, a real friend's
// marker and roster row had no picture to look up. The host also has to OVERWRITE a claimed id, or a guest could show someone else's face.
async Task<Packet> PresenceAsSeenByEveryoneAsync( string helloSteamId, string claimedSteamId )
{
    var journalPath = NewServerJournalPath();
    var directory = Path.GetDirectoryName( journalPath )!;
    SessionServer server = null;
    Wire client = null;
    try
    {
        (server, var port) = StartServer( journalPath, hello => Task.FromResult( SteamTokenVerdict.Verified ) );
        client = await Wire.Connect( "127.0.0.1", port );
        client.Send( new Packet
        {
            Kind = "hello", Peer = Guid.NewGuid().ToString(), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
            Name = "Presence Tester", Token = "test-token", Project = "test-project", Baseline = "test-baseline",
            SteamId = helloSteamId, SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
        } );
        var deadline = DateTime.UtcNow.AddSeconds( 6 );
        var welcomed = false;
        while ( DateTime.UtcNow < deadline && !welcomed )
        {
            server.Pump();
            while ( client.Incoming.TryDequeue( out var reply ) ) if ( reply.Kind == "welcome" ) welcomed = true;
            await Task.Delay( 10 );
        }
        if ( !welcomed ) return null;
        client.Send( new Packet { Kind = "presence", Peer = Guid.NewGuid().ToString(), SteamId = claimedSteamId, Presence = new System.Text.Json.Nodes.JsonObject { ["state"] = "idle" } } );
        deadline = DateTime.UtcNow.AddSeconds( 6 );
        while ( DateTime.UtcNow < deadline )
        {
            server.Pump();
            while ( client.Incoming.TryDequeue( out var reply ) ) if ( reply.Kind == "presence" ) return reply;
            await Task.Delay( 10 );
        }
        return null;
    }
    finally
    {
        client?.Dispose();
        server?.Dispose();
        try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); }
        catch ( IOException ) { }
    }
}
var relayedPresence = await PresenceAsSeenByEveryoneAsync( "76561198000000007", null );
Assert( relayedPresence != null && relayedPresence.SteamId == "76561198000000007", "the host stamps the sender's verified Steam account on its presence, so a collaborator's marker can load their picture: " + relayedPresence?.SteamId );
var spoofedPresence = await PresenceAsSeenByEveryoneAsync( "76561198000000007", "76561198000000099" );
Assert( spoofedPresence != null && spoofedPresence.SteamId == "76561198000000007", "a guest that claims another account in its presence still shows its own: " + spoofedPresence?.SteamId );


// ---- The test collaborator's script, run against the REAL host authority -----------------------
// Base scene: one plain box, shaped like a scene the editor really wrote.
var soloBaseId = Guid.NewGuid();
var soloBase = new List<ObjectState> { SoloScript.WithPosition( SoloScript.NewCube( soloBaseId, Guid.NewGuid(), "0,0,0", "1,1,1,1" ), "219.5,3.2,34.3" ) };
var soloJournal = new List<Packet>();
var soloAuthority = new Authority( p => soloJournal.Add( p ) );
soloAuthority.Subscribe( new Packet { Kind = "subscribe", Scene = "scenes/minimal.scene", Objects = soloBase } );
var soloBefore = soloAuthority.Scenes["scenes/minimal.scene"].Values.Select( x => x.Data.ToJsonString() ).OrderBy( x => x ).ToList();
var soloKnown = new Dictionary<string, ObjectState>( soloAuthority.Scenes["scenes/minimal.scene"] );
Packet SoloEdit( string peer, params Change[] changes ) => new() { Kind = "edit", Scene = "scenes/minimal.scene", Peer = peer, Changes = changes.ToList() };

var soloCube = SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), SoloScript.Vec( 10, 20, 30 ), SoloScript.Color( 1, 0, 0 ) );
var soloCreate = soloAuthority.Apply( SoloEdit( "bot", SoloScript.Create( soloCube ) ) );
Assert( soloCreate.Kind == "accepted", "the host accepts the test collaborator's new cube: " + soloCreate.Error );
SoloScript.ApplyAccepted( soloKnown, soloCreate.Changes );
var soloMoved = SoloScript.WithPosition( soloCube, SoloScript.Vec( 50, 60, 70 ) );
var soloMove = soloAuthority.Apply( SoloEdit( "bot", SoloScript.Update( soloCube, soloMoved ) ) );
Assert( soloMove.Kind == "accepted" && soloAuthority.Scenes["scenes/minimal.scene"][soloCube.Id].Data["Position"]!.GetValue<string>() == "50,60,70", "a position change is accepted and lands in the host's state: " + soloMove.Error );
SoloScript.ApplyAccepted( soloKnown, soloMove.Changes );
var soloTinted = SoloScript.WithTint( soloMoved, SoloScript.TintAt( 0.4 ) );
var soloTint = soloAuthority.Apply( SoloEdit( "bot", SoloScript.Update( soloMoved, soloTinted ) ) );
Assert( soloTint.Kind == "accepted", "a supported property change (renderer tint) is accepted: " + soloTint.Error );
SoloScript.ApplyAccepted( soloKnown, soloTint.Changes );
var soloStale = soloAuthority.Apply( SoloEdit( "bot", SoloScript.Update( soloCube, SoloScript.WithPosition( soloCube, "1,1,1" ) ) ) );
Assert( soloStale.Kind == "accepted" && soloStale.Merged && soloStale.Notice == "Concurrent edits to \"" + soloCube.Data["Name"]!.GetValue<string>() + "\" were combined; the later edit was kept for: Position.", "an edit built on stale state is merged per property and the clash is reported, never silently applied: " + soloStale.Kind + " " + soloStale.Error + " " + soloStale.Notice );
Assert( soloAuthority.Scenes["scenes/minimal.scene"][soloCube.Id].Data["Position"]!.GetValue<string>() == "1,1,1" && StateRules.Equal( soloAuthority.Scenes["scenes/minimal.scene"][soloCube.Id], SoloScript.WithPosition( soloTinted, "1,1,1" ) ), "the merged stale edit moves only the position it changed and keeps the tint the host already held" );
SoloScript.ApplyAccepted( soloKnown, soloStale.Changes );
var soloHumanMove = soloAuthority.Apply( SoloEdit( "human", SoloScript.Update( soloKnown[soloBaseId.ToString()], SoloScript.WithPosition( soloKnown[soloBaseId.ToString()], "0,0,99" ) ) ) );
Assert( soloHumanMove.Kind == "accepted", "another collaborator's edit to a different object goes through alongside the bot's" );
SoloScript.ApplyAccepted( soloKnown, soloHumanMove.Changes );
var soloDelete = soloAuthority.Apply( SoloEdit( "bot", SoloScript.Delete( soloTinted ) ) );
Assert( soloDelete.Kind == "accepted" && !soloAuthority.Scenes["scenes/minimal.scene"].ContainsKey( soloCube.Id ), "the test collaborator deletes its cube: " + soloDelete.Error );
SoloScript.ApplyAccepted( soloKnown, soloDelete.Changes );
var soloAfter = soloAuthority.Scenes["scenes/minimal.scene"].Values.Where( x => x.Id == soloBaseId.ToString() ).Select( x => x.Data["Name"]!.GetValue<string>() ).ToList();
Assert( soloAuthority.Scenes["scenes/minimal.scene"].Count == 1 && soloAfter.Single() == SoloScript.ObjectName, "after the script the scene holds only the original object: no cube left behind" );
Assert( soloKnown.Count == 1 && soloKnown.ContainsKey( soloBaseId.ToString() ), "the collaborator's local copy tracks the same scene as the host" );

// Any component type is accepted on creation now; only a malformed component (here, one without a valid identifier) is refused.
var soloAnyType = SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), "0,0,0", "1,1,1,1" );
((System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)soloAnyType.Data["Components"]!)[0]!)["__type"] = "Sandbox.SomeOtherComponent";
var soloAnyTypeReceipt = soloAuthority.Apply( SoloEdit( "bot", SoloScript.Create( soloAnyType ) ) );
Assert( soloAnyTypeReceipt.Kind == "accepted", "the host accepts any component type on creation: " + soloAnyTypeReceipt.Error );
Assert( soloAuthority.Apply( SoloEdit( "bot", SoloScript.Delete( soloAnyType ) ) ).Kind == "accepted", "the any-type cube is deleted again, leaving the scene as the script found it" );
var soloBadShape = SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), "0,0,0", "1,1,1,1" );
((System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)soloBadShape.Data["Components"]!)[0]!)["__guid"] = "not-a-guid";
Assert( soloAuthority.Apply( SoloEdit( "bot", SoloScript.Create( soloBadShape ) ) ).Kind == "reject", "the host refuses a component without a valid identifier on creation" );
Assert( SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), "0,0,0", "1,1,1,1" ).Data["Components"]!.AsArray()[0]!.AsObject()["__type"]!.GetValue<string>() == "Sandbox.ModelRenderer", "the cube's renderer is typed Sandbox.ModelRenderer" );

// Geometry: orbit, facing and presence.
var soloCenter = ( X: 100.0, Y: -40.0, Z: 25.0 );
var soloOrbit = Enumerable.Range( 0, 36 ).Select( i => SoloScript.Orbit( soloCenter, 150, i * Math.PI / 18, 0 ) ).ToList();
Assert( soloOrbit.All( p => Math.Abs( Math.Sqrt( ( p.X - soloCenter.X ) * ( p.X - soloCenter.X ) + ( p.Y - soloCenter.Y ) * ( p.Y - soloCenter.Y ) ) - 150 ) < 1e-6 ), "the orbit stays 150 units from its centre in the ground plane" );
Assert( Enumerable.Range( 0, 200 ).All( i => Math.Abs( SoloScript.Orbit( soloCenter, 150, i * 0.1, 12 ).Z - soloCenter.Z ) <= 12 + 1e-9 ), "the bob stays within its height" );
foreach ( var target in new[] { ( X: 1.0, Y: 0.0, Z: 0.0 ), ( X: 0.0, Y: 1.0, Z: 0.0 ), ( X: -3.0, Y: 2.0, Z: -1.0 ), ( X: 2.0, Y: -5.0, Z: 4.0 ) } )
{
    var forward = SoloScript.Rotate( SoloScript.LookAt( ( 0, 0, 0 ), target ), ( 1, 0, 0 ) );
    var len = Math.Sqrt( target.X * target.X + target.Y * target.Y + target.Z * target.Z );
    Assert( Math.Abs( forward.X - target.X / len ) < 1e-9 && Math.Abs( forward.Y - target.Y / len ) < 1e-9 && Math.Abs( forward.Z - target.Z / len ) < 1e-9, "the look rotation turns forward onto the target " + target );
}
Assert( SoloScript.LookAt( ( 5, 5, 5 ), ( 5, 5, 5 ) ) == ( 0.0, 0.0, 0.0, 1.0 ), "looking at your own position gives the identity rather than NaN" );
var soloPresence = SoloScript.Presence( ( 1.5, 2, 3 ), ( 0, 0, 0, 1 ), "editing" );
Assert( soloPresence["position"]!.GetValue<string>() == "1.5,2,3" && soloPresence["rotation"]!.GetValue<string>() == "0,0,0,1" && soloPresence["state"]!.GetValue<string>() == "editing" && soloPresence["fov"]!.GetValue<double>() == 60.0,
    "presence carries the same string formats the scene uses for positions and rotations" );
Assert( Enumerable.Range( 0, 50 ).Select( i => SoloScript.TintAt( i / 50.0 ) ).Distinct().Count() > 30 && Enumerable.Range( 0, 50 ).All( i => SoloScript.TintAt( i / 50.0 ).Split( ',' ).All( c => double.Parse( c, System.Globalization.CultureInfo.InvariantCulture ) is >= 0 and <= 1 ) ), "the tint walks through many distinct colours and stays a valid colour" );

// The checklist wording.
var soloSteps = new List<SoloStep> { new() { State = SoloStepState.Passed }, new() { State = SoloStepState.Passed }, new() { State = SoloStepState.Waiting } };
Assert( SoloReport.Summary( soloSteps, false ) == "2 of 3 checks passed so far." && SoloReport.Summary( soloSteps, true ) == "2 of 3 checks passed; 1 did not run." && !SoloReport.AllPassed( soloSteps ), "a step that has not run is never counted as a pass" );
soloSteps[2].State = SoloStepState.Failed;
Assert( SoloReport.Summary( soloSteps, true ) == "1 of 3 checks failed. 2 passed." && !SoloReport.AllPassed( soloSteps ), "a failure is reported first and by count" );
soloSteps[2].State = SoloStepState.Skipped;
Assert( SoloReport.Summary( soloSteps, true ) == "2 checks passed, 1 skipped." && !SoloReport.AllPassed( soloSteps ), "a skipped step is named as skipped, and an all-pass claim needs every step to pass" );
Assert( SoloReport.DemoAllowed( soloSteps ) && !SoloReport.DemoAllowed( null ), "the live demo may start while nothing has failed" );
soloSteps[2].State = SoloStepState.Failed;
Assert( !SoloReport.DemoAllowed( soloSteps ), "the live demo never starts once a check has failed" );
soloSteps[2].State = SoloStepState.Passed;
Assert( SoloReport.Summary( soloSteps, true ) == "All 3 checks passed." && SoloReport.AllPassed( soloSteps ) && SoloReport.Summary( null, false ) == "Not run yet." && !SoloReport.AllPassed( new List<SoloStep>() ), "all-pass is only claimed when every step passed" );


// ---- The scripted collaborator against a real SessionServer over a real socket -------------------------------------------------
// No stand-ins: the bot dials the server's TCP port with the same encrypted transport an editor uses, says hello, subscribes, sends presence and
// edits. The scene is seeded the way the host editor seeds it (a subscribe carrying the objects), and every assertion about the HOST reads what a
// SECOND observer connected to that host receives, so nothing depends on the bot's own copy of events.
async Task SoloBotRoundTripAsync()
{
    var journalPath = NewServerJournalPath();
    var directory = Path.GetDirectoryName( journalPath )!;
    SessionServer server = null;
    SoloBot bot = null;
    SoloBot observer = null;
    SoloBot impostor = null;
    try
    {
        var runToken = TestHarnessRules.NewRunToken();
        var secret = InviteCode.NewSecret();
        var projectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";
        var key = InviteCode.DeriveKey( secret, projectId );
        // Two reserved-style identities are needed (one account may hold one slot), so the verifier here vouches for any account presenting this run's token.
        Func<Packet, Task<SteamTokenVerdict>> verifier = hello =>
            Task.FromResult( TestHarnessRules.TokenMatches( hello.SteamToken, runToken ) ? SteamTokenVerdict.Verified : SteamTokenVerdict.ServiceRejected );
        var port = GetFreeLoopbackPort();
        server = new SessionServer( "127.0.0.1", port, projectId, "base-1", Convert.ToHexString( key ), journalPath, null, verifier, key );
        server.hostManifest = "a1b2c3d4";
        server.peerFacts = hello => new PeerFacts( hello.SteamId, IdentityVerified: false, IsFriend: true, IsOrgMember: true, hello.LibraryVersion, hello.ProjectManifest, hello.OrgIdent );

        SoloHandshake HandshakeFor( string steamId, string name, string token ) => new() { Project = projectId, Baseline = "base-1", Manifest = "a1b2c3d4", Org = "cyn", LibraryVersion = LibraryInfo.LibraryVersion, SteamId = steamId, SteamToken = token, Name = name, SessionToken = Convert.ToHexString( key ) };
        SoloBot Make( string steamId, string name, string token ) { var b = new SoloBot( HandshakeFor( steamId, name, token ) ); b.Connect( () => Wire.Connect( "127.0.0.1", port, key, true ) ); return b; }
        async Task PumpUntil( Func<bool> done, double seconds, string what )
        {
            var stop = DateTime.UtcNow.AddSeconds( seconds );
            while ( !done() && DateTime.UtcNow < stop ) { server.Pump(); bot?.Pump(); observer?.Pump(); await Task.Delay( 5 ); }
            Assert( done(), what + " (bot " + bot?.State + " " + bot?.Refusal + "; observer " + observer?.State + " " + observer?.Refusal + ")" );
        }

        // The observer stands in for the host editor: it seeds the scene with a real subscribe, then watches what the host broadcasts.
        var baseId = Guid.NewGuid();
        var baseObject = SoloScript.WithPosition( SoloScript.NewCube( baseId, Guid.NewGuid(), "0,0,0", "1,1,1,1" ), "219.5,3.2,34.3" );
        observer = Make( "76561198000000010", "Observer", runToken );
        await PumpUntil( () => observer.IsLive, 8, "an observer holding this run's token is admitted by the real host" );
        observer.Subscribe( "scenes/minimal.scene", new List<ObjectState> { baseObject } );
        await PumpUntil( () => observer.HasScene( "scenes/minimal.scene" ), 8, "the host seeds the scene from the observer's subscribe" );

        bot = Make( TestHarnessRules.SoloGuestSteamId, TestHarnessRules.SoloGuestName, runToken );
        await PumpUntil( () => bot.IsLive, 8, "the scripted collaborator is welcomed by the real host" );
        Assert( bot.HostSession != null && bot.HostSession == observer.HostSession, "the welcome names the same host session the observer joined" );
        bot.Subscribe( "scenes/minimal.scene" );
        await PumpUntil( () => bot.HasScene( "scenes/minimal.scene" ), 8, "the host answers a subscribe with the scene's real objects" );
        Assert( bot.Known( "scenes/minimal.scene" ).ContainsKey( baseId.ToString() ) && bot.Known( "scenes/minimal.scene" ).Count == 1, "the collaborator sees exactly the host's scene" );

        bot.SendPresence( "scenes/minimal.scene", SoloScript.Presence( ( 300, 0, 120 ), SoloScript.LookAt( ( 300, 0, 120 ), ( 219.5, 3.2, 34.3 ) ), "editing" ) );
        await PumpUntil( () => observer.Others.Values.Any( x => x.SteamId == TestHarnessRules.SoloGuestSteamId && x.Presence?["state"]?.GetValue<string>() == "editing" ), 8, "the other collaborator receives the bot's presence, stamped with its admitted account" );
        Assert( observer.Others.Values.Single( x => x.SteamId == TestHarnessRules.SoloGuestSteamId ).Name == TestHarnessRules.SoloGuestName, "the presence carries the bot's name" );

        var cube = SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), SoloScript.Vec( 250, 40, 60 ), SoloScript.Color( 1, 0.3, 0.2 ) );
        bot.SendEdit( "scenes/minimal.scene", new[] { SoloScript.Create( cube ) } );
        await PumpUntil( () => bot.AcceptedOwn == 1 && observer.Known( "scenes/minimal.scene" ).ContainsKey( cube.Id ), 8, "the host accepts the cube and the OTHER collaborator sees it appear" );
        var moved = SoloScript.WithPosition( cube, SoloScript.Vec( 300, 80, 90 ) );
        bot.SendEdit( "scenes/minimal.scene", new[] { SoloScript.Update( cube, moved ) } );
        await PumpUntil( () => bot.AcceptedOwn == 2 && observer.Known( "scenes/minimal.scene" )[cube.Id].Data["Position"]!.GetValue<string>() == "300,80,90", 8, "the other collaborator sees the cube move" );
        var tinted = SoloScript.WithTint( moved, SoloScript.TintAt( 0.3 ) );
        bot.SendEdit( "scenes/minimal.scene", new[] { SoloScript.Update( moved, tinted ) } );
        await PumpUntil( () => bot.AcceptedOwn == 3 && JsonNode.DeepEquals( observer.Known( "scenes/minimal.scene" )[cube.Id].Data, tinted.Data ), 8, "the other collaborator sees the property change" );

        bot.SendEdit( "scenes/minimal.scene", new[] { SoloScript.Update( cube, SoloScript.WithPosition( cube, "1,1,1" ) ) } );
        await PumpUntil( () => bot.AcceptedOwn == 4 && observer.Known( "scenes/minimal.scene" )[cube.Id].Data["Position"]!.GetValue<string>() == "1,1,1", 8, "a stale edit of one property is merged by the host and the other collaborator sees it" );
        Assert( bot.Rejects.Count == 0 && JsonNode.DeepEquals( observer.Known( "scenes/minimal.scene" )[cube.Id].Data, SoloScript.WithPosition( tinted, "1,1,1" ).Data ), "the merged stale edit moved only the position and kept the tint for the other collaborator" );

        bot.SendEdit( "scenes/minimal.scene", new[] { SoloScript.Delete( tinted ) } );
        await PumpUntil( () => bot.AcceptedOwn == 5 && !observer.Known( "scenes/minimal.scene" ).ContainsKey( cube.Id ), 8, "the other collaborator sees the cube deleted (a deletion built on an older state still wins)" );
        Assert( observer.Known( "scenes/minimal.scene" ).Count == 1 && observer.Known( "scenes/minimal.scene" ).ContainsKey( baseId.ToString() ) && bot.PendingCount == 0, "the scene is back to exactly the original object, with no edit left unanswered" );

        bot.Leave();
        await PumpUntil( () => !observer.Others.Values.Any( x => x.SteamId == TestHarnessRules.SoloGuestSteamId ), 8, "the other collaborator's roster clears the bot's row when it leaves" );

        // The reserved account number without this run's token opens nothing.
        impostor = Make( TestHarnessRules.SoloGuestSteamId, "Impostor", TestHarnessRules.NewRunToken() );
        var impostorDeadline = DateTime.UtcNow.AddSeconds( 8 );
        while ( impostor.State != SoloBotState.Refused && impostor.State != SoloBotState.Failed && impostor.State != SoloBotState.Live && DateTime.UtcNow < impostorDeadline ) { server.Pump(); impostor.Pump(); await Task.Delay( 10 ); }
        Assert( impostor.State == SoloBotState.Refused && impostor.Refusal.Contains( "rejected" ), "a collaborator with the reserved account number but not this run's token is refused: " + impostor.State + " " + impostor.Refusal );
    }
    finally
    {
        bot?.Dispose();
        observer?.Dispose();
        impostor?.Dispose();
        server?.Dispose();
        try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); }
        catch ( IOException ) { }
    }
}
await SoloBotRoundTripAsync();


// ---- The disposable copy the solo test runs in -------------------------------------------------------------------------
Assert( SoloCopy.ShouldSkip( "Code/obj/Debug/x.dll" ) && SoloCopy.ShouldSkip( @"Libraries\lib\Editor\bin\z.dll" ) && SoloCopy.ShouldSkip( "Assets/models/a.vmdl_c" ) && SoloCopy.ShouldSkip( "Code/Game.csproj" ) && SoloCopy.ShouldSkip( "Assets/x.generated.vtex" ) && SoloCopy.ShouldSkip( "Editor/Properties/launch.json" ) && SoloCopy.ShouldSkip( "Assets/.sbox/cache" ) && SoloCopy.ShouldSkip( "" ),
    "generated, compiled and per-machine files are not copied" );
Assert( !SoloCopy.ShouldSkip( "Assets/scenes/minimal.scene" ) && !SoloCopy.ShouldSkip( "Code/Player.cs" ) && !SoloCopy.ShouldSkip( "ProjectSettings/collaboration.json" ) && !SoloCopy.ShouldSkip( "Assets/scenes/minimal.scene.meta" ) && !SoloCopy.ShouldSkip( "Assets/binder/notes.txt" ),
    "scenes, source, settings and metadata are copied, and a folder that merely starts with a skipped name is not skipped" );
{
    var soloTemp = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t ? t : Path.GetTempPath(), "teamcreate-solocopy", Guid.NewGuid().ToString( "N" ) );
    void Write( string rel, string text = "x" ) { var f = Path.Combine( soloTemp, rel ); Directory.CreateDirectory( Path.GetDirectoryName( f )! ); File.WriteAllText( f, text ); }
    try
    {
        Write( "Assets/scenes/minimal.scene", "scene" ); Write( "Assets/scenes/minimal.scene.meta" ); Write( "Code/Player.cs" ); Write( "Code/obj/y.dll" ); Write( "Libraries/lib/Editor/bin/z.dll" ); Write( "Libraries/lib/Editor/A.cs" );
        Write( "ProjectSettings/collaboration.json", "{}" ); Write( "proj.sbproj", "{}" ); Write( ".collaboration/journal-1.jsonl", "secret journal" ); Write( ".collaboration/outbox/o.json", "pending" ); Write( "Unrelated/notes.txt" );
        var built = SoloCopy.Build( soloTemp, autorun: true );
        Assert( built.Ok && built.Path == SoloCopy.CopyRoot( soloTemp ) && TestHarnessRules.IsDisposableCopyPath( built.Path ), "a copy is built inside .collaboration/test-copies: " + built.Error );
        Assert( File.ReadAllText( Path.Combine( built.Path, "Assets/scenes/minimal.scene" ) ) == "scene" && File.Exists( Path.Combine( built.Path, "Code/Player.cs" ) ) && File.Exists( Path.Combine( built.Path, "Libraries/lib/Editor/A.cs" ) ) && File.Exists( Path.Combine( built.Path, "ProjectSettings/collaboration.json" ) ) && File.Exists( Path.Combine( built.Path, "proj.sbproj" ) ),
            "the copy carries the scene, source, library, settings and project file" );
        Assert( !File.Exists( Path.Combine( built.Path, "Code/obj/y.dll" ) ) && !File.Exists( Path.Combine( built.Path, "Libraries/lib/Editor/bin/z.dll" ) ) && !Directory.Exists( Path.Combine( built.Path, "Unrelated" ) ), "build output and unrelated folders stay behind" );
        Assert( !File.Exists( Path.Combine( built.Path, ".collaboration/journal-1.jsonl" ) ) && !Directory.Exists( Path.Combine( built.Path, ".collaboration/outbox" ) ) && Directory.GetFiles( Path.Combine( built.Path, ".collaboration" ) ).Length == 1, "the real project's journals and pending edits are never copied; the copy's .collaboration holds only the marker" );
        Assert( SoloCopy.HasMarker( built.Path ) && SoloCopy.WantsAutorun( built.Path ) && TestHarnessRules.IsDisposableCopy( built.Path, SoloCopy.HasMarker( built.Path ) ), "the copy carries the marker the session insists on, set to run by itself once" );
        SoloCopy.ConsumeAutorun( built.Path );
        Assert( !SoloCopy.WantsAutorun( built.Path ) && SoloCopy.HasMarker( built.Path ), "the autorun request is spent after one use and the marker stays" );
        Write( ".collaboration/test-copies/Solo/Assets/stale.txt", "left from an earlier run" );
        Write( "Assets/scenes/minimal.scene", "scene v2" );
        var rebuilt = SoloCopy.Build( soloTemp, autorun: false );
        Assert( rebuilt.Ok && File.ReadAllText( Path.Combine( rebuilt.Path, "Assets/scenes/minimal.scene" ) ) == "scene v2" && !File.Exists( Path.Combine( rebuilt.Path, "Assets/stale.txt" ) ) && !SoloCopy.WantsAutorun( rebuilt.Path ), "rebuilding replaces the earlier copy completely" );
        Assert( File.ReadAllText( Path.Combine( soloTemp, "Assets/scenes/minimal.scene" ) ) == "scene v2" && File.Exists( Path.Combine( soloTemp, ".collaboration/journal-1.jsonl" ) ), "the real project is untouched" );
        var nested = SoloCopy.Build( rebuilt.Path, autorun: true );
        Assert( !nested.Ok && nested.Error.Contains( "already a solo test copy" ), "a copy is never copied again from inside itself: " + nested.Error );
        Directory.Delete( built.Path, true );
        Directory.CreateDirectory( built.Path ); File.WriteAllText( Path.Combine( built.Path, "precious.txt" ), "someone's work" );
        var blocked = SoloCopy.Build( soloTemp, autorun: true );
        Assert( !blocked.Ok && blocked.Error.Contains( "left alone" ) && File.Exists( Path.Combine( built.Path, "precious.txt" ) ), "a folder at the copy location that has no marker is never deleted" );
        Directory.Delete( built.Path, true );
        var tooBig = SoloCopy.Build( soloTemp, autorun: true, maxBytes: 5 );
        Assert( !tooBig.Ok && tooBig.Error.Contains( "larger than" ) && !Directory.Exists( SoloCopy.CopyRoot( soloTemp ) ), "a project over the size limit is refused before anything is written" );
        File.Delete( Path.Combine( soloTemp, "proj.sbproj" ) );
        Assert( !SoloCopy.Build( soloTemp, autorun: true ).Ok && !SoloCopy.Build( Path.Combine( soloTemp, "missing" ), autorun: true ).Ok && !SoloCopy.Build( null, autorun: true ).Ok, "a project with no .sbproj, a missing folder or a null path is refused with a message" );
    }
    finally
    {
        try { if ( Directory.Exists( soloTemp ) ) Directory.Delete( soloTemp, true ); }
        catch ( IOException ) { }
    }
}


// ---- Values written into scene state must be exactly what the engine writes back --------------------------------------------
// The engine stores floats as 32-bit. A value like 0.6 comes back as 0.600000024, so the host editor "corrects" it with an edit of its own, and every
// later edit built on the old text is refused as stale. Live, this cascaded into seven failures. The script therefore writes only values that survive a
// 32-bit round trip, and colours with at most five decimals, which is how the engine prints them.
bool FloatExact( string csv, int maxDecimals = 9 ) => csv.Split( ',' ).All( t => { var d = double.Parse( t, System.Globalization.CultureInfo.InvariantCulture ); return (double)(float)d == d && ( t.Contains( '.' ) ? t.Split( '.' )[1].Length : 0 ) <= maxDecimals; } );
var soloShape = SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), SoloScript.SnapVec( 12.4, -7.6, 45.2 ), SoloScript.TintAt( 0.37 ) );
Assert( FloatExact( soloShape.Data["Position"]!.GetValue<string>() ) && FloatExact( soloShape.Data["Scale"]!.GetValue<string>() ) && FloatExact( soloShape.Data["Rotation"]!.GetValue<string>() ), "a new cube's position, scale and rotation survive a 32-bit round trip" );
Assert( FloatExact( soloShape.Data["Components"]![0]!["Tint"]!.GetValue<string>(), 5 ), "the cube's tint survives a 32-bit round trip and has at most five decimals" );
Assert( Enumerable.Range( 0, 400 ).All( i => FloatExact( SoloScript.TintAt( i / 400.0 ), 5 ) ), "every tint the script can produce is exact in 32 bits, with at most five decimals" );
Assert( Enumerable.Range( 0, 400 ).All( i => { var o = SoloScript.Orbit( ( 0, 0, 45 ), 90, i * 0.16, 14 ); return FloatExact( SoloScript.SnapVec( o.X, o.Y, o.Z ) ); } ), "every position the demo orbit produces, once snapped, is exact in 32 bits" );
Assert( SoloScript.SnapVec( 12.4, -7.6, 45.5 ) == "12,-8,46" && SoloScript.SnapVec( 0.4, -0.4, 0 ) == "0,0,0", "snapping rounds to whole units and never prints negative zero" );
// The engine's echo: after it re-serializes, the host holds the same text, so no corrective edit is generated.
var soloEngineEcho = SoloScript.NewCube( Guid.NewGuid(), Guid.NewGuid(), "0,0,45", SoloScript.TintAt( 0.62 ) );
var soloEchoed = new ObjectState( soloEngineEcho.Id, "", (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse( soloEngineEcho.Data.ToJsonString() ) );
soloEchoed.Data["Scale"] = string.Join( ",", soloEngineEcho.Data["Scale"]!.GetValue<string>().Split( ',' ).Select( t => ( (float)double.Parse( t, System.Globalization.CultureInfo.InvariantCulture ) ).ToString( "G9", System.Globalization.CultureInfo.InvariantCulture ) ) );
Assert( StateRules.Equal( soloEngineEcho, soloEchoed ), "the cube's scale prints identically after the engine's 32-bit round trip, so the host editor has nothing to correct" );


// ---- Replacing a copy that something still holds open must never strand it ----------------------------------------------------------
// Live, a terminal left inside the old copy made the delete fail partway. The marker had already gone, so the next run refused the folder as "not a
// solo test copy" and the one-click button was stuck until someone deleted it by hand. The marker is now the last thing removed, and a folder with no
// files in it at all is reclaimed rather than treated as somebody's work.
{
    var lockTemp = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t2 ? t2 : Path.GetTempPath(), "teamcreate-solocopy-lock", Guid.NewGuid().ToString( "N" ) );
    void W( string rel, string text = "x" ) { var f = Path.Combine( lockTemp, rel ); Directory.CreateDirectory( Path.GetDirectoryName( f )! ); File.WriteAllText( f, text ); }
    try
    {
        W( "Assets/scenes/minimal.scene", "scene" ); W( "Code/Player.cs" ); W( "proj.sbproj", "{}" );
        var lockFirst = SoloCopy.Build( lockTemp, autorun: false );
        Assert( lockFirst.Ok, "a first copy is built: " + lockFirst.Error );

        // 1. a leftover folder with directories but no files at all is reclaimed
        Directory.Delete( lockFirst.Path, true );
        Directory.CreateDirectory( Path.Combine( lockFirst.Path, ".collaboration" ) );
        Directory.CreateDirectory( Path.Combine( lockFirst.Path, "Assets", "scenes" ) );
        var reclaimed = SoloCopy.Build( lockTemp, autorun: false );
        Assert( reclaimed.Ok && SoloCopy.HasMarker( reclaimed.Path ), "a leftover folder holding no files is reclaimed instead of refused: " + reclaimed.Error );

        // 2. a file held open makes the replacement fail cleanly, and the copy stays a marked, reusable copy
        if ( OperatingSystem.IsWindows() )
        {
            var held = Path.Combine( reclaimed.Path, "Assets", "scenes", "minimal.scene" );
            using ( var hold = new FileStream( held, FileMode.Open, FileAccess.Read, FileShare.None ) )
            {
                var blocked = SoloCopy.Build( lockTemp, autorun: false );
                Assert( !blocked.Ok && blocked.Error.Contains( "still has" ) && blocked.Error.Contains( "open" ), "a copy with a file held open is not replaced, and the message names the cause: " + blocked.Error );
                Assert( SoloCopy.HasMarker( reclaimed.Path ), "the failed replacement leaves the marker in place, so the folder is still recognised as a solo test copy" );
            }
            var afterRelease = SoloCopy.Build( lockTemp, autorun: false );
            Assert( afterRelease.Ok && File.ReadAllText( Path.Combine( afterRelease.Path, "Assets", "scenes", "minimal.scene" ) ) == "scene", "once the file is released the very next build succeeds, with nothing to clean up by hand: " + afterRelease.Error );
        }

        // 2b. a process whose working directory is inside the copy (a terminal, an IDE) must not block replacing it: a held directory
        // stops that directory from being deleted, but not the files in it, so the copy is emptied and refilled in place.
        if ( OperatingSystem.IsWindows() )
        {
            var beforeCwd = Directory.GetCurrentDirectory();
            var inside = Path.Combine( SoloCopy.CopyRoot( lockTemp ), ".collaboration" );
            try
            {
                Directory.SetCurrentDirectory( inside );
                var whileHeld = SoloCopy.Build( lockTemp, autorun: false );
                Assert( whileHeld.Ok && File.ReadAllText( Path.Combine( whileHeld.Path, "Assets", "scenes", "minimal.scene" ) ) == "scene" && SoloCopy.HasMarker( whileHeld.Path ), "a working directory held inside the copy does not stop it being replaced: " + whileHeld.Error );
            }
            finally
            {
                Directory.SetCurrentDirectory( beforeCwd );
            }
        }

        // 3. a folder with real files and no marker is still never touched
        Directory.Delete( SoloCopy.CopyRoot( lockTemp ), true );
        Directory.CreateDirectory( SoloCopy.CopyRoot( lockTemp ) );
        File.WriteAllText( Path.Combine( SoloCopy.CopyRoot( lockTemp ), "mine.txt" ), "someone's work" );
        var refused = SoloCopy.Build( lockTemp, autorun: false );
        Assert( !refused.Ok && refused.Error.Contains( "left alone" ) && File.Exists( Path.Combine( SoloCopy.CopyRoot( lockTemp ), "mine.txt" ) ), "a folder with real files and no marker is still left alone" );
    }
    finally
    {
        try { if ( Directory.Exists( lockTemp ) ) Directory.Delete( lockTemp, true ); }
        catch ( IOException ) { }
    }
}


// ---- The test never runs against a scene that has not finished loading --------------------------------------------------------------
// Live, the test started while the editor was still restoring its scene, so the host published an EMPTY scene, the save wrote that emptiness into the
// copy, and all 19 checks passed against nothing. The test now reads how many top-level objects the scene FILE holds and waits until the editor has
// loaded at least that many.
{
    var loadTemp = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t3 ? t3 : Path.GetTempPath(), "teamcreate-sceneload", Guid.NewGuid().ToString( "N" ) );
    Directory.CreateDirectory( loadTemp );
    try
    {
        string SceneWith( string body ) { var f = Path.Combine( loadTemp, Guid.NewGuid().ToString( "N" ) + ".scene" ); File.WriteAllText( f, body ); return f; }
        Assert( SoloScript.TopLevelObjectCount( SceneWith( "{\"__guid\":\"x\",\"GameObjects\":[{\"Name\":\"a\",\"Children\":[{\"Name\":\"nested\"},{\"Name\":\"nested2\"}]},{\"Name\":\"b\"},{\"Name\":\"c\"}]}" ) ) == 3, "only top-level objects are counted; children nested under them are not" );
        Assert( SoloScript.TopLevelObjectCount( SceneWith( "{\"GameObjects\":[]}" ) ) == 0 && SoloScript.TopLevelObjectCount( SceneWith( "{\"__guid\":\"x\"}" ) ) == 0, "a scene with an empty or absent object list holds zero objects" );
        Assert( SoloScript.TopLevelObjectCount( SceneWith( "not json" ) ) == null && SoloScript.TopLevelObjectCount( SceneWith( "[1,2]" ) ) == null && SoloScript.TopLevelObjectCount( Path.Combine( loadTemp, "missing.scene" ) ) == null && SoloScript.TopLevelObjectCount( null ) == null, "an unreadable, malformed or missing scene file gives no count rather than a guess" );
    }
    finally
    {
        try { if ( Directory.Exists( loadTemp ) ) Directory.Delete( loadTemp, true ); }
        catch ( IOException ) { }
    }
}
Assert( SoloScript.SceneLoadState( 6, 0 ) == SoloSceneLoad.Loading && SoloScript.SceneLoadState( 6, 5 ) == SoloSceneLoad.Loading, "a scene holding fewer objects than its file is still loading" );
Assert( SoloScript.SceneLoadState( 6, 6 ) == SoloSceneLoad.Loaded && SoloScript.SceneLoadState( 6, 9 ) == SoloSceneLoad.Loaded, "a scene holding at least as many objects as its file is loaded" );
Assert( SoloScript.SceneLoadState( 0, 0 ) == SoloSceneLoad.Loaded, "a genuinely empty scene is loaded immediately; the test does not wait forever for objects that do not exist" );
Assert( SoloScript.SceneLoadState( null, 0 ) == SoloSceneLoad.Unknown && SoloScript.SceneLoadState( null, 7 ) == SoloSceneLoad.Unknown, "with no readable file the state is unknown, which the test reports rather than passes" );
Assert( SoloScript.SceneLoadDetail( "scenes/minimal.scene", 6, 6 ) == "Using scenes/minimal.scene (6 objects loaded)." && SoloScript.SceneLoadDetail( "scenes/minimal.scene", 6, 2 ) == "Waiting for scenes/minimal.scene to finish loading (2 of 6 objects)…" && SoloScript.SceneLoadDetail( "scenes/a.scene", 1, 1 ) == "Using scenes/a.scene (1 object loaded)." && SoloScript.SceneLoadDetail( "scenes/a.scene", null, 3 ).Contains( "could not be read" ),
    "the step names how many objects were loaded, so a pass on an empty scene could never look like a pass" );


// ---- The solo test opens a second editor, so it checks there is room for one first --------------------------------------------------
// Each editor holds about 3.6 GB. On a 16 GB machine with an editor and a browser already open, a second one pushed committed memory to its limit.
// The check uses AVAILABLE memory (free plus the standby cache Windows can give back at once), not the smaller "free" figure, which would refuse
// machines that have plenty of room.
const long gb = 1024L * 1024 * 1024;
Assert( SoloCopy.MemoryAdvice( 8 * gb ) == null && SoloCopy.MemoryAdvice( SoloCopy.MinAvailableBytes ) == null && SoloCopy.MemoryAdvice( SoloCopy.MinAvailableBytes + 1 ) == null, "enough available memory gives no advice, and the threshold itself is enough" );
Assert( SoloCopy.MemoryAdvice( 1 * gb ) is { } lowAdvice && lowAdvice.Contains( "1.0 GB" ) && lowAdvice.Contains( "second editor" ) && lowAdvice.Contains( "-Force" ), "too little memory is refused, states the amount, says why, and names the override" );
Assert( SoloCopy.MemoryAdvice( SoloCopy.MinAvailableBytes - 1 ) != null && SoloCopy.MemoryAdvice( 0 ) != null, "one byte under the threshold, and none at all, are both refused" );
Assert( SoloCopy.MemoryAdvice( null ) == null && SoloCopy.MemoryAdvice( -5 ) == null, "an unreadable or nonsensical reading never blocks the test; the person can still see the message when memory really is low" );
Assert( SoloCopy.MemoryAdvice( (long)( 1.26 * gb ) )!.Contains( "1.3 GB" ) && SoloCopy.MinAvailableBytes == 3 * gb, "the amount is shown to one decimal, and the threshold is 3 GB" );


// ---- Stopping the test removes its object BEFORE the session ends ---------------------------------------------------------------------
// Leaving a session checkpoints the scene. Live, Stop destroyed the test cube and left in the same frame, so the engine had not removed it yet and the
// checkpoint saved it into the copy's scene file. Stop now waits until the object is gone and the host has been told, then leaves.
Assert( SoloScript.StopDecision( objectPresent: true, editsPending: false, connected: true, secondsWaited: 0 ) == SoloStopDecision.Wait, "while the test object still exists, stopping waits" );
Assert( SoloScript.StopDecision( objectPresent: false, editsPending: true, connected: true, secondsWaited: 0.5 ) == SoloStopDecision.Wait, "once the object is gone, stopping still waits until the host has acknowledged its removal" );
Assert( SoloScript.StopDecision( objectPresent: false, editsPending: false, connected: true, secondsWaited: 0.2 ) == SoloStopDecision.Proceed, "with the object gone and nothing pending, the session may end" );
Assert( SoloScript.StopDecision( objectPresent: true, editsPending: true, connected: true, secondsWaited: SoloScript.StopWaitSeconds ) == SoloStopDecision.Proceed && SoloScript.StopDecision( objectPresent: true, editsPending: true, connected: true, secondsWaited: SoloScript.StopWaitSeconds - 0.1 ) == SoloStopDecision.Wait, "a stop that cannot finish cleanly gives up after the time limit instead of hanging, and not one moment sooner" );
Assert( SoloScript.StopDecision( objectPresent: true, editsPending: true, connected: false, secondsWaited: 0 ) == SoloStopDecision.Proceed, "with no connection there is nothing to wait for" );
Assert( SoloScript.StopWaitSeconds is > 2 and <= 10, "the wait is long enough for a round trip and short enough to feel like a stop" );


// ---- The built-in relay: nothing to configure ---------------------------------------------------------------------------------------
// A first-time user has no server and no settings, so the library carries a default relay address and uses it unless the project says otherwise.
Assert( RelayDefaults.Resolve( null, "wss://default.example.workers.dev/relay" ) == "wss://default.example.workers.dev/relay", "with no RelayUrl in the settings, the built-in relay is used" );
Assert( RelayDefaults.Resolve( "wss://mine.example.com/relay", "wss://default.example.workers.dev/relay" ) == "wss://mine.example.com/relay", "a relay the project names wins over the built-in one" );
Assert( RelayDefaults.Resolve( RelayDefaults.Off, "wss://default.example.workers.dev/relay" ) == null && RelayDefaults.Resolve( "OFF", "wss://default.example.workers.dev/relay" ) == null, "\"off\" opts a project out of relaying altogether" );
Assert( RelayDefaults.Resolve( null, null ) == null && RelayDefaults.Resolve( "  ", null ) == null, "with no built-in relay yet and nothing configured there is no relay, exactly as before" );
Assert( RelayDefaults.Resolve( "  ", "wss://default.example.workers.dev/relay" ) == "wss://default.example.workers.dev/relay", "a blank RelayUrl counts as not set, not as a request to disable" );
Assert( RelayDefaults.DefaultUrl == null || RelayProtocol.TryValidateUrl( RelayDefaults.DefaultUrl, out _, out _ ), "the shipped default is either absent or a URL the editor will accept" );
Assert( RelayDefaults.DefaultUrl == null || RelayProtocol.IsPublicRelay( RelayDefaults.DefaultUrl ), "the shipped default is never a loopback address" );

// ---- An invite must never send a friend to the host's own computer ---------------------------------------------------------------
Assert( RelayProtocol.IsPublicRelay( "wss://relay.example.com/relay" ) && RelayProtocol.IsPublicRelay( "wss://collab.foo.workers.dev/relay" ), "a relay on a DNS name is public" );
Assert( !RelayProtocol.IsPublicRelay( "ws://localhost:8080/relay" ) && !RelayProtocol.IsPublicRelay( "ws://127.0.0.1:8080/relay" ) && !RelayProtocol.IsPublicRelay( "wss://localhost/relay" ) && !RelayProtocol.IsPublicRelay( null ) && !RelayProtocol.IsPublicRelay( "" ) && !RelayProtocol.IsPublicRelay( "not a url" ),
    "localhost, loopback addresses, blanks and garbage are not public relays" );
Assert( RelayProtocol.InviteRelayFor( "wss://relay.example.com/relay", allowLoopback: false ) == "wss://relay.example.com/relay", "a public relay goes into the invite" );
Assert( RelayProtocol.InviteRelayFor( "ws://localhost:8080/relay", allowLoopback: false ) == null, "a localhost relay is left out of an invite meant for another machine" );
Assert( RelayProtocol.InviteRelayFor( "ws://localhost:8080/relay", allowLoopback: true ) == "ws://localhost:8080/relay", "the solo test, which joins from the same machine, may carry a localhost relay" );
Assert( RelayProtocol.InviteRelayFor( null, allowLoopback: true ) == null, "no relay, no relay in the invite" );

// ---- The relay is told which room a socket is for, in the address itself -----------------------------------------------------------
// A hosted relay (a Cloudflare Worker) has to pick the right room BEFORE the first message, so the room id, which is one-way derived from the secret
// and not secret itself, rides in the query string.
var roomSecret = Enumerable.Range( 0, 16 ).Select( i => (byte)( 40 + i ) ).ToArray();
var roomHex = RelayProtocol.Hex( RelayProtocol.DeriveRoom( roomSecret ) );
Assert( RelayProtocol.ConnectUrl( "wss://relay.example.com/relay", roomSecret ) == "wss://relay.example.com/relay?room=" + roomHex, "the connect address names the room" );
Assert( RelayProtocol.ConnectUrl( "wss://relay.example.com/relay?x=1", roomSecret ) == "wss://relay.example.com/relay?x=1&room=" + roomHex, "an address that already has a query keeps it" );
Assert( roomHex.Length == 32 && roomHex.All( c => "0123456789abcdef".Contains( c ) ), "the room id is 32 lowercase hex characters" );
Assert( !RelayProtocol.ConnectUrl( "wss://relay.example.com/relay", roomSecret ).Contains( Convert.ToHexString( roomSecret ), StringComparison.OrdinalIgnoreCase ), "the invite secret itself never appears in the address" );

// ---- A friend holding a code that names a localhost relay is told exactly that -----------------------------------------------------
var loopbackAttempt = new RouteAttempt( RelayProtocol.RouteSource, "ws://localhost:8080/relay", "points at this computer", RelayProtocol.LoopbackCode );
var loopbackSummary = RouteFailureRules.Summarize( new[] { loopbackAttempt, new RouteAttempt( "code", "203.0.113.42:27845", "actively refused" ) } );
Assert( loopbackSummary.Contains( "host's own computer" ) && loopbackSummary.Contains( "new session" ) && !loopbackSummary.Contains( "Unable to connect" ) && loopbackSummary.Length <= RouteFailureRules.MaxLength, "the message says the code points at the host's own computer and what to do, not a raw socket error: " + loopbackSummary );
Assert( RelayProtocol.IsTerminal( RelayProtocol.LoopbackCode ) && !RelayProtocol.IsTerminal( "no-host" ), "a code that names the host's own computer is terminal (retrying cannot help), unlike a host that is merely not connected yet" );
{
    var offTemp = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t5 ? t5 : Path.GetTempPath(), "teamcreate-off", Guid.NewGuid().ToString( "N" ) );
    Directory.CreateDirectory( offTemp );
    try
    {
        var offPath = Path.Combine( offTemp, "c.json" );
        File.WriteAllText( offPath, "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\", \"RelayUrl\": \"OFF\" }" );
        var offCfg = CollaborationConfig.LoadFrom( offPath, out var offErr );
        Assert( offErr == null && RelayDefaults.Resolve( offCfg.RelayUrl, "wss://x.example.com/relay" ) == null, "RelayUrl \"off\" loads without complaint and disables relaying" );
        File.WriteAllText( offPath, "{ \"ProjectId\": \"2e9f8f81-8b38-4207-ad3e-4e7c0bea567f\" }" );
        var noneCfg = CollaborationConfig.LoadFrom( offPath, out var noneErr );
        Assert( noneErr == null && noneCfg.RelayUrl == null && RelayDefaults.Resolve( noneCfg.RelayUrl, "wss://x.example.com/relay" ) == "wss://x.example.com/relay", "a settings file with no RelayUrl uses the built-in relay" );
        var badIdPath = Path.Combine( offTemp, "bad.json" );
        File.WriteAllText( badIdPath, "{ \"ProjectId\": \"nope\" }" );
        var badId = CollaborationSetup.EnsureConfig( badIdPath, "o", null, "1.2.0" );
        Assert( !badId.Created && badId.Error != null && badId.Error.Contains( "not a GUID" ) && File.ReadAllText( badIdPath ) == "{ \"ProjectId\": \"nope\" }", "a ProjectId that is not a GUID is reported and never silently replaced" );
    }
    finally
    {
        try { if ( Directory.Exists( offTemp ) ) Directory.Delete( offTemp, true ); }
        catch ( IOException ) { }
    }
}

// ---- An installed copy of the library is recognised under every ident it can carry ---------------------------------------------------------
// Installing from Asset Party puts the library into a project as Libraries/<org>.<ident> under the PUBLISHED ident, which differs from the ident
// in this workspace's own descriptor. Matching only the configured ident reported "Library installation required" with the library installed.
{
    Assert( LibraryInfo.PublishedIdent == "sbox_editor_collab", "the published ident is the name Asset Party lists the package under" );
    Assert( LibraryInfo.DefaultLibraryIdent( "cyn" ) == "cyn." + LibraryInfo.PublishedIdent, "a new project's settings name the published package, not the workspace descriptor's name" );
    Assert( LibraryInfo.DefaultLibraryIdent( " " ) is null && LibraryInfo.DefaultLibraryIdent( null ) is null, "no organization gives no default ident" );
    Assert( LibraryInfo.IdentPart( "cyn.sbox_editor_collab" ) == "sbox_editor_collab" && LibraryInfo.IdentPart( "bare" ) == "bare" && LibraryInfo.IdentPart( "  " ) is null && LibraryInfo.IdentPart( null ) is null, "IdentPart drops the organization and keeps a bare name" );
    Assert( LibraryInfo.IsThisLibrary( "cyn.editor_collaboration", "sbox_editor_collab" ), "the Asset Party install is found even though the settings file names the older ident" );
    Assert( LibraryInfo.IsThisLibrary( "cyn.sbox_editor_collab", "cyn.sbox_editor_collab" ), "the install is found when the settings file names the published ident" );
    Assert( LibraryInfo.IsThisLibrary( "cyn.editor_collaboration", "local.editor_collaboration" ), "the workspace's own descriptor is still recognised" );
    Assert( LibraryInfo.IsThisLibrary( null, "cyn.sbox_editor_collab" ), "a project with no configured ident still finds the published install" );
    Assert( LibraryInfo.IsThisLibrary( "cyn.custom_name", "acme.custom_name" ), "a project that names its own ident still finds a library installed under it" );
    Assert( !LibraryInfo.IsThisLibrary( "cyn.editor_collaboration", "facepunch.actiongraph" ), "an unrelated library is not mistaken for this one" );
    Assert( !LibraryInfo.IsThisLibrary( "cyn.editor_collaboration", null ) && !LibraryInfo.IsThisLibrary( "cyn.editor_collaboration", "" ), "an entry with no ident is never this library" );
    Assert( sessionSource.Contains( "LibraryInfo.IsThisLibrary(" ) && !sessionSource.Contains( "item.Version?.ToString()" ),
        "the readiness check goes through LibraryInfo.IsThisLibrary and never compares Asset Party's build number (1.0.<build>) with the release version" );
}

// ---- The first Host click writes the shared settings file itself ----------------------------------------------------------------------------
{
    var setupTemp = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t4 ? t4 : Path.GetTempPath(), "teamcreate-setup", Guid.NewGuid().ToString( "N" ) );
    Directory.CreateDirectory( setupTemp );
    try
    {
        var settingsPath = Path.Combine( setupTemp, "ProjectSettings", "collaboration.json" );
        var made = CollaborationSetup.EnsureConfig( settingsPath, "myorg", "myorg.editor_collaboration", "1.2.0" );
        Assert( made.Created && made.Error == null && File.Exists( settingsPath ), "a missing settings file is created: " + made.Error );
        var loaded = CollaborationConfig.LoadFrom( settingsPath, out var loadErr );
        Assert( loadErr == null && Guid.TryParse( loaded.ProjectId, out _ ) && loaded.Org == "myorg" && loaded.LibraryIdent == "myorg.editor_collaboration" && loaded.LibraryVersion == new Version( "1.2.0" ) && loaded.RequireSteamFriend && loaded.RequireOrgMember && loaded.RequirePublished, "the new file loads, has a fresh project id, and keeps every admission gate ON" );
        var firstId = loaded.ProjectId;
        var again = CollaborationSetup.EnsureConfig( settingsPath, "otherorg", "x.y", "9.9.9" );
        Assert( !again.Created && again.Error == null && CollaborationConfig.LoadFrom( settingsPath, out _ ).ProjectId == firstId && CollaborationConfig.LoadFrom( settingsPath, out _ ).Org == "myorg", "an existing file is never touched: same id, same organization" );
        var otherPath = Path.Combine( setupTemp, "b", "collaboration.json" );
        Assert( CollaborationSetup.EnsureConfig( otherPath, "myorg", null, "1.2.0" ).Created && CollaborationConfig.LoadFrom( otherPath, out _ ).ProjectId != firstId, "every new project gets its own id" );
        var lackingId = Path.Combine( setupTemp, "c", "collaboration.json" );
        Directory.CreateDirectory( Path.GetDirectoryName( lackingId )! );
        File.WriteAllText( lackingId, "{ \"Org\": \"keepme\", \"RequireSteamFriend\": false }" );
        var filled = CollaborationSetup.EnsureConfig( lackingId, "myorg", null, "1.2.0" );
        var filledCfg = CollaborationConfig.LoadFrom( lackingId, out _ );
        Assert( filled.Created && Guid.TryParse( filledCfg.ProjectId, out _ ) && filledCfg.Org == "keepme" && !filledCfg.RequireSteamFriend, "a file that exists but has no ProjectId gets one added and keeps every choice the person made (including a relaxed gate)" );
        var brokenPath = Path.Combine( setupTemp, "d", "collaboration.json" );
        Directory.CreateDirectory( Path.GetDirectoryName( brokenPath )! );
        File.WriteAllText( brokenPath, "{ not json" );
        var broken = CollaborationSetup.EnsureConfig( brokenPath, "myorg", null, "1.2.0" );
        Assert( !broken.Created && broken.Error != null && File.ReadAllText( brokenPath ) == "{ not json", "a file that is not valid JSON is reported and never overwritten" );
        Assert( CollaborationSetup.EnsureConfig( Path.Combine( setupTemp, "e", "collaboration.json" ), "", null, "1.2.0" ).Created && CollaborationConfig.LoadFrom( Path.Combine( setupTemp, "e", "collaboration.json" ), out _ ).Org == "cyn", "with no organization known, the file is still created and falls back to the library's own default" );
    }
    finally
    {
        try { if ( Directory.Exists( setupTemp ) ) Directory.Delete( setupTemp, true ); }
        catch ( IOException ) { }
    }
}


// ---- Presence and heartbeat cadence: a free shared relay is billed per message ----------------------------------------------------------
// Live measurement: one two-person session sent the relay 50 messages a second, about 9,000 billed requests an hour, which would spend a free daily
// allowance in eleven hours of session time across ALL users. Presence that has not changed is not sent, and the heartbeat is only needed while
// nothing else is flowing. The host still drops a member after 15 seconds of silence, so these must stay well inside that.
Assert( SendCadence.MaxSilence < TimeSpan.FromSeconds( 15 ) && SendCadence.MaxSilence <= TimeSpan.FromSeconds( 5 ), "the longest an editor stays silent is well inside the host's 15 second timeout, with room for a slow network" );
var t0 = new DateTime( 2026, 9, 30, 12, 0, 0, DateTimeKind.Utc );
Assert( SendCadence.ShouldSendPresence( changed: true, sinceLastSend: TimeSpan.FromMilliseconds( 100 ), isEditing: false ), "changed presence is sent at the normal rate" );
Assert( !SendCadence.ShouldSendPresence( changed: true, sinceLastSend: TimeSpan.FromMilliseconds( 20 ), isEditing: false ), "changed presence is not sent more often than the minimum interval" );
Assert( !SendCadence.ShouldSendPresence( changed: false, sinceLastSend: TimeSpan.FromMilliseconds( 900 ), isEditing: false ), "unchanged presence is not resent" );
Assert( SendCadence.ShouldSendPresence( changed: false, sinceLastSend: SendCadence.PresenceRefresh, isEditing: false ), "unchanged presence is still refreshed now and then, so a collaborator who joins later sees where everyone is" );
Assert( SendCadence.ShouldSendPresence( changed: false, sinceLastSend: SendCadence.PresenceRefresh + TimeSpan.FromSeconds( 1 ), isEditing: false ), "the refresh is a floor, not a one-off" );
Assert( SendCadence.PresenceRefresh <= SendCadence.MaxSilence, "the refresh alone keeps the connection alive" );
Assert( SendCadence.ShouldSendPing( sinceLastSend: SendCadence.PingInterval ) && !SendCadence.ShouldSendPing( sinceLastSend: SendCadence.PingInterval - TimeSpan.FromMilliseconds( 1 ) ), "a heartbeat is sent when the interval has passed, and not before" );
Assert( SendCadence.PingInterval <= SendCadence.MaxSilence, "the heartbeat alone keeps the connection alive" );
Assert( SendCadence.MessagesPerSecondIdle( peers: 2 ) <= 2.0, "an idle two-person session sends the relay two messages a second or fewer, not fifty" );
Assert( Math.Abs( SendCadence.MessagesPerSecondMoving( peers: 2 ) - 2 * ( 1000.0 / 33.0 + 0.5 ) ) < 1e-9, "two people both moving their cameras send at most about sixty-two a second: a presence packet every 33 ms and half a heartbeat each (was twenty-one at 100 ms)" );
Assert( SendCadence.BilledRequestsPerHour( messagesPerSecond: 50 ) == 9000 && SendCadence.BilledRequestsPerHour( messagesPerSecond: 2 ) == 360, "billing at 20 messages to 1 request, as the relay's pricing states" );
Assert( SendCadence.FreeHoursPerDay( messagesPerSecond: 2 ) > 250, "at the idle rate the free allowance covers hundreds of session-hours a day across all users" );


// ---- The solo test waits for the readiness checks instead of racing them -------------------------------------------------------------------
// Live, the test tried to host ten seconds after the editor loaded, before the project-publication check had answered, and failed with "project
// publication required" while the same card showed green a minute later. Earlier runs passed only when the answer happened to arrive first.
var okRow = new ReadinessItem( "steam", "Steam sign-in", ReadinessLevel.Ok, "signed in", null );
var blockedRow = new ReadinessItem( "published", "Project publication required", ReadinessLevel.Blocked, "Publish this project", null );
Assert( SoloScript.HostReadiness( new[] { okRow }, 0 ) == SoloHostReadiness.Ready, "with every row ok the test may host" );
Assert( SoloScript.HostReadiness( new[] { okRow, blockedRow }, 5 ) == SoloHostReadiness.Waiting, "a blocked row shortly after load is waited on, because the checks answer asynchronously" );
Assert( SoloScript.HostReadiness( new[] { okRow, blockedRow }, SoloScript.ReadinessGraceSeconds ) == SoloHostReadiness.Blocked, "a row still blocked after the grace period is a real failure, reported with its reason" );
Assert( SoloScript.HostReadiness( new[] { okRow, blockedRow }, SoloScript.ReadinessGraceSeconds - 0.1 ) == SoloHostReadiness.Waiting, "and not one moment sooner" );
Assert( SoloScript.HostReadiness( Array.Empty<ReadinessItem>(), 1 ) == SoloHostReadiness.Waiting && SoloScript.HostReadiness( null, 1 ) == SoloHostReadiness.Waiting, "an empty checklist means the checks have not run yet, not that everything passed" );
Assert( SoloScript.HostReadiness( Array.Empty<ReadinessItem>(), SoloScript.ReadinessGraceSeconds ) == SoloHostReadiness.Blocked, "but a checklist that never appears is reported rather than waited on forever" );
Assert( SoloScript.ReadinessGraceSeconds is >= 30 and <= 90, "the grace period covers a slow first answer from the package service without hiding a real problem for long" );
Assert( ReadinessRules.CanStart( new[] { okRow, new ReadinessItem( "published", "Project publication", ReadinessLevel.Warning, "could not be fetched", null ) } ), "a warning row never blocks hosting; only a blocked row does" );


// ---- Engine bookkeeping in .meta files must not count as a change to the project -----------------------------------------------------------
// Live: the engine writes "sceneCompileDirty": true into a scene's .meta as soon as the scene is edited. Both the baseline the host compares at join and
// the check that guards Save hashed those raw bytes, so a collaborator's first edit made Save refuse with "Source/assets changed during collaboration".
byte[] Utf8( string text ) => System.Text.Encoding.UTF8.GetBytes( text );
string HashOf( byte[] bytes ) => Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( bytes ) );
var metaClean = Utf8( "{\n  \"guid\": \"d67ea403-1d84-498f-a860-7f993d45846b\"\n}" );
var metaDirty = Utf8( "{\"guid\":\"d67ea403-1d84-498f-a860-7f993d45846b\",\"sceneCompileDirty\":true}" );
var metaDirtyFalse = Utf8( "{\"guid\":\"d67ea403-1d84-498f-a860-7f993d45846b\",\"sceneCompileDirty\":false}" );
Assert( HashOf( MetaFileRules.Canonical( "Assets/scenes/minimal.scene.meta", metaClean ) ) == HashOf( MetaFileRules.Canonical( "Assets/scenes/minimal.scene.meta", metaDirty ) ) && HashOf( MetaFileRules.Canonical( "Assets/scenes/minimal.scene.meta", metaClean ) ) == HashOf( MetaFileRules.Canonical( "Assets/scenes/minimal.scene.meta", metaDirtyFalse ) ),
    "a scene's .meta hashes the same whether or not the engine has marked it compile-dirty, and however it is formatted" );
var metaOtherGuid = Utf8( "{\"guid\":\"00000000-0000-0000-0000-000000000001\"}" );
Assert( HashOf( MetaFileRules.Canonical( "a.scene.meta", metaClean ) ) != HashOf( MetaFileRules.Canonical( "a.scene.meta", metaOtherGuid ) ), "a different guid is still a different project: only the bookkeeping flag is ignored" );
var metaExtra = Utf8( "{\"guid\":\"d67ea403-1d84-498f-a860-7f993d45846b\",\"importer\":\"custom\"}" );
Assert( HashOf( MetaFileRules.Canonical( "a.scene.meta", metaClean ) ) != HashOf( MetaFileRules.Canonical( "a.scene.meta", metaExtra ) ), "any other field in the .meta still counts" );
Assert( HashOf( MetaFileRules.Canonical( "a.scene.meta", Utf8( "{\"sceneCompileDirty\":true,\"guid\":\"x\"}" ) ) ) == HashOf( MetaFileRules.Canonical( "a.scene.meta", Utf8( "{\"guid\":\"x\",\"sceneCompileDirty\":true}" ) ) ), "field order does not matter" );
Assert( HashOf( MetaFileRules.Canonical( "notes.txt", metaDirty ) ) == HashOf( metaDirty ) && HashOf( MetaFileRules.Canonical( "Code/Player.cs", metaDirty ) ) == HashOf( metaDirty ), "files that are not .meta are hashed exactly as they are, so a real code or asset change is never hidden" );
var notJson = Utf8( "this is not json" );
Assert( HashOf( MetaFileRules.Canonical( "a.scene.meta", notJson ) ) == HashOf( notJson ) && HashOf( MetaFileRules.Canonical( "a.scene.meta", Array.Empty<byte>() ) ) == HashOf( Array.Empty<byte>() ), "a .meta that is not a JSON object is hashed as it is, never dropped or guessed at" );
Assert( HashOf( MetaFileRules.Canonical( "a.scene.meta", Utf8( "[1,2]" ) ) ) == HashOf( Utf8( "[1,2]" ) ), "a .meta holding a JSON array is left alone" );
Assert( HashOf( MetaFileRules.Canonical( "A.SCENE.META", metaDirty ) ) == HashOf( MetaFileRules.Canonical( "a.scene.meta", metaClean ) ), "the .meta extension is matched without regard to case, as Windows treats file names" );
Assert( HashOf( MetaFileRules.Canonical( "a.scene.meta", Utf8( "\uFEFF{\"guid\":\"x\",\"sceneCompileDirty\":true}" ) ) ) == HashOf( MetaFileRules.Canonical( "a.scene.meta", Utf8( "{\"guid\":\"x\"}" ) ) ), "a byte-order mark at the start of a .meta does not change its meaning" );

// The project manifest uses the same rule: two copies of a project that differ only in that flag are the same project.
{
    var manifestTemp = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t6 ? t6 : Path.GetTempPath(), "teamcreate-manifest", Guid.NewGuid().ToString( "N" ) );
    try
    {
        foreach ( var name in new[] { "a", "b" } )
        {
            Directory.CreateDirectory( Path.Combine( manifestTemp, name, "Assets", "scenes" ) );
            File.WriteAllText( Path.Combine( manifestTemp, name, "Assets", "scenes", "m.scene" ), "scene" );
            File.WriteAllText( Path.Combine( manifestTemp, name, "Code.cs" ), "code" );
        }
        File.WriteAllBytes( Path.Combine( manifestTemp, "a", "Assets", "scenes", "m.scene.meta" ), metaClean );
        File.WriteAllBytes( Path.Combine( manifestTemp, "b", "Assets", "scenes", "m.scene.meta" ), metaDirty );
        Assert( ProjectManifest.Compute( Path.Combine( manifestTemp, "a" ) ) == ProjectManifest.Compute( Path.Combine( manifestTemp, "b" ) ) && ProjectManifest.Compute( Path.Combine( manifestTemp, "a" ) ) != "",
            "two copies of a project that differ only in the compile-dirty flag have the same manifest" );
        File.WriteAllText( Path.Combine( manifestTemp, "b", "Assets", "scenes", "m.scene" ), "scene EDITED" );
        Assert( ProjectManifest.Compute( Path.Combine( manifestTemp, "a" ) ) != ProjectManifest.Compute( Path.Combine( manifestTemp, "b" ) ), "an actual change to the scene still changes the manifest" );
    }
    finally
    {
        try { if ( Directory.Exists( manifestTemp ) ) Directory.Delete( manifestTemp, true ); }
        catch ( IOException ) { }
    }
}


// ---- A host with no relay is told plainly what that means ------------------------------------------------------------------------------------
Assert( RelayDefaults.NoRelayNotice( effectiveRelay: null, configured: null ) is { } none && none.Contains( "same network" ) && none.Contains( "Set up relay" ), "with no relay at all the host is told friends on other networks cannot join, and where to set one up" );
Assert( RelayDefaults.NoRelayNotice( effectiveRelay: null, configured: "off" ) is { } off && off.Contains( "turned off" ) && !off.Contains( "Set up relay" ), "a project that turned relaying off is told so, not told to set one up" );
Assert( RelayDefaults.NoRelayNotice( effectiveRelay: "wss://x.example.com/relay", configured: null ) == null, "with a relay in use there is nothing to warn about" );
Assert( RelayDefaults.NoRelayNotice( effectiveRelay: "ws://localhost:8080/relay", configured: "ws://localhost:8080/relay" ) is { } local && local.Contains( "this computer" ), "a relay on localhost is called out as reachable only from this computer, because that is exactly what made a friend's join fail" );


// ---- Set up the relay from the dock: the token, the deploy, and what the card says -----------------------------------------------------------
// A person with no command-line tools presses a button, creates one Cloudflare token, and the editor deploys the relay itself. The token is a credential, so
// the checks below cover what may be sent to Cloudflare and where, what a failure says, and that nothing it echoes back carries the token.
{
    var rsToken = "cfut_TestTokenAbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
    bool Accepts( string text ) => CloudflareApi.TryAcceptToken( text, out _, out _ );

    Assert( CloudflareApi.TryAcceptToken( "  " + rsToken + "\r\n", out var rsAccepted, out var rsNoProblem ) && rsAccepted == rsToken && rsNoProblem == null, "a token on the clipboard is accepted, with surrounding whitespace removed" );
    Assert( Accepts( "aB3dE6gH9jK2mN5pQ8sT1vW4yZ7cF0hJ3kL6nP9r" ) && Accepts( "cfat_" + new string( 'a', 40 ) ), "a legacy mixed-case token and a token with a known prefix are accepted" );
    Assert( !Accepts( null ) && !Accepts( "" ) && !Accepts( "   " ) && !Accepts( "abc" ), "an empty, blank or far too short clipboard is refused" );

    var rsInvite = InviteCode.Encode( new InviteData { Port = 27845, ProjectId = Guid.NewGuid().ToString(), Secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes( 16 ), Endpoints = new List<byte[]>() } );
    Assert( !CloudflareApi.TryAcceptToken( rsInvite, out var rsLeaked, out var rsInviteProblem ) && rsLeaked == null && rsInviteProblem.Contains( "Nothing was sent" ),
        "an invite code on the clipboard is refused before anything is sent, because it carries the session's encryption secret" );
    // The all-one-case rule also refuses an uppercase invite code, so the decode guard is only what stops a MIXED-case one: pasted from a chat that capitalised
    // it, or typed by hand. The message is what tells the two apart.
    var rsMixed = rsInvite.Replace( "-", "" ).Substring( 0, 12 ).ToLowerInvariant() + rsInvite.Replace( "-", "" ).Substring( 12 );
    Assert( InviteCode.TryDecode( rsMixed, out _, out _ ) && !CloudflareApi.TryAcceptToken( rsMixed, out _, out var rsMixedProblem ) && rsMixedProblem.Contains( "invite code" ),
        "a mixed-case invite code, which only the decode guard can recognise, is refused as an invite code" );
    Assert( !Accepts( new string( 'a', 32 ) + new string( '7', 32 ) ) && !Accepts( "ABCDEFGHJKMNPQRSTVWXYZ0123456789ABCDEF" ), "a hexadecimal key or any all-one-case string (a hash, a relay access key, an invite-like code) is refused" );
    Assert( !Accepts( "https://dash.cloudflare.com/profile/api-tokens" ) && !Accepts( rsToken + "\n" + rsToken ) && !Accepts( rsToken + " extra" ), "a web address, several lines, or extra words are refused" );

    var rsPage = new Uri( CloudflareApi.TokenPageUrl() );
    var rsQuery = System.Web.HttpUtility.ParseQueryString( rsPage.Query );
    var rsPermissions = JsonNode.Parse( rsQuery["permissionGroupKeys"] )!.AsArray();
    Assert( rsPage.Host == "dash.cloudflare.com" && rsPage.AbsolutePath == "/profile/api-tokens" && rsPermissions.Count == 2
        && rsPermissions.Any( x => x!["key"]!.GetValue<string>() == "workers_scripts" && x["type"]!.GetValue<string>() == "edit" )
        && rsPermissions.Any( x => x!["key"]!.GetValue<string>() == "account_settings" && x["type"]!.GetValue<string>() == "read" ),
        "the token page asks for exactly two permissions, editing Workers scripts and reading the account, and nothing that reaches DNS or billing" );
    Assert( CloudflareApi.HealthUrlFor( "wss://a.workers.dev/relay" ) == "https://a.workers.dev/health" && CloudflareApi.HealthUrlFor( "ws://localhost:8080/relay" ) == "http://localhost:8080/health" && CloudflareApi.HealthUrlFor( "nonsense" ) == null,
        "the health address is derived from the relay's own address" );

    // ---- the whole deploy, against a stand-in served over real HTTP -------------------------------------------------------------------------
    var rsDelays = new List<TimeSpan>();
    Task RsNoWait( TimeSpan span, CancellationToken token ) { rsDelays.Add( span ); return Task.CompletedTask; }
    RelayDeployer RsDeployer( FakeCloudflare fake, Func<CloudflareRequest, bool> block = null, params string[] names )
    {
        var queue = new Queue<string>( names.Length == 0 ? new[] { "collab-abc" } : names );
        return new RelayDeployer( new RewritingTransport( fake.Port, block ), RelayWorkerSource.Text, host => fake.HealthUrl( host ), RsNoWait, () => queue.Count > 0 ? queue.Dequeue() : "collab-zzz" );
    }
    string RsRoute( FakeCloudflare.Call call ) => call.Method + " " + ( call.Path.StartsWith( "/client/v4" ) ? call.Path.Substring( "/client/v4".Length ) : call.Path );

    // First setup on an account with no workers.dev name: the name is refused once as taken, then registered; the relay is uploaded and its address waited on.
    {
        using var fake = new FakeCloudflare { Subdomain = null, HealthFailuresBeforeOk = 2 };
        fake.TakenNames.Add( "collab-abc" );
        var progress = new List<string>();
        var result = await RsDeployer( fake, null, "collab-abc", "collab-def" ).RunAsync( fake.Token, progress.Add, CancellationToken.None );
        var api = fake.Calls.Where( c => !c.Path.StartsWith( "/health" ) ).Select( RsRoute ).ToList();
        Assert( result.Ok && result.Error == null && result.RelayUrl == "wss://collab-relay.collab-def.workers.dev/relay", "a first setup succeeds and reports the relay's wss address" );
        Assert( string.Join( "|", api ) == string.Join( "|", new[]
            {
                "GET /accounts?per_page=50", "GET /accounts/acct-1/workers/subdomain", "PUT /accounts/acct-1/workers/subdomain", "PUT /accounts/acct-1/workers/subdomain",
                "GET /accounts/acct-1/workers/services/collab-relay", "PUT /accounts/acct-1/workers/scripts/collab-relay?excludeScript=true&bindings_inherit=strict", "POST /accounts/acct-1/workers/scripts/collab-relay/subdomain"
            } ), "the calls go in Wrangler's order: account, workers.dev name (retried when taken), existing relay, upload, address on" );
        Assert( fake.Calls.Where( c => c.Path.StartsWith( "/client/v4" ) ).All( c => c.Authorization == "Bearer " + fake.Token ), "every call to Cloudflare carries the token as a bearer credential" );
        Assert( fake.Calls.Where( c => c.Path.StartsWith( "/health" ) ).All( c => c.Authorization == null ) && fake.Calls.Count( c => c.Path.StartsWith( "/health" ) ) == 3 && rsDelays.Count == 2,
            "the token is never sent to the relay's own address, and the health check is repeated until it answers (two failures, then success)" );
        Assert( fake.Calls.Where( c => c.Path.EndsWith( "/subdomain" ) && c.Method == "POST" ).All( c => c.ScriptApiDate == CloudflareApi.ScriptApiDate ) && fake.Calls.Where( c => !( c.Method == "POST" && c.Path.EndsWith( "/subdomain" ) ) ).All( c => c.ScriptApiDate == null ),
            "only the per-script address switch selects Cloudflare's current API shape, as Wrangler does" );
        Assert( fake.UploadedParts.TryGetValue( "index.js", out var module ) && module.FileName == "index.js" && module.ContentType == "application/javascript+module" && module.Body == RelayWorkerSource.Text,
            "the uploaded module is the embedded relay source, byte for byte, as a JavaScript module" );
        var meta = fake.UploadedMetadata;
        Assert( meta != null && meta["main_module"]!.GetValue<string>() == "index.js" && meta["compatibility_date"]!.GetValue<string>() == CloudflareApi.CompatibilityDate
            && meta["bindings"]![0]!["type"]!.GetValue<string>() == "durable_object_namespace" && meta["bindings"]![0]!["name"]!.GetValue<string>() == "ROOMS" && meta["bindings"]![0]!["class_name"]!.GetValue<string>() == "Room",
            "the metadata names the module, the compatibility date and the Durable Object binding the relay needs" );
        Assert( meta!["migrations"]!["new_tag"]!.GetValue<string>() == "v1" && meta["migrations"]!["steps"]![0]!["new_sqlite_classes"]![0]!.GetValue<string>() == "Room" && meta["keep_bindings"] == null,
            "a first setup carries the one-time SQLite Durable Object migration (the only kind the free plan allows) and has no secrets to keep" );
        Assert( fake.HealthAnswerHost == "collab-relay.collab-def.workers.dev" && progress.First().StartsWith( "Step 1 of 6" ) && progress.Last().StartsWith( "Step 6 of 6" ) && progress.Count >= 7,
            "the address that is checked is the new relay's, and progress is reported from step 1 to step 6" );
    }

    // Setup again on an account that already has the relay: it is updated in place, the migration is not repeated, and an access key the owner set survives.
    {
        using var fake = new FakeCloudflare { ExistingTag = "v1" };
        var result = await RsDeployer( fake ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( result.Ok && result.RelayUrl == "wss://collab-relay.tester.workers.dev/relay", "setting up again on an account that has the relay succeeds and reports its address" );
        Assert( fake.UploadedMetadata!["migrations"] == null && fake.UploadedMetadata["keep_bindings"] is JsonArray rsKept && rsKept.Select( x => x!.GetValue<string>() ).SequenceEqual( new[] { "secret_text", "secret_key" } ),
            "an update carries no migration and keeps the owner's secrets, so an access key set with `wrangler secret put` is not wiped" );
        Assert( fake.Calls.Count( c => c.Method == "PUT" && c.Path.EndsWith( "/workers/subdomain" ) ) == 0, "an account that already has a workers.dev name is not asked to register one" );
    }
    {
        using var fake = new FakeCloudflare { ExistingTag = "v0" };
        await RsDeployer( fake ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( fake.UploadedMetadata!["migrations"]!["new_tag"]!.GetValue<string>() == "v1", "a relay on an older migration tag is brought forward to the current one" );
    }

    // Failures: each says what happened and what to do, stops before doing anything further, and never carries the token.
    {
        using var fake = new FakeCloudflare();
        var result = await RsDeployer( fake ).RunAsync( "cfut_WrongTokenAbCdEfGhIjKlMnOpQrStUvWxYz012345", null, CancellationToken.None );
        Assert( !result.Ok && result.RelayUrl == null && result.Error.Contains( "refused the token" ) && result.Error.Contains( "Open Cloudflare" ) && fake.Calls.Count == 1,
            "a token Cloudflare refuses stops at the first call with a sentence naming the fix; nothing is uploaded" );
    }
    {
        using var fake = new FakeCloudflare();
        var result = await RsDeployer( fake, _ => true ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( !result.Ok && result.Error.Contains( "could not be reached" ) && result.Error.Contains( "internet" ) && fake.Calls.Count == 0, "no connection is reported as such, with the next action" );
    }
    {
        using var fake = new FakeCloudflare { HealthFailuresBeforeOk = 1000 };
        rsDelays.Clear();
        var result = await RsDeployer( fake ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( !result.Ok && result.RelayUrl == null && result.Error.Contains( "did not answer" ) && result.Error.Contains( "not in use" ) && fake.Calls.Count( c => c.Path.StartsWith( "/health" ) ) == 10 && rsDelays.Count == 9,
            "a relay that never answers is reported as not in use after ten checks, and the address is never handed back to be saved" );
    }
    {
        using var fake = new FakeCloudflare { HealthService = "some-other-site" };
        var result = await RsDeployer( fake ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( !result.Ok && result.RelayUrl == null && result.Error.Contains( "did not answer" ) && fake.Calls.Count( c => c.Path.StartsWith( "/health" ) ) == 10,
            "an address that answers as a different service is not accepted as the relay, so a stranger's site at that name is never saved" );
    }
    {
        using var fake = new FakeCloudflare { FailUploadWithStatus = 400, EchoTokenInErrors = true };
        var result = await RsDeployer( fake ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( !result.Ok && result.Error.Contains( "uploading the relay" ) && !result.Error.Contains( fake.Token ) && result.Error.Contains( "[token]" ), "when Cloudflare echoes the token back in an error, the message shows a placeholder instead" );
    }
    {
        using var fake = new FakeCloudflare { FailUploadWithStatus = 500 };
        var result = await RsDeployer( fake ).RunAsync( fake.Token, null, CancellationToken.None );
        Assert( !result.Ok && result.Error.Contains( "problem of its own" ) && result.Error.Contains( "few minutes" ), "a Cloudflare outage is reported as Cloudflare's, with a time to retry" );
    }
    {
        using var fake = new FakeCloudflare();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var early = await RsDeployer( fake ).RunAsync( fake.Token, null, cancelled.Token );
        Assert( early.Cancelled && !early.Ok && fake.Calls.Count == 0, "a setup cancelled before it starts sends nothing" );
        using var midway = new CancellationTokenSource();
        var later = await RsDeployer( fake ).RunAsync( fake.Token, message => { if ( message.StartsWith( "Step 3" ) ) midway.Cancel(); }, midway.Token );
        Assert( later.Cancelled && !later.Ok && later.Error.Contains( "Nothing was changed on this computer" ) && !fake.Calls.Any( c => c.Method == "PUT" && c.Path.Contains( "/workers/scripts/" ) ),
            "cancelling part-way stops before the upload and says that nothing was changed on this computer" );
    }

    // ---- the embedded source is the real one ------------------------------------------------------------------------------------------------
    var rsWorkerFile = Path.Combine( versionRoot, "Tools", "RelayWorker", "src", "index.js" );
    if ( File.Exists( rsWorkerFile ) )
    {
        string Normal( string text ) => text.Replace( "\r\n", "\n" ).TrimEnd( '\n' );
        Assert( Normal( File.ReadAllText( rsWorkerFile ) ) == Normal( RelayWorkerSource.Text ), "the relay source embedded in the library equals Tools/RelayWorker/src/index.js (run `npm run embed` in Tools/RelayWorker after changing the Worker)" );
    }
    Assert( RelayWorkerSource.Text.Contains( "export class Room" ) && RelayWorkerSource.Text.Contains( "collab-relay" ), "the embedded source is the relay Worker" );

    // ---- the per-computer relay address ---------------------------------------------------------------------------------------------------
    Assert( LocalRelayFile.Parse( LocalRelayFile.Format( "wss://collab-relay.tester.workers.dev/relay" ), out var rsFileError ) == "wss://collab-relay.tester.workers.dev/relay" && rsFileError == null, "the address written to the local file reads back" );
    Assert( LocalRelayFile.Parse( "# comment\n\n  wss://a.example.com/relay  \n", out _ ) == "wss://a.example.com/relay" && LocalRelayFile.Parse( "", out var rsEmptyError ) == null && rsEmptyError == null && LocalRelayFile.Parse( null, out _ ) == null,
        "comments and blank lines are skipped, and an empty file simply means nothing is set" );
    Assert( LocalRelayFile.Parse( "http://x.example.com/relay", out var rsBadError ) == null && rsBadError != null && rsBadError.Contains( "relay-url.txt" ), "an unusable address is reported by file name and not used" );

    // ---- what the relay card shows ---------------------------------------------------------------------------------------------------------
    const string RsShared = "wss://shared.workers.dev/relay";
    const string RsOwn = "wss://collab-relay.mine.workers.dev/relay";
    RelaySetupView Describe( string project = null, string local = null, string builtIn = null, bool working = false, string progress = null, DeployResult last = null ) =>
        RelaySetupRules.Describe( new RelaySetupInput( project, local, builtIn, working, progress, last ) );
    var rsMissing = Describe();
    Assert( rsMissing.Mode == RelaySetupMode.Missing && rsMissing.ShowOpen && rsMissing.ShowSetup && !rsMissing.ShowCancel && !rsMissing.IsProblem && rsMissing.Text.Contains( "No relay is set up" ) && rsMissing.Text.Contains( "clipboard" ),
        "with no relay at all the card offers both buttons and explains the two steps" );
    var rsSharedView = Describe( builtIn: RsShared );
    Assert( rsSharedView.Mode == RelaySetupMode.Shared && !rsSharedView.ShowOpen && !rsSharedView.ShowSetup && rsSharedView.Text.Contains( "shared.workers.dev" ), "with the library's shared relay there is nothing to set up, and the card names it" );
    Assert( Describe( local: RsOwn, builtIn: RsShared ).Mode == RelaySetupMode.Own && Describe( project: "wss://proj.example.com/relay", local: RsOwn, builtIn: RsShared ).Mode == RelaySetupMode.Project,
        "a relay set up on this computer outranks the shared one, and the project's own setting outranks both" );
    Assert( RelaySetupRules.Fallback( RsOwn, RsShared ) == RsOwn && RelaySetupRules.Fallback( null, RsShared ) == RsShared && RelaySetupRules.Fallback( "  ", RsShared ) == RsShared && RelaySetupRules.Fallback( null, null ) == null,
        "the session and the card pick the same fallback relay" );
    var rsOff = Describe( project: "off", local: RsOwn, builtIn: RsShared );
    Assert( rsOff.Mode == RelaySetupMode.Off && !rsOff.ShowSetup && !rsOff.ShowOpen, "a project that turned relaying off is told so, and not offered a setup that would be ignored" );
    var rsLocalProject = Describe( project: "ws://localhost:8080/relay" );
    var rsLocalFile = Describe( local: "ws://localhost:8080/relay" );
    Assert( rsLocalProject.Mode == RelaySetupMode.LocalOnly && rsLocalProject.IsProblem && !rsLocalProject.ShowSetup && rsLocalFile.Mode == RelaySetupMode.LocalOnly && rsLocalFile.ShowSetup && rsLocalFile.Text.Contains( "only this computer" ),
        "a relay on this computer is called a problem; the setup is offered only when this computer's own file, not the project's, is the cause" );
    var rsWorking = Describe( working: true, progress: "Step 4 of 6: uploading the relay…", builtIn: RsShared );
    Assert( rsWorking.Mode == RelaySetupMode.Working && rsWorking.ShowCancel && !rsWorking.ShowSetup && !rsWorking.ShowOpen && rsWorking.Text.StartsWith( "Step 4 of 6" ), "while a setup runs the card shows its progress and offers only Cancel" );
    var rsFailed = Describe( last: new DeployResult( false, null, "Cloudflare refused the token while finding the account." ) );
    Assert( rsFailed.Mode == RelaySetupMode.Failed && rsFailed.IsProblem && rsFailed.ShowOpen && rsFailed.ShowSetup && rsFailed.Text.StartsWith( "Cloudflare refused the token" ), "a failed setup with no relay in use shows the reason and offers to try again" );
    Assert( Describe( builtIn: RsShared, last: new DeployResult( false, null, "x" ) ).Mode == RelaySetupMode.Shared, "a failed attempt does not hide a relay that is working" );
    Assert( Describe( last: new DeployResult( false, null, "Setup was cancelled.", Cancelled: true ) ) is { Mode: RelaySetupMode.Missing, IsProblem: false } rsCancelled && rsCancelled.Text.Contains( "cancelled" ), "a cancelled setup is not shown as an error" );
    var rsDone = Describe( local: RsOwn, last: new DeployResult( true, RsOwn, null ) );
    Assert( rsDone.Mode == RelaySetupMode.Done && rsDone.Text.StartsWith( "Relay ready" ) && rsDone.Text.Contains( "mine.workers.dev" ) && !rsDone.ShowSetup, "a finished setup says the relay is ready and names it" );
}


// ---- The dock's three sections ------------------------------------------------------------------------------------------------------------------
// The dock used to be one long column of seven cards. It is now three sections (Session, People, Setup), each its own tab, and only what applies right now is shown.
{
    Assert( DockSectionRules.All.Length == 3 && DockSectionRules.All.Distinct().Count() == 3 && DockSectionRules.All[0] == DockTab.Session, "there are three sections and the Session tab comes first" );
    Assert( DockSectionRules.All.All( t => DockSectionRules.Name( t ).Length > 0 && DockSectionRules.Icon( t ).Length > 0 && DockSectionRules.Purpose( t ).EndsWith( "." ) )
        && DockSectionRules.All.Select( DockSectionRules.Name ).Distinct().Count() == 3 && DockSectionRules.All.Select( DockSectionRules.Icon ).Distinct().Count() == 3,
        "each section has its own name, icon and one-sentence purpose for its tooltip" );
    Assert( DockSectionRules.Name( DockTab.Session ) == "Session" && DockSectionRules.Name( DockTab.People ) == "People" && DockSectionRules.Name( DockTab.Setup ) == "Setup", "the sections are named Session, People and Setup" );

    Assert( DockSectionRules.Default( soloCopy: false, readyToHost: true ) == DockTab.Session, "a person who can host opens on the Session tab" );
    Assert( DockSectionRules.Default( soloCopy: false, readyToHost: false ) == DockTab.Setup, "a person who cannot host yet opens on Setup, where the reason and the fix are" );
    Assert( DockSectionRules.Default( soloCopy: true, readyToHost: true ) == DockTab.Setup && DockSectionRules.Default( soloCopy: true, readyToHost: false ) == DockTab.Setup, "a solo test copy opens on Setup, where the test's checklist is" );

    Assert( DockSectionRules.FromIndex( 0, DockTab.People ) == DockTab.Session && DockSectionRules.FromIndex( 2, DockTab.Session ) == DockTab.Setup, "a remembered tab index selects that tab" );
    Assert( DockSectionRules.FromIndex( -1, DockTab.People ) == DockTab.People && DockSectionRules.FromIndex( 3, DockTab.Setup ) == DockTab.Setup && DockSectionRules.FromIndex( 99, DockTab.Session ) == DockTab.Session,
        "a remembered index that is out of range falls back, so a setting left by another version never opens a blank dock" );
    Assert( DockSectionRules.All.All( t => DockSectionRules.FromIndex( DockSectionRules.IndexOf( t ), DockTab.Setup ) == t ), "an index and its tab round-trip" );
    Assert( DockSectionRules.Initial( soloCopy: true, savedIndex: 0, readyToHost: true ) == DockTab.Setup && DockSectionRules.Initial( soloCopy: true, savedIndex: 1, readyToHost: false ) == DockTab.Setup,
        "a solo test copy opens on Setup whatever tab was chosen last, because the editor remembers that choice across projects" );
    Assert( DockSectionRules.Initial( false, 1, true ) == DockTab.People && DockSectionRules.Initial( false, 0, false ) == DockTab.Session, "otherwise the tab the person last chose is used, even when hosting is blocked (the banner explains)" );
    Assert( DockSectionRules.Initial( false, -1, true ) == DockTab.Session && DockSectionRules.Initial( false, -1, false ) == DockTab.Setup && DockSectionRules.Initial( false, 42, true ) == DockTab.Session, "with nothing remembered, or something unusable, the default applies" );

    Assert( !DockSectionRules.ShowActions( inSession: false, hasNotice: false ), "an idle dock does not show Save, Undo or Recover, which have nothing to act on" );
    Assert( DockSectionRules.ShowActions( inSession: true, hasNotice: false ) && DockSectionRules.ShowActions( inSession: false, hasNotice: true ) && DockSectionRules.ShowActions( inSession: true, hasNotice: true ),
        "the actions appear during a session, and after one ends if something happened that needs an answer" );
    Assert( !DockSectionRules.ShowSessionRoster( false ) && DockSectionRules.ShowSessionRoster( true ), "the list of people in the session appears only when there is a session" );

    var dsOk = new ReadinessItem( "steam", "Steam sign-in", ReadinessLevel.Ok, "signed in", null );
    var dsBlocked = new ReadinessItem( "published", "Project publication required", ReadinessLevel.Blocked, "Publish this project", null );
    var dsWarn = new ReadinessItem( "library", "Collaboration library", ReadinessLevel.Warning, "version unknown", null );
    Assert( DockSectionRules.SetupBanner( new[] { dsOk, dsWarn }, inSession: false ) == null, "nothing blocking means no banner; a warning does not block hosting" );
    var dsBanner = DockSectionRules.SetupBanner( new[] { dsOk, dsBlocked }, inSession: false );
    Assert( dsBanner != null && dsBanner.Contains( "project publication required" ) && dsBanner.Contains( "Setup tab" ) && !dsBanner.Contains( ".." ), "a blocked check puts its reason, and where to look, at the top of the Session tab" );
    Assert( DockSectionRules.SetupBanner( new[] { dsOk, dsBlocked }, inSession: true ) == null, "during a session the banner is moot and is not shown" );
    Assert( DockSectionRules.SetupBanner( null, false ) == null && DockSectionRules.SetupBanner( Array.Empty<ReadinessItem>(), false ) == null, "before the checks have answered there is no banner rather than a false alarm" );
}


// ---- The words under Host and Join ---------------------------------------------------------------------------------------------------------------
{
    string cc( bool connected = false, bool isHost = false, bool hosting = false, string route = null, string code = null, string relay = null, string port = null, string play = null, string solo = null, string disc = null )
        => ConnectCopy.Build( connected, isHost, hosting, route, code, relay, port, play, solo, disc );
    Assert( cc() == ConnectCopy.Idle && !ConnectCopy.Idle.Contains( "\n" ), "an idle dock says one thing: host, or paste a code" );
    Assert( cc( code: "ABCD-EFGH" ) == ConnectCopy.ReadyToJoin, "with a code typed, it says it is ready to join" );
    Assert( cc( code: "  " ) == ConnectCopy.Idle, "a code of only spaces counts as none" );
    Assert( cc( isHost: true, hosting: true ) == ConnectCopy.ShareCode, "a host with a live session is told what to do with the code, in one sentence" );
    var hostFull = cc( isHost: true, hosting: true, relay: "Relay ready.", port: "No router answered.", play: "Playing: changes are not shared." ).Split( '\n' );
    Assert( hostFull.Length == 4 && hostFull[0] == ConnectCopy.ShareCode && hostFull[1] == "Relay ready." && hostFull[2] == "No router answered." && hostFull[3].StartsWith( "Playing" ), "a host's detail lines follow the lead, in a fixed order" );
    Assert( cc( connected: true, route: "Connected through the relay." ) == "Connected through the relay.", "a connected guest sees how they are connected" );
    Assert( cc( connected: true, isHost: true, hosting: true ) == ConnectCopy.ShareCode, "a host is never shown a guest's route line" );
    Assert( !cc( isHost: true, hosting: true, relay: "", port: "  ", play: null ).Contains( "\n" ), "empty detail lines add nothing" );
    Assert( cc( disc: "Discovery failed." ) == ConnectCopy.Idle + "\nDiscovery failed.", "a discovery failure is shown on its own line under the idle lead" );
    Assert( cc( code: "X", solo: "Solo test copy." ).EndsWith( "\nSolo test copy." ), "the solo-copy notice is always last" );
    Assert( !cc( isHost: true, hosting: true, relay: "Relay ready." ).Contains( "Public endpoint" ), "the host's public address is not in the words under the buttons" );
}


// ---- Red means something went wrong ---------------------------------------------------------------------------------------------------------------
{
    Assert( !ControlsMessageRules.IsProblem( null, null, null ), "a save, undo or action notice alone is not a problem" );
    Assert( ControlsMessageRules.IsProblem( "teardown failed", null, null ) && ControlsMessageRules.IsProblem( null, "relay refused", null ) && ControlsMessageRules.IsProblem( null, null, "recovery archive written" ),
        "a teardown error, a session error and a recovery notice are each a problem" );
    Assert( !ControlsMessageRules.IsProblem( null, null, null ) && ControlsMessageRules.Compose( null, null, null, null, null, null, "Auto-saved the synchronized scene at 01:14." ).StartsWith( "Auto-saved" ),
        "an auto-save line still reaches the card; it is only shown quietly and does not keep the card open" );
}

SectionRunner.RunAll( Assert );

Console.WriteLine($"{passed} tests passed");

// Faked transport: the request shape and the decision are asserted without a network.
sealed class FakeTokenTransport : ISteamTokenTransport
{
    readonly int? status;
    readonly string body;
    public int Calls;
    public string LastUrl;
    public string LastBody;
    public FakeTokenTransport( int? status, string body ) { this.status = status; this.body = body; }
    public Task<SteamTokenHttpResponse> PostAsync( string url, string jsonBody, CancellationToken cancellationToken )
    {
        Calls++;
        LastUrl = url;
        LastBody = jsonBody;
        return Task.FromResult( new SteamTokenHttpResponse( status, body ) );
    }
}

// A transport that fails the way a dead DNS or TLS endpoint does.
sealed class ThrowingTokenTransport : ISteamTokenTransport
{
    public Task<SteamTokenHttpResponse> PostAsync( string url, string jsonBody, CancellationToken cancellationToken )
        => throw new HttpRequestException( "connection refused for " + url );
}


sealed class FlagDisposable : IDisposable
{
    public bool Invoked { get; set; }
    public int DisposeCount { get; private set; }
    public void Dispose() { Invoked = true; DisposeCount++; }
}

sealed class TrackedDisposable : IDisposable
{
    public int DisposeCount { get; private set; }
    public void Dispose() => DisposeCount++;
}

sealed class ThrowingDisposable : IDisposable
{
    readonly string message;
    public ThrowingDisposable( string message = "transport dispose failed" ) { this.message = message; }
    public int DisposeCount { get; private set; }
    public void Dispose()
    {
        DisposeCount++;
        throw new IOException( message );
    }
}

sealed class JsonElementPassThroughConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonDocument.ParseValue(ref reader).RootElement.Clone();
    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options) =>
        value.WriteTo(writer);
}
