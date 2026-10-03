using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamCreate;
using TeamCreate.Relay;
using TeamCreate.RelayTests;

// Integration tests: a real relay on a real loopback socket, driven by the real Wire, RelayHost and
// RelayGuest. Each case starts its own relay so limits and rooms never leak between them.

// `solo-copy <projectRoot> [--manual]` builds the disposable solo test copy with the SAME code the dock button uses (Core/SoloCopy.cs) and prints its path
// and the path of the .sbproj to open. Tools/Run-SoloTest.ps1 calls it, so the launcher and the button cannot drift apart.
if (args.Length >= 2 && args[0] == "solo-copy")
{
    var built = SoloCopy.Build(args[1], autorun: !args.Contains("--manual"));
    if (!built.Ok)
    {
        Console.Error.WriteLine("ERROR: " + built.Error);
        Environment.Exit(1);
    }
    Console.WriteLine("COPY=" + built.Path);
    Console.WriteLine("PROJECT=" + Directory.EnumerateFiles(built.Path, "*.sbproj").First());
    Console.WriteLine("FILES=" + built.Files);
    Environment.Exit(0);
}

// `worker <relay url> [accessKey]` holds ANY relay reachable at a URL (the Cloudflare Worker, or the .NET relay) to the same behaviour, using the
// library's real client code. See WorkerConformance.cs.
if (args.Length >= 2 && args[0] == "worker")
{
    Environment.Exit(await WorkerConformance.Run(args[1], args.Length >= 3 ? args[2] : null));
}

// `tunnel-e2e <verified exe>` exercises a guest across the real internet, not merely a local relay.
if ( args.Length >= 2 && args[0] == "tunnel-e2e" )
{
    Environment.Exit( await TunnelE2e.Run( args[1] ) );
}

// `embedded` runs the same conformance suite against the host-run relay (Connect/EmbeddedRelay.cs), started in-process. See EmbeddedConformance.cs.
if (args.Length >= 1 && args[0] == "embedded")
{
    Environment.Exit(EmbeddedConformance.Run());
}

// `measure <relay url> [seconds] [pingMs]` counts the WebSocket messages a two-person session sends a relay. See MeasureUsage.cs.
if (args.Length >= 2 && args[0] == "measure")
{
    MeasureUsage.Legacy = args.Contains("--legacy");
    MeasureUsage.Moving = args.Contains("--moving");
    Environment.Exit(await MeasureUsage.Run(args[1], args.Length >= 3 && int.TryParse(args[2], out int secs) ? secs : 20));
}

if (args.Length >= 2 && args[0] == "join")
{
    Environment.Exit(await JoinDriver.Run(args[1], "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f"));
}

int passed = 0;
void Assert(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

const string ProjectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";
byte[] Secret( byte seed ) => Enumerable.Range( 0, 16 ).Select( i => (byte)( seed + i ) ).ToArray();
byte[] KeyFor( byte[] secret ) => InviteCode.DeriveKey( secret, ProjectId );

async Task<bool> WaitFor( Func<bool> condition, double seconds = 8 )
{
    var deadline = DateTime.UtcNow.AddSeconds( seconds );
    while ( DateTime.UtcNow < deadline )
    {
        if ( condition() ) return true;
        await Task.Delay( 15 );
    }
    return condition();
}

async Task<Packet> NextPacket( Wire wire, double seconds = 8 )
{
    Packet result = null;
    await WaitFor( () => wire.Incoming.TryDequeue( out result ), seconds );
    return result;
}

async Task<Wire> NextAccepted( RelayHost host, double seconds = 8 )
{
    Wire result = null;
    await WaitFor( () => host.Accepted.TryDequeue( out result ), seconds );
    return result;
}

async Task<(WebApplication App, string Url, RelayServer Server)> StartRelay( RelayOptions options )
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls( "http://127.0.0.1:0" );
    // Each case leaves a live host socket open while the relay stops. The default graceful-shutdown wait
    // is 30 seconds per relay, which made the suite look hung; a second is plenty for loopback.
    builder.Services.Configure<HostOptions>( o => o.ShutdownTimeout = TimeSpan.FromSeconds( 1 ) );
    var server = new RelayServer( options );
    builder.Services.AddSingleton( server );
    var app = builder.Build();
    app.UseWebSockets();
    app.Map( options.Path, async ( HttpContext context, RelayServer relay ) =>
    {
        if ( !context.WebSockets.IsWebSocketRequest ) { context.Response.StatusCode = 426; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await relay.HandleAsync( socket, context.Connection.RemoteIpAddress?.ToString() ?? "unknown", context.RequestAborted );
    } );
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
    return (app, address.Replace( "http://", "ws://" ) + options.Path, server);
}

// A raw client: connects, sends what it is told and reports the first control reply and the close.
async Task<(RelayControl Reply, bool Closed)> Raw( string url, Func<ClientSocket, Task> act, double seconds = 4 )
{
    using var socket = new ClientSocket();
    RelayControl reply = null;
    var closed = false;
    socket.TextReceived += text => { if ( RelayProtocol.TryParseControl( text, out var c, out _ ) ) reply ??= c; };
    socket.Closed += ( _, _ ) => closed = true;
    await socket.ConnectAsync( url, CancellationToken.None );
    await act( socket );
    await WaitFor( () => reply != null, seconds );
    await WaitFor( () => closed, 2 );
    return (reply, closed);
}

string HostHelloJson( byte[] room, byte[] hostSecret, byte[] guestSecret, string accessKey = null ) =>
    System.Text.Json.JsonSerializer.Serialize( new RelayControl
    {
        Type = RelayProtocol.HostHello, Version = RelayProtocol.ProtocolVersion,
        Room = RelayProtocol.Hex( room ), HostProof = RelayProtocol.Hex( RelayProtocol.DeriveHostProof( hostSecret ) ),
        GuestProof = RelayProtocol.Hex( RelayProtocol.DeriveGuestProof( guestSecret ) ), AccessKey = accessKey
    } );

string GuestHelloJson( byte[] room, byte[] proofSecret, int version = RelayProtocol.ProtocolVersion ) =>
    System.Text.Json.JsonSerializer.Serialize( new RelayControl
    {
        Type = RelayProtocol.GuestHello, Version = version,
        Room = RelayProtocol.Hex( room ), Proof = RelayProtocol.Hex( RelayProtocol.DeriveGuestProof( proofSecret ) )
    } );

var fastBackoff = new[] { TimeSpan.FromMilliseconds( 60 ) };

// ---- A guest and a host talk through the relay, encrypted end to end ---------------------------
{
    var (app, url, server) = await StartRelay( new RelayOptions() );
    var secret = Secret( 1 );
    var key = KeyFor( secret );
    var hostSockets = new List<ClientSocket>();
    var hostSeen = new List<byte[]>();
    using var host = new RelayHost( () =>
    {
        var s = new ClientSocket { OutboundObserver = b => { lock ( hostSeen ) hostSeen.Add( b ); } };
        lock ( hostSockets ) hostSockets.Add( s );
        return s;
    }, url, secret, key, null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    Assert( await WaitFor( () => host.State == RelayHostState.Ready ), "a host registers its room with the relay and becomes ready" );
    Assert( server.RoomCount == 1, "the relay holds exactly one room for it" );

    var guestSeen = new List<byte[]>();
    using var guest = await RelayGuest.ConnectAsync( () => new ClientSocket { OutboundObserver = b => { lock ( guestSeen ) guestSeen.Add( b ); } }, url, secret, key, anyLoopbackPort: true );
    var accepted = await NextAccepted( host );
    Assert( accepted != null, "the relay announces the guest to the host, which surfaces it as an accepted wire" );

    const string marker = "PLAINTEXT-MARKER-4F2A9C";
    guest.Send( new Packet { Kind = "hello", Peer = "guest-1", Name = marker } );
    var atHost = await NextPacket( accepted );
    Assert( atHost != null && atHost.Kind == "hello" && atHost.Name == marker, "a packet sent by the guest arrives at the host intact" );
    accepted.Send( new Packet { Kind = "welcome", Session = "s-1" } );
    var atGuest = await NextPacket( guest );
    Assert( atGuest != null && atGuest.Kind == "welcome" && atGuest.Session == "s-1", "a packet sent by the host arrives at the guest intact" );

    // What the relay is handed must be unreadable: the marker travelled inside AES-GCM frames.
    var needle = System.Text.Encoding.UTF8.GetBytes( marker );
    bool Contains( List<byte[]> seen ) { lock ( seen ) return seen.Any( b => b.AsSpan().IndexOf( needle ) >= 0 ); }
    Assert( !Contains( guestSeen ) && !Contains( hostSeen ),
        "no byte handed to the relay contains the plaintext, so the relay cannot read the session" );

    // Ordering and volume: two hundred packets in one burst arrive complete and in order.
    for ( var i = 0; i < 200; i++ ) guest.Send( new Packet { Kind = "presence", Id = "p" + i } );
    var inOrder = true;
    for ( var i = 0; i < 200; i++ )
    {
        var next = await NextPacket( accepted );
        if ( next == null || next.Id != "p" + i ) { inOrder = false; break; }
    }
    Assert( inOrder, "two hundred packets sent back to back arrive in order through the relay" );

    // Chunking: a 3 MiB packet is split into small relay messages and reassembled by the wire framing.
    var big = new string( 'q', 3 * 1024 * 1024 );
    accepted.Send( new Packet { Kind = "snapshot", Error = big } );
    var bigAtGuest = await NextPacket( guest, 15 );
    Assert( bigAtGuest != null && bigAtGuest.Error != null && bigAtGuest.Error.Length == big.Length,
        "a multi-megabyte packet crosses the relay in chunks and arrives whole" );

    // A second guest gets its own isolated channel.
    using var guest2 = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    var accepted2 = await NextAccepted( host );
    guest2.Send( new Packet { Kind = "hello", Peer = "guest-2" } );
    var at2 = await NextPacket( accepted2 );
    Assert( accepted2 != null && at2 != null && at2.Peer == "guest-2" && accepted.Incoming.IsEmpty,
        "a second guest gets its own channel and its traffic never reaches the first guest's wire" );
    Assert( server.GuestCount( RelayProtocol.Hex( RelayProtocol.DeriveRoom( secret ) ) ) == 2, "the relay counts both guests in the room" );

    // Leaving: the guest's close reaches the host as a closed wire, and the relay forgets the channel.
    guest2.Dispose();
    Assert( await WaitFor( () => accepted2.Failure != null ), "a guest that leaves surfaces to the host as a closed wire" );
    Assert( await WaitFor( () => server.GuestCount( RelayProtocol.Hex( RelayProtocol.DeriveRoom( secret ) ) ) == 1 ), "the relay drops that guest's channel" );

    // Host loss: the relay's own socket to the host drops. Guests are closed, and the host reconnects by itself.
    ClientSocket firstSocket;
    lock ( hostSockets ) firstSocket = hostSockets[0];
    firstSocket.Dispose();
    Assert( await WaitFor( () => guest.Failure != null ), "when the host's relay connection drops, its guests see a closed connection instead of hanging" );
    Assert( await WaitFor( () => host.State == RelayHostState.Ready && hostSockets.Count >= 2 ), "the host re-registers its room on its own after the drop" );
    using var again = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    var acceptedAgain = await NextAccepted( host );
    again.Send( new Packet { Kind = "hello", Peer = "returned" } );
    var atAgain = await NextPacket( acceptedAgain );
    Assert( atAgain != null && atAgain.Peer == "returned", "a guest can join again through the re-registered room" );

    await app.StopAsync();
}

// ---- Refusals -------------------------------------------------------------------------------
{
    var (app, url, _) = await StartRelay( new RelayOptions() );
    var secret = Secret( 10 );
    var key = KeyFor( secret );
    RelayRefusedException refused = null;
    try { using var w = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true ); }
    catch ( RelayRefusedException ex ) { refused = ex; }
    Assert( refused != null && refused.Code == "no-host" && refused.Message.Contains( "not connected to the relay" ),
        "a guest arriving before the host is told, in plain words, that the host is not connected" );
    Assert( AutoRecoveryRules.Classify( refused ) == SessionFailureClass.Terminal
        && AutoRecoveryRules.Classify( new IOException( "The relay closed the connection." ) ) == SessionFailureClass.Transient,
        "a relay refusal is terminal for automatic recovery while a dropped relay connection stays retryable" );

    using var host = new RelayHost( () => new ClientSocket(), url, secret, key, null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    await WaitFor( () => host.State == RelayHostState.Ready );
    var room = RelayProtocol.DeriveRoom( secret );

    var otherSecret = Secret( 99 );
    var (wrongProof, _) = await Raw( url, s => s.SendTextAsync( GuestHelloJson( room, otherSecret ), CancellationToken.None ) );
    Assert( wrongProof?.Code == "bad-proof", "a guest that knows the room but not the secret is refused with bad-proof" );

    var (takenReply, _) = await Raw( url, s => s.SendTextAsync( HostHelloJson( room, otherSecret, secret ), CancellationToken.None ) );
    Assert( takenReply?.Code == "room-taken", "a second host with a different proof cannot take over a live room" );

    RelayRefusedException differentRoom = null;
    try { using var w = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, otherSecret, KeyFor( otherSecret ), anyLoopbackPort: true ); }
    catch ( RelayRefusedException ex ) { differentRoom = ex; }
    Assert( differentRoom?.Code == "no-host", "an invite from a different session addresses a different room and finds no host" );

    var (junk, _) = await Raw( url, s => s.SendTextAsync( "this is not json", CancellationToken.None ) );
    Assert( junk?.Code == "bad-hello", "a first message that is not JSON is refused" );
    var (binaryFirst, _) = await Raw( url, s => s.SendBinaryAsync( new byte[] { 1, 2, 3 }, CancellationToken.None ) );
    Assert( binaryFirst?.Code == "bad-hello", "a first message that is binary is refused" );
    var (badVersion, _) = await Raw( url, s => s.SendTextAsync( GuestHelloJson( room, secret, version: 99 ), CancellationToken.None ) );
    Assert( badVersion?.Code == "version", "a hello for another protocol version is refused with the version code" );
    var (oversized, closed) = await Raw( url, s => s.SendTextAsync( new string( 'a', 9000 ), CancellationToken.None ) );
    Assert( oversized?.Code == "limit" && closed, "an oversized first message is refused and the socket is closed" );
    Assert( RelayProtocol.UserMessage( "bad-proof", null ).Contains( "Copy the code again" ), "every relay refusal has a formal, actionable sentence" );
    await app.StopAsync();
}

// ---- Access keys ----------------------------------------------------------------------------
{
    var (app, url, server) = await StartRelay( new RelayOptions { AccessKeys = new[] { "correct-key-1", "another-key" } } );
    var secret = Secret( 20 );
    var key = KeyFor( secret );
    using var keyless = new RelayHost( () => new ClientSocket(), url, secret, key, null, fastBackoff, anyLoopbackPort: true );
    keyless.Start();
    Assert( await WaitFor( () => keyless.State == RelayHostState.Failed ), "a host without an access key is refused and stops instead of retrying forever" );
    Assert( keyless.Failure != null && keyless.Failure.Contains( "access key" ) && server.RoomCount == 0, "the refusal names the access key and no room was created" );

    using var wrong = new RelayHost( () => new ClientSocket(), url, secret, key, "guess", fastBackoff, anyLoopbackPort: true );
    wrong.Start();
    Assert( await WaitFor( () => wrong.State == RelayHostState.Failed ), "a host with the wrong access key is refused" );

    using var right = new RelayHost( () => new ClientSocket(), url, secret, key, "another-key", fastBackoff, anyLoopbackPort: true );
    right.Start();
    Assert( await WaitFor( () => right.State == RelayHostState.Ready ), "a host with an accepted access key registers its room" );

    using var guest = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    Assert( await NextAccepted( right ) != null, "a guest needs no access key: the invite secret is its credential" );
    await app.StopAsync();
}
{
    var options = new RelayOptions { AccessKeys = new[] { "alpha", "beta" } };
    Assert( options.AccessKeyAccepted( "alpha" ) && options.AccessKeyAccepted( "beta" ), "each configured key is accepted" );
    Assert( !options.AccessKeyAccepted( "alph" ) && !options.AccessKeyAccepted( "alphaa" ) && !options.AccessKeyAccepted( "" ) && !options.AccessKeyAccepted( null ),
        "a prefix, an extension, an empty key and a missing key are all refused" );
    Assert( new RelayOptions().AccessKeyAccepted( null ), "with no keys configured every host is accepted, and startup warns about it" );
}

// ---- Limits ---------------------------------------------------------------------------------
{
    var (app, url, _) = await StartRelay( new RelayOptions { MaxGuestsPerRoom = 2 } );
    var secret = Secret( 30 );
    var key = KeyFor( secret );
    using var host = new RelayHost( () => new ClientSocket(), url, secret, key, null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    await WaitFor( () => host.State == RelayHostState.Ready );
    using var g1 = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    using var g2 = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    RelayRefusedException full = null;
    try { using var g3 = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true ); }
    catch ( RelayRefusedException ex ) { full = ex; }
    Assert( full?.Code == "full", "a guest beyond the room's limit is refused as full" );
    await app.StopAsync();
}
{
    var (app, url, server) = await StartRelay( new RelayOptions { MaxRooms = 1 } );
    using var h1 = new RelayHost( () => new ClientSocket(), url, Secret( 40 ), KeyFor( Secret( 40 ) ), null, fastBackoff, anyLoopbackPort: true );
    h1.Start();
    await WaitFor( () => h1.State == RelayHostState.Ready );
    var (limit, _) = await Raw( url, s => s.SendTextAsync( HostHelloJson( RelayProtocol.DeriveRoom( Secret( 41 ) ), Secret( 41 ), Secret( 41 ) ), CancellationToken.None ) );
    Assert( limit?.Code == "limit" && server.RoomCount == 1, "a host beyond the relay's room limit is refused" );
    await app.StopAsync();
}
{
    var (app, url, _) = await StartRelay( new RelayOptions { MaxConnectionsPerAddress = 2 } );
    var a = new ClientSocket();
    var b = new ClientSocket();
    await a.ConnectAsync( url, CancellationToken.None );
    await b.ConnectAsync( url, CancellationToken.None );
    var (over, _) = await Raw( url, _ => Task.CompletedTask );
    Assert( over?.Code == "limit", "a third simultaneous connection from one address is refused" );
    a.Dispose();
    b.Dispose();
    await app.StopAsync();
}
{
    var (app, url, _) = await StartRelay( new RelayOptions { MaxFailuresPerAddress = 3 } );
    var secret = Secret( 50 );
    using var host = new RelayHost( () => new ClientSocket(), url, secret, KeyFor( secret ), null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    await WaitFor( () => host.State == RelayHostState.Ready );
    var room = RelayProtocol.DeriveRoom( secret );
    for ( var i = 0; i < 3; i++ ) await Raw( url, s => s.SendTextAsync( GuestHelloJson( room, Secret( 200 ) ), CancellationToken.None ) );
    var (throttled, _) = await Raw( url, s => s.SendTextAsync( GuestHelloJson( room, secret ), CancellationToken.None ) );
    Assert( throttled?.Code == "limit", "an address that keeps presenting wrong proofs is throttled, even when its next proof is right" );
    await app.StopAsync();
}
{
    var (app, url, _) = await StartRelay( new RelayOptions { MaxBytesPerSecond = 1000, BurstBytes = 30000 } );
    var secret = Secret( 60 );
    using var host = new RelayHost( () => new ClientSocket(), url, secret, KeyFor( secret ), null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    await WaitFor( () => host.State == RelayHostState.Ready );
    var (flood, closed) = await Raw( url, async s =>
    {
        await s.SendTextAsync( GuestHelloJson( RelayProtocol.DeriveRoom( secret ), secret ), CancellationToken.None );
        await Task.Delay( 200 );
        for ( var i = 0; i < 20; i++ )
        {
            try { await s.SendBinaryAsync( new byte[10000], CancellationToken.None ); } catch { break; }
        }
    }, 3 );
    // The first control reply is the ok; the limit error follows it.
    using var socket2 = new ClientSocket();
    var codes = new List<string>();
    socket2.TextReceived += t => { if ( RelayProtocol.TryParseControl( t, out var c, out _ ) ) lock ( codes ) codes.Add( c.Code ?? c.Type ); };
    await socket2.ConnectAsync( url, CancellationToken.None );
    await socket2.SendTextAsync( GuestHelloJson( RelayProtocol.DeriveRoom( secret ), secret ), CancellationToken.None );
    await Task.Delay( 200 );
    for ( var i = 0; i < 20; i++ )
    {
        try { await socket2.SendBinaryAsync( new byte[10000], CancellationToken.None ); } catch { break; }
    }
    Assert( await WaitFor( () => { lock ( codes ) return codes.Contains( "limit" ); }, 4 ), "a connection that exceeds its bandwidth budget is told so and dropped" );
    await app.StopAsync();
}
{
    var (app, url, _) = await StartRelay( new RelayOptions { IdleTimeoutSeconds = 1 } );
    var secret = Secret( 70 );
    using var socket = new ClientSocket();
    var closed = false;
    socket.Closed += ( _, _ ) => closed = true;
    await socket.ConnectAsync( url, CancellationToken.None );
    await socket.SendTextAsync( HostHelloJson( RelayProtocol.DeriveRoom( secret ), secret, secret ), CancellationToken.None );
    Assert( await WaitFor( () => closed, 6 ), "a connection that goes silent past the idle limit is closed by the relay" );
    await app.StopAsync();
}

// ---- A relay that tampers cannot alter the session unnoticed --------------------------------
{
    var (app, url, _) = await StartRelay( new RelayOptions() );
    var secret = Secret( 80 );
    var key = KeyFor( secret );
    var dataFrames = 0;
    using var host = new RelayHost( () => new ClientSocket
    {
        // Duplicate the second data frame of the channel: the first is the encryption preamble, the second
        // is the sealed hello. A relay replaying a sealed frame must be caught by the sequence rule.
        InboundBinaryFilter = message =>
        {
            if ( message.Length > 0 && message[0] == (byte)RelayFrameType.Data && Interlocked.Increment( ref dataFrames ) == 2 )
                return new[] { message, (byte[])message.Clone() };
            return new[] { message };
        }
    }, url, secret, key, null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    await WaitFor( () => host.State == RelayHostState.Ready );
    using var guest = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    var accepted = await NextAccepted( host );
    guest.Send( new Packet { Kind = "hello", Peer = "replayed" } );
    Assert( await WaitFor( () => accepted.Failure != null ), "a relay that replays a sealed frame ends the session instead of feeding it twice" );
    Assert( accepted.Failure == AeadFraming.SequenceFailure, "the failure says the stream was replayed, not that the invite code was wrong" );
    await app.StopAsync();
}
{
    var (app, url, _) = await StartRelay( new RelayOptions() );
    var secret = Secret( 90 );
    var key = KeyFor( secret );
    var seen = 0;
    using var host = new RelayHost( () => new ClientSocket
    {
        // Drop the second data frame: the hello never arrives and the third frame is out of sequence.
        InboundBinaryFilter = message =>
        {
            if ( message.Length > 0 && message[0] == (byte)RelayFrameType.Data && Interlocked.Increment( ref seen ) == 2 )
                return Array.Empty<byte[]>();
            return new[] { message };
        }
    }, url, secret, key, null, fastBackoff, anyLoopbackPort: true );
    host.Start();
    await WaitFor( () => host.State == RelayHostState.Ready );
    using var guest = await RelayGuest.ConnectAsync( () => new ClientSocket(), url, secret, key, anyLoopbackPort: true );
    var accepted = await NextAccepted( host );
    guest.Send( new Packet { Kind = "hello", Peer = "dropped" } );
    guest.Send( new Packet { Kind = "presence", Peer = "dropped" } );
    Assert( await WaitFor( () => accepted.Failure == AeadFraming.SequenceFailure ), "a relay that withholds a frame is detected when the next one arrives" );
    await app.StopAsync();
}

// ---- The host gives up when the relay stays down ---------------------------------------------
{
    using var host = new RelayHost( () => throw new IOException( "connection refused" ), "ws://127.0.0.1:8080/relay", Secret( 5 ), KeyFor( Secret( 5 ) ), null,
        new[] { TimeSpan.FromMilliseconds( 5 ) }, anyLoopbackPort: true );
    host.Start();
    Assert( await WaitFor( () => host.State == RelayHostState.Failed, 10 ), "a relay that never answers is given up on after a bounded number of attempts" );
    Assert( host.Failure != null && host.Failure.Contains( RelayHost.MaxReconnectAttempts.ToString() ) && host.Failure.Contains( "local network" ),
        "the failure states the attempt budget and points at the local-network route" );
}

Console.WriteLine( $"{passed} relay tests passed" );
