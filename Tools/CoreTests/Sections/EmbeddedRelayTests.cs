using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TeamCreate;

/// <summary>
/// The host-run relay (EmbeddedRelay) over real loopback sockets: real ClientWebSocket clients, a real HttpListener, the relay pumped from this thread
/// exactly as the editor frame pumps it. Every wait is bounded at five seconds and fails its named assertion instead of hanging. Each scenario starts its
/// own relay on a port the OS reports free, so rooms, limits and counters never leak between scenarios or between parallel runs.
/// </summary>
public static class EmbeddedRelayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds( 5 );

    // The worker's /health body (Tools/RelayWorker/src/index.js: Response.json({ status: "ok", service: "collab-relay", protocol: 1 })).
    private const string WorkerHealth = "{\"status\":\"ok\",\"service\":\"collab-relay\",\"protocol\":1}";

    private sealed record Msg( WebSocketMessageType Type, byte[] Data, WebSocketCloseStatus? Status, bool Aborted )
    {
        public string Text => Encoding.UTF8.GetString( Data );

        public bool IsClose => Type == WebSocketMessageType.Close;
    }

    // A clock the scenario moves by hand, so the proof deadline and the rate window are exact rather than raced against wall time.
    private sealed class Clock
    {
        public double Now = 1000;
    }

    private static int FreePort()
    {
        var tcp = new TcpListener( IPAddress.Loopback, 0 );
        tcp.Start();
        int port = ( (IPEndPoint)tcp.LocalEndpoint ).Port;
        tcp.Stop();
        return port;
    }

    private static (EmbeddedRelay Relay, Clock Clock) Start()
    {
        var clock = new Clock();
        var relay = new EmbeddedRelay( FreePort(), () => clock.Now );
        relay.Start();
        return ( relay, clock );
    }

    /// <summary>Pumps the relay until the task completes or five seconds pass. True when it completed.</summary>
    private static bool Drive( EmbeddedRelay relay, Task task )
    {
        var timer = Stopwatch.StartNew();
        while ( !task.IsCompleted && timer.Elapsed < Deadline )
        {
            relay.Pump();
            Thread.Sleep( 1 );
        }
        relay.Pump();
        return task.IsCompleted;
    }

    private static void PumpFor( EmbeddedRelay relay, int milliseconds )
    {
        var timer = Stopwatch.StartNew();
        while ( timer.ElapsedMilliseconds < milliseconds )
        {
            relay.Pump();
            Thread.Sleep( 1 );
        }
    }

    private static async Task<Msg> ReceiveAsync( ClientWebSocket socket )
    {
        byte[] buffer = new byte[RelayLimits.MaxFrameBytes + 1024];
        int size = 0;
        try
        {
            while ( true )
            {
                var result = await socket.ReceiveAsync( new ArraySegment<byte>( buffer, size, buffer.Length - size ), CancellationToken.None );
                size += result.Count;
                if ( result.MessageType == WebSocketMessageType.Close )
                    return new Msg( WebSocketMessageType.Close, Array.Empty<byte>(), result.CloseStatus, false );
                if ( result.EndOfMessage )
                    return new Msg( result.MessageType, buffer.AsSpan( 0, size ).ToArray(), null, false );
            }
        }
        catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is OperationCanceledException || ex is IOException )
        {
            return new Msg( WebSocketMessageType.Close, Array.Empty<byte>(), null, true );
        }
    }

    /// <summary>The next message on a socket, or null after five seconds.</summary>
    private static Msg Next( EmbeddedRelay relay, ClientWebSocket socket ) => Await( relay, ReceiveAsync( socket ) );

    private static Msg Await( EmbeddedRelay relay, Task<Msg> pending ) => Drive( relay, pending ) ? pending.Result : null;

    /// <summary>Reads past text refusals to the close that must follow; true when the socket was closed or aborted within the deadline.</summary>
    private static bool ClosedSoon( EmbeddedRelay relay, ClientWebSocket socket, out Msg close )
    {
        close = null;
        for ( int i = 0; i < 4; i++ )
        {
            var msg = Next( relay, socket );
            if ( msg == null )
                return false;
            if ( msg.IsClose )
            {
                close = msg;
                return true;
            }
        }
        return false;
    }

    private static bool Send( EmbeddedRelay relay, ClientWebSocket socket, byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Binary )
    {
        try
        {
            return Drive( relay, socket.SendAsync( new ArraySegment<byte>( bytes ), type, true, CancellationToken.None ) );
        }
        catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException )
        {
            return false;
        }
    }

    private static bool SendText( EmbeddedRelay relay, ClientWebSocket socket, string text ) => Send( relay, socket, Encoding.UTF8.GetBytes( text ), WebSocketMessageType.Text );

    private static ClientWebSocket NewClient()
    {
        var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.CollectHttpResponseDetails = true;
        return socket;
    }

    private static ClientWebSocket Connect( EmbeddedRelay relay, string room )
    {
        var socket = NewClient();
        var connecting = socket.ConnectAsync( new Uri( "ws://localhost:" + relay.Port + "/relay?room=" + room ), CancellationToken.None );
        if ( !Drive( relay, connecting ) || connecting.Status != TaskStatus.RanToCompletion )
            throw new InvalidOperationException( "The embedded relay did not accept a WebSocket for room " + room + "." );
        return socket;
    }

    private static string Room( byte[] secret ) => RelayProtocol.Hex( RelayProtocol.DeriveRoom( secret ) );

    private static byte[] Secret( byte seed ) => Enumerable.Range( 0, 16 ).Select( i => (byte)( seed + i * 7 ) ).ToArray();

    /// <summary>Opens a socket and presents a hello; returns the socket and the relay's first answer (null on timeout).</summary>
    private static (ClientWebSocket Socket, Msg Reply) Hello( EmbeddedRelay relay, string room, string hello )
    {
        var socket = Connect( relay, room );
        SendText( relay, socket, hello );
        return ( socket, Next( relay, socket ) );
    }

    private static bool IsOk( Msg msg ) => msg != null && msg.Type == WebSocketMessageType.Text && msg.Text == RelayProtocol.BuildOk();

    private static string ErrorCode( Msg msg )
    {
        if ( msg == null || msg.Type != WebSocketMessageType.Text || !RelayProtocol.TryParseControl( msg.Text, out var control, out _ ) || control.Type != RelayProtocol.Error )
            return null;
        return control.Code;
    }

    private static ClientWebSocket Host( EmbeddedRelay relay, byte[] secret, Action<bool, string> assert, string name )
    {
        var ( socket, reply ) = Hello( relay, Room( secret ), RelayProtocol.BuildHostHello( secret, null ) );
        assert( IsOk( reply ), name );
        return socket;
    }

    /// <summary>A guest joins; asserts the guest's ok and the host's Open frame, and returns the guest with its channel.</summary>
    private static (ClientWebSocket Socket, uint Channel) Guest( EmbeddedRelay relay, ClientWebSocket host, byte[] secret, Action<bool, string> assert, string name )
    {
        var ( socket, reply ) = Hello( relay, Room( secret ), RelayProtocol.BuildGuestHello( secret ) );
        var open = Next( relay, host );
        bool opened = open != null && RelayProtocol.TryParseFrame( open.Data, out var kind, out uint channel, out _ ) && kind == RelayFrameType.Open;
        RelayProtocol.TryParseFrame( open?.Data ?? Array.Empty<byte>(), out _, out uint parsed, out _ );
        assert( IsOk( reply ) && opened, name );
        return ( socket, parsed );
    }

    private static bool IsFrame( Msg msg, RelayFrameType expected, uint channel, byte[] payload = null )
    {
        if ( msg == null || msg.Type != WebSocketMessageType.Binary || !RelayProtocol.TryParseFrame( msg.Data, out var kind, out uint got, out var data ) )
            return false;
        return kind == expected && got == channel && ( payload == null || data.SequenceEqual( payload ) );
    }

    private static byte[] Pattern( int length, int seed ) => Enumerable.Range( 0, length ).Select( i => (byte)( ( i * 31 + seed ) & 0xFF ) ).ToArray();

    public static void Run( Action<bool, string> assert )
    {
        Http( assert );
        Forwarding( assert );
        Membership( assert );
        ProofDeadline( assert );
        PendingCap( assert );
        RateLimit( assert );
        SlowReader( assert );
        HostDeparture( assert );
        RoomsIsolated( assert );
        DisposeAndPort( assert );
        LibraryClients( assert );
    }

    // ---- HTTP surface: health, the allow-list, upgrade refusals, loopback only ----------------------------------------------

    private static void Http( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        using ( relay )
        using ( var http = new HttpClient( new SocketsHttpHandler { UseProxy = false } ) )
        {
            assert( relay.Prefix == "http://localhost:" + relay.Port + "/", "embrelay_prefix_is_loopback_only" );
            string Base( string path ) => "http://localhost:" + relay.Port + path;
            HttpResponseMessage Get( string path )
            {
                var task = http.GetAsync( Base( path ) );
                return Drive( relay, task ) && task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            }
            string Body( HttpResponseMessage response )
            {
                var task = response.Content.ReadAsStringAsync();
                return Drive( relay, task ) ? task.Result : null;
            }

            var health = Get( "/health" );
            assert( health?.StatusCode == HttpStatusCode.OK && Body( health ) == WorkerHealth
                && health.Content.Headers.ContentType?.MediaType == "application/json", "embrelay_health_matches_worker" );

            bool all404 = true;
            foreach ( string path in new[] { "/", "/unknown", "/relay/", "/Health", "/health/", "/RELAY", "/files/" + new string( 'a', 64 ), "/files/" + new string( '0', 63 ), "/files/" + new string( 'A', 64 ) } )
            {
                var response = Get( path );
                bool ok = response?.StatusCode == HttpStatusCode.NotFound && Body( response ) == "";
                if ( !ok )
                    Console.WriteLine( "embrelay: " + path + " answered " + ( response?.StatusCode.ToString() ?? "nothing" ) );
                all404 &= ok;
            }
            assert( all404, "embrelay_allow_list_404_without_body" );
            assert( Get( "/files/" + new string( 'b', 64 ) )?.StatusCode == HttpStatusCode.NotFound, "embrelay_files_route_reserved_404" );
            assert( Get( "/relay" )?.StatusCode == HttpStatusCode.UpgradeRequired, "embrelay_relay_without_upgrade_426" );

            foreach ( string room in new[] { "", "abc", new string( 'A', 32 ), new string( 'g', 32 ), new string( 'a', 33 ) } )
            {
                var bad = NewClient();
                var connecting = bad.ConnectAsync( new Uri( Base( "/relay?room=" + room ).Replace( "http:", "ws:" ) ), CancellationToken.None );
                bool completed = Drive( relay, connecting );
                assert( completed && connecting.IsFaulted && bad.HttpStatusCode == HttpStatusCode.BadRequest, "embrelay_malformed_room_400_" + ( room.Length == 0 ? "empty" : room.Substring( 0, 3 ) + room.Length ) );
                bad.Dispose();
            }

            // A malformed upgrade request and raw garbage: each is refused and the relay keeps serving.
            using ( var raw = new TcpClient() )
            {
                Drive( relay, raw.ConnectAsync( IPAddress.Loopback, relay.Port ) );
                var stream = raw.GetStream();
                byte[] request = Encoding.ASCII.GetBytes( "GET /relay?room=" + new string( 'a', 32 ) + " HTTP/1.1\r\nHost: localhost:" + relay.Port
                    + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: not-a-key\r\n\r\n" );
                stream.Write( request );
                byte[] answer = new byte[512];
                var reading = stream.ReadAsync( answer, 0, answer.Length );
                // Refused means an HTTP error status, or the connection closed or reset; never a 101 Switching Protocols.
                bool ended = Drive( relay, reading );
                string status = reading.Status == TaskStatus.RanToCompletion ? Encoding.ASCII.GetString( answer, 0, reading.Result ) : "";
                assert( ended && !status.StartsWith( "HTTP/1.1 101" ), "embrelay_malformed_upgrade_refused" );
            }
            using ( var raw = new TcpClient() )
            {
                Drive( relay, raw.ConnectAsync( IPAddress.Loopback, relay.Port ) );
                raw.GetStream().Write( Encoding.ASCII.GetBytes( "\u0016\u0003\u0001 not http at all\r\n\r\n" ) );
                PumpFor( relay, 50 );
            }
            assert( Get( "/health" )?.StatusCode == HttpStatusCode.OK, "embrelay_serves_after_malformed_requests" );

            NonLoopback( relay, assert );
        }
    }

    /// <summary>
    /// The listener's "localhost" prefix filters on the Host header only, so a request sent to this machine's network address WITH "Host: localhost" is the
    /// real test of loopback-only serving. It must not reach the relay: refused at connect, or answered with anything but the health body.
    /// </summary>
    private static void NonLoopback( EmbeddedRelay relay, Action<bool, string> assert )
    {
        IPAddress external = NetworkInterface.GetAllNetworkInterfaces()
            .Where( n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback )
            .SelectMany( n => n.GetIPProperties().UnicastAddresses )
            .Select( a => a.Address )
            .Where( a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback( a ) )
            // A routable address is the meaningful case; a link-local (169.254/16) one is used only when there is nothing else.
            .OrderBy( a => a.GetAddressBytes()[0] == 169 && a.GetAddressBytes()[1] == 254 ? 1 : 0 )
            .FirstOrDefault();
        if ( external == null )
        {
            Console.WriteLine( "embrelay: this machine has no non-loopback IPv4 address; only the prefix string was asserted for loopback-only serving." );
            return;
        }
        using var tcp = new TcpClient();
        var connecting = tcp.ConnectAsync( external, relay.Port );
        bool connected = Drive( relay, connecting ) && connecting.Status == TaskStatus.RanToCompletion;
        string answer = "";
        if ( connected )
        {
            var stream = tcp.GetStream();
            stream.Write( Encoding.ASCII.GetBytes( "GET /health HTTP/1.1\r\nHost: localhost:" + relay.Port + "\r\nConnection: close\r\n\r\n" ) );
            var buffer = new byte[1024];
            var reading = stream.ReadAsync( buffer, 0, buffer.Length );
            if ( Drive( relay, reading ) && reading.Status == TaskStatus.RanToCompletion )
                answer = Encoding.ASCII.GetString( buffer, 0, reading.Result );
        }
        Console.WriteLine( "embrelay: a request to " + external + " was " + ( connected ? "answered \"" + answer.Split( '\r' )[0] + "\"" : "refused at connect" ) + "." );
        assert( !answer.Contains( "collab-relay" ) && !answer.StartsWith( "HTTP/1.1 200" ), "embrelay_non_loopback_request_refused" );
    }

    // ---- forwarding, both directions, byte for byte; frame cap; text frames; departures --------------------------------------

    private static void Forwarding( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        using ( relay )
        {
            byte[] secret = Secret( 1 );
            using var host = Host( relay, secret, assert, "embrelay_host_hello_ok" );
            var ( guest, channel ) = Guest( relay, host, secret, assert, "embrelay_guest_hello_ok_and_open" );
            using ( guest )
            {
                // A guest message of exactly MaxFrameBytes is forwarded (the host receives it under a 5-byte header); a host frame of exactly MaxFrameBytes,
                // header included, delivers its 65531-byte payload.
                foreach ( int length in new[] { 16 * 1024, RelayLimits.MaxFrameBytes } )
                {
                    byte[] up = Pattern( length, 3 );
                    Send( relay, guest, up );
                    assert( IsFrame( Next( relay, host ), RelayFrameType.Data, channel, up ), "embrelay_guest_to_host_" + length + "_unchanged" );
                    byte[] down = Pattern( length - RelayProtocol.HeaderBytes, 5 );
                    Send( relay, host, RelayProtocol.BuildFrame( RelayFrameType.Data, channel, down ) );
                    var got = Next( relay, guest );
                    assert( got != null && got.Type == WebSocketMessageType.Binary && got.Data.SequenceEqual( down ), "embrelay_host_to_guest_" + length + "_unchanged" );
                }
                assert( relay.Stats.FramesForwarded == 4 && relay.Stats.BytesForwarded == 16384 * 2 - 5 + 65536 * 2 - 5, "embrelay_stats_count_forwarded" );

                // One byte over the cap closes the sender with 1009, and the host is told that channel closed.
                Send( relay, guest, new byte[RelayLimits.MaxFrameBytes + 1] );
                assert( ClosedSoon( relay, guest, out var tooBig ) && tooBig.Status == WebSocketCloseStatus.MessageTooBig, "embrelay_frame_cap_closes_sender" );
                assert( IsFrame( Next( relay, host ), RelayFrameType.Close, channel ), "embrelay_frame_cap_host_told" );
            }

            // A text frame after the hello closes that socket (the worker's 1003 "binary only").
            var ( texter, textChannel ) = Guest( relay, host, secret, assert, "embrelay_second_guest_joins" );
            using ( texter )
            {
                assert( textChannel == channel + 1, "embrelay_channels_increment" );
                SendText( relay, texter, "plain text" );
                assert( ClosedSoon( relay, texter, out var textClose ) && textClose.Status == WebSocketCloseStatus.InvalidMessageType, "embrelay_text_frame_closes_socket" );
                assert( IsFrame( Next( relay, host ), RelayFrameType.Close, textChannel ), "embrelay_text_frame_host_told" );
            }

            // A guest that leaves cleanly: the host receives Close for its channel.
            var ( leaver, leaverChannel ) = Guest( relay, host, secret, assert, "embrelay_third_guest_joins" );
            Drive( relay, leaver.CloseOutputAsync( WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None ) );
            assert( IsFrame( Next( relay, host ), RelayFrameType.Close, leaverChannel ), "embrelay_guest_leaves_host_told" );
            leaver.Dispose();

            // The host closes a channel: that guest is closed, and the host is not sent a Close back for it.
            var ( closed, closedChannel ) = Guest( relay, host, secret, assert, "embrelay_fourth_guest_joins" );
            using ( closed )
            {
                Send( relay, host, RelayProtocol.BuildFrame( RelayFrameType.Close, closedChannel, ReadOnlySpan<byte>.Empty ) );
                assert( ClosedSoon( relay, closed, out var byHost ) && byHost.Status == WebSocketCloseStatus.NormalClosure, "embrelay_host_closes_channel" );
                var echo = ReceiveAsync( host );
                PumpFor( relay, 150 );
                assert( !echo.IsCompleted, "embrelay_host_close_not_echoed" );

                // The host's own oversized frame closes the host, which ends the room.
                Send( relay, host, new byte[RelayLimits.MaxFrameBytes + 1] );
                var hostClose = Await( relay, echo );
                if ( hostClose != null && !hostClose.IsClose )
                    hostClose = Next( relay, host );
                assert( hostClose != null && hostClose.Status == WebSocketCloseStatus.MessageTooBig && relay.Stats.Rooms == 0, "embrelay_frame_cap_closes_host" );
            }
        }
    }

    // ---- hello refusals and the member cap -------------------------------------------------------------------------------------

    private static void Membership( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        using ( relay )
        {
            byte[] secret = Secret( 2 );
            string room = Room( secret );
            var ( lonely, lonelyReply ) = Hello( relay, room, RelayProtocol.BuildGuestHello( secret ) );
            bool lonelyClosed = ClosedSoon( relay, lonely, out var noHostClose );
            assert( ErrorCode( lonelyReply ) == "no-host" && lonelyClosed && noHostClose.Status == WebSocketCloseStatus.PolicyViolation, "embrelay_guest_before_host_no_host" );
            lonely.Dispose();

            using var host = Host( relay, secret, assert, "embrelay_member_host_ok" );
            var guests = new List<ClientWebSocket>();
            for ( int i = 0; i < RelayLimits.MaxMembersPerRoom - 1; i++ )
                guests.Add( Guest( relay, host, secret, assert, "embrelay_member_" + ( i + 2 ) + "_admitted" ).Socket );
            assert( relay.Stats.Members == RelayLimits.MaxMembersPerRoom, "embrelay_stats_members" );

            var ( fifth, fifthReply ) = Hello( relay, room, RelayProtocol.BuildGuestHello( secret ) );
            assert( ErrorCode( fifthReply ) == "full" && ClosedSoon( relay, fifth, out var fullClose ) && fullClose.Status == WebSocketCloseStatus.PolicyViolation, "embrelay_member_cap_fifth_refused" );
            fifth.Dispose();
            assert( relay.Stats.Members == RelayLimits.MaxMembersPerRoom, "embrelay_member_cap_count_unchanged" );

            // A seat freed by a departure can be taken again.
            Drive( relay, guests[0].CloseOutputAsync( WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None ) );
            Next( relay, host );
            guests.Add( Guest( relay, host, secret, assert, "embrelay_member_seat_reused" ).Socket );

            string wrongProof = JsonSerializer.Serialize( new RelayControl { Type = RelayProtocol.GuestHello, Version = 1, Room = room, Proof = RelayProtocol.Hex( RelayProtocol.DeriveGuestProof( Secret( 99 ) ) ) } );
            var ( wrong, wrongReply ) = Hello( relay, room, wrongProof );
            assert( ErrorCode( wrongReply ) == "bad-proof" && ClosedSoon( relay, wrong, out _ ), "embrelay_wrong_proof_refused" );
            wrong.Dispose();

            string impostor = JsonSerializer.Serialize( new RelayControl
            {
                Type = RelayProtocol.HostHello, Version = 1, Room = room,
                HostProof = RelayProtocol.Hex( RelayProtocol.DeriveHostProof( Secret( 98 ) ) ), GuestProof = RelayProtocol.Hex( RelayProtocol.DeriveGuestProof( Secret( 98 ) ) )
            } );
            var ( taken, takenReply ) = Hello( relay, room, impostor );
            assert( ErrorCode( takenReply ) == "room-taken", "embrelay_impostor_host_room_taken" );
            taken.Dispose();

            byte[] other = Secret( 3 );
            var binary = Connect( relay, Room( other ) );
            Send( relay, binary, new byte[] { 1, 2, 3 } );
            assert( ErrorCode( Next( relay, binary ) ) == "bad-hello", "embrelay_binary_first_message_refused" );
            binary.Dispose();
            var ( junk, junkReply ) = Hello( relay, Room( other ), "this is not json" );
            assert( ErrorCode( junkReply ) == "bad-hello", "embrelay_non_json_hello_refused" );
            junk.Dispose();
            var ( mismatch, mismatchReply ) = Hello( relay, Room( other ), RelayProtocol.BuildGuestHello( Secret( 4 ) ) );
            assert( ErrorCode( mismatchReply ) == "bad-hello", "embrelay_hello_room_must_match_address" );
            mismatch.Dispose();
            string versioned = JsonSerializer.Serialize( new RelayControl { Type = RelayProtocol.GuestHello, Version = 99, Room = Room( other ), Proof = RelayProtocol.Hex( RelayProtocol.DeriveGuestProof( other ) ) } );
            var ( version, versionReply ) = Hello( relay, Room( other ), versioned );
            assert( ErrorCode( versionReply ) == "version", "embrelay_other_version_refused" );
            version.Dispose();
            var ( oversized, oversizedReply ) = Hello( relay, Room( other ), new string( 'a', 9000 ) );
            assert( ErrorCode( oversizedReply ) == "bad-hello", "embrelay_oversized_hello_refused" );
            oversized.Dispose();
            foreach ( var guest in guests )
                guest.Dispose();
        }
    }

    // ---- proof deadline and the pending-socket cap -------------------------------------------------------------------------------

    private static void ProofDeadline( Action<bool, string> assert )
    {
        var ( relay, clock ) = Start();
        using ( relay )
        {
            using var silent = Connect( relay, Room( Secret( 5 ) ) );
            assert( relay.Stats.PendingSockets == 1, "embrelay_pending_counted" );
            var first = ReceiveAsync( silent );
            clock.Now += RelayLimits.ProofDeadlineSeconds - 0.5;
            PumpFor( relay, 150 );
            assert( !first.IsCompleted, "embrelay_proof_deadline_not_early" );
            clock.Now += 0.5;
            var refusal = Await( relay, first );
            assert( ErrorCode( refusal ) == "bad-hello" && ClosedSoon( relay, silent, out var close ) && close.Status == WebSocketCloseStatus.PolicyViolation, "embrelay_proof_deadline_closes" );
            PumpFor( relay, 50 );
            assert( relay.Stats.PendingSockets == 0, "embrelay_proof_deadline_pending_released" );

            // A proven socket is never expired by the deadline.
            byte[] secret = Secret( 6 );
            using var host = Host( relay, secret, assert, "embrelay_proof_host_ok" );
            clock.Now += RelayLimits.ProofDeadlineSeconds * 3;
            var quiet = ReceiveAsync( host );
            PumpFor( relay, 150 );
            assert( !quiet.IsCompleted && relay.Stats.Rooms == 1, "embrelay_proven_socket_not_expired" );
        }
    }

    private static void PendingCap( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        using ( relay )
        {
            var sockets = new List<ClientWebSocket>();
            for ( int i = 0; i < EmbeddedRelay.MaxPendingSockets; i++ )
                sockets.Add( Connect( relay, Room( Secret( (byte)( 10 + i ) ) ) ) );
            assert( relay.Stats.PendingSockets == EmbeddedRelay.MaxPendingSockets, "embrelay_pending_cap_filled" );
            var extra = NewClient();
            var connecting = extra.ConnectAsync( new Uri( "ws://localhost:" + relay.Port + "/relay?room=" + Room( Secret( 200 ) ) ), CancellationToken.None );
            assert( Drive( relay, connecting ) && connecting.IsFaulted && extra.HttpStatusCode == HttpStatusCode.ServiceUnavailable, "embrelay_pending_cap_refuses_upgrade" );
            extra.Dispose();
            foreach ( var socket in sockets )
                socket.Dispose();
        }
    }

    // ---- rate limit: a burst over 200/s is held back and counted, never dropped ----------------------------------------------

    private static void RateLimit( Action<bool, string> assert )
    {
        var ( relay, clock ) = Start();
        using ( relay )
        {
            byte[] secret = Secret( 7 );
            using var host = Host( relay, secret, assert, "embrelay_rate_host_ok" );
            var ( guest, channel ) = Guest( relay, host, secret, assert, "embrelay_rate_guest_ok" );
            using ( guest )
            {
                int burst = RelayLimits.MaxMessagesPerSecond + 10;
                long before = relay.Stats.FramesForwarded;
                // The clock is frozen, so every frame falls in one window.
                for ( int i = 0; i < burst; i++ )
                    Send( relay, guest, new[] { (byte)( i & 0xFF ), (byte)( i >> 8 ) } );
                PumpFor( relay, 100 );
                int received = 0;
                bool inOrder = true;
                for ( int i = 0; i < RelayLimits.MaxMessagesPerSecond; i++ )
                {
                    var msg = Next( relay, host );
                    inOrder &= IsFrame( msg, RelayFrameType.Data, channel, new[] { (byte)( i & 0xFF ), (byte)( i >> 8 ) } );
                    received++;
                }
                var overflow = ReceiveAsync( host );
                PumpFor( relay, 150 );
                assert( received == RelayLimits.MaxMessagesPerSecond && inOrder && !overflow.IsCompleted
                    && relay.Stats.FramesForwarded - before == RelayLimits.MaxMessagesPerSecond, "embrelay_rate_burst_held_at_limit" );
                assert( relay.Stats.FramesThrottled == 1, "embrelay_rate_burst_counted" );

                clock.Now += 1;
                var resumed = Await( relay, overflow );
                bool rest = IsFrame( resumed, RelayFrameType.Data, channel, new[] { (byte)200, (byte)0 } );
                for ( int i = RelayLimits.MaxMessagesPerSecond + 1; i < burst; i++ )
                    rest &= IsFrame( Next( relay, host ), RelayFrameType.Data, channel, new[] { (byte)( i & 0xFF ), (byte)( i >> 8 ) } );
                assert( rest && relay.Stats.FramesForwarded - before == burst, "embrelay_rate_nothing_lost_in_order" );
            }
        }
    }

    // ---- a reader that never reads is closed once its queue passes the cap ---------------------------------------------------------

    private static void SlowReader( Action<bool, string> assert )
    {
        var ( relay, clock ) = Start();
        using ( relay )
        {
            byte[] secret = Secret( 8 );
            using var host = Host( relay, secret, assert, "embrelay_slow_host_ok" );
            var ( guest, channel ) = Guest( relay, host, secret, assert, "embrelay_slow_guest_ok" );
            using ( guest )
            {
                byte[] frame = RelayProtocol.BuildFrame( RelayFrameType.Data, channel, Pattern( RelayLimits.MaxFrameBytes - RelayProtocol.HeaderBytes, 9 ) );
                var told = ReceiveAsync( host );
                var timer = Stopwatch.StartNew();
                int sent = 0;
                // Bounded by bytes (64 MiB) as well as by the deadline, so a machine with enormous socket buffers fails the assertion instead of looping.
                while ( !told.IsCompleted && sent < 1024 && timer.Elapsed < Deadline )
                {
                    if ( !Send( relay, host, frame ) )
                        break;
                    if ( ++sent % 100 == 0 )
                        clock.Now += 1;
                }
                Drive( relay, told );
                assert( told.IsCompleted && IsFrame( told.Result, RelayFrameType.Close, channel ), "embrelay_slow_reader_closed_at_queue_cap" );
                assert( relay.Stats.Rooms == 1 && relay.Stats.Members == 1, "embrelay_slow_reader_host_unaffected" );
            }
        }
    }

    // ---- host departure and a returning host --------------------------------------------------------------------------------------

    private static void HostDeparture( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        using ( relay )
        {
            byte[] secret = Secret( 9 );
            var host = Host( relay, secret, assert, "embrelay_departure_host_ok" );
            var ( first, firstChannel ) = Guest( relay, host, secret, assert, "embrelay_departure_guest_ok" );

            // The same host (same proofs) reconnecting replaces its old socket and closes the old socket's guests.
            using var returning = Host( relay, secret, assert, "embrelay_returning_host_ok" );
            assert( ClosedSoon( relay, host, out var replaced ) && replaced.Status == WebSocketCloseStatus.NormalClosure, "embrelay_returning_host_replaces_old_socket" );
            assert( ClosedSoon( relay, first, out var restarted ) && restarted.Status == WebSocketCloseStatus.EndpointUnavailable, "embrelay_returning_host_closes_old_guests" );
            host.Dispose();
            first.Dispose();
            var ( second, secondChannel ) = Guest( relay, returning, secret, assert, "embrelay_returning_host_accepts_guests" );
            assert( secondChannel == firstChannel + 1 && relay.Stats.Rooms == 1 && relay.Stats.Members == 2, "embrelay_returning_host_keeps_channel_sequence" );
            var ( third, _ ) = Guest( relay, returning, secret, assert, "embrelay_departure_third_ok" );

            // The host drops without a close handshake: every guest is closed and the room is gone.
            returning.Abort();
            bool secondClosed = ClosedSoon( relay, second, out var secondClose );
            bool thirdClosed = ClosedSoon( relay, third, out var thirdClose );
            assert( secondClosed && thirdClosed && secondClose.Status == WebSocketCloseStatus.EndpointUnavailable && thirdClose.Status == WebSocketCloseStatus.EndpointUnavailable, "embrelay_host_leaves_guests_closed" );
            PumpFor( relay, 50 );
            assert( relay.Stats.Rooms == 0 && relay.Stats.Members == 0, "embrelay_host_leaves_room_gone" );
            var ( late, lateReply ) = Hello( relay, Room( secret ), RelayProtocol.BuildGuestHello( secret ) );
            assert( ErrorCode( lateReply ) == "no-host", "embrelay_host_leaves_later_guest_no_host" );
            late.Dispose();
            second.Dispose();
            third.Dispose();
        }
    }

    private static void RoomsIsolated( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        using ( relay )
        {
            byte[] a = Secret( 11 ), b = Secret( 12 );
            using var hostA = Host( relay, a, assert, "embrelay_rooms_host_a_ok" );
            using var hostB = Host( relay, b, assert, "embrelay_rooms_host_b_ok" );
            var ( guestA, channelA ) = Guest( relay, hostA, a, assert, "embrelay_rooms_guest_a_ok" );
            var ( guestB, channelB ) = Guest( relay, hostB, b, assert, "embrelay_rooms_guest_b_ok" );
            using ( guestA )
            using ( guestB )
            {
                assert( channelA == 1 && channelB == 1 && relay.Stats.Rooms == 2, "embrelay_rooms_have_own_channels" );
                Send( relay, guestB, new byte[] { 0xB1 } );
                assert( IsFrame( Next( relay, hostB ), RelayFrameType.Data, channelB, new byte[] { 0xB1 } ), "embrelay_rooms_b_delivers" );
                Send( relay, guestA, new byte[] { 0xA1 } );
                assert( IsFrame( Next( relay, hostA ), RelayFrameType.Data, channelA, new byte[] { 0xA1 } ), "embrelay_rooms_isolated" );
                // The host of room B names channel 1, which in room A is guest A: it must reach only room B's guest.
                Send( relay, hostB, RelayProtocol.BuildFrame( RelayFrameType.Data, 1, new byte[] { 0xB2 } ) );
                var atB = Next( relay, guestB );
                var atA = ReceiveAsync( guestA );
                PumpFor( relay, 100 );
                assert( atB != null && atB.Data.SequenceEqual( new byte[] { 0xB2 } ) && !atA.IsCompleted, "embrelay_rooms_channel_numbers_do_not_cross" );
            }
        }
    }

    private static void DisposeAndPort( Action<bool, string> assert )
    {
        var ( relay, _ ) = Start();
        byte[] secret = Secret( 13 );
        using var host = Host( relay, secret, assert, "embrelay_dispose_host_ok" );
        var ( guest, _ ) = Guest( relay, host, secret, assert, "embrelay_dispose_guest_ok" );
        using var pending = Connect( relay, Room( Secret( 14 ) ) );
        var hostEnd = ReceiveAsync( host );
        var timer = Stopwatch.StartNew();
        relay.Dispose();
        assert( timer.Elapsed < TimeSpan.FromSeconds( 2 ), "embrelay_dispose_returns_promptly" );
        assert( hostEnd.Wait( Deadline ) && hostEnd.Result.IsClose, "embrelay_dispose_closes_live_sockets" );
        assert( !relay.IsListening, "embrelay_dispose_stops_listener" );
        relay.Pump();
        guest.Dispose();

        var occupied = new TcpListener( IPAddress.Loopback, 0 );
        occupied.Start();
        int port = ( (IPEndPoint)occupied.LocalEndpoint ).Port;
        string message = null;
        using ( var collision = new EmbeddedRelay( port ) )
        {
            try
            {
                collision.Start();
            }
            catch ( InvalidOperationException ex )
            {
                message = ex.Message;
            }
        }
        occupied.Stop();
        assert( message == "The local relay could not listen on localhost port " + port + ". The port may already be in use by another program; close that program or choose a different port.", "embrelay_port_in_use_formal_exception" );
    }

    // ---- the library's own relay clients, end to end ---------------------------------------------------------------------------

    private static void LibraryClients( Action<bool, string> assert )
    {
        using var relay = new EmbeddedRelay( FreePort() );
        relay.Start();
        byte[] secret = Secret( 17 );
        byte[] key = InviteCode.DeriveKey( secret, "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f" );
        string url = "ws://localhost:" + relay.Port + "/relay";
        var sockets = new List<PolledSocket>();
        PolledSocket Make()
        {
            var socket = new PolledSocket();
            lock ( sockets )
                sockets.Add( socket );
            return socket;
        }
        bool Wait( Func<bool> condition )
        {
            var timer = Stopwatch.StartNew();
            bool met;
            // The condition's result is kept: a TryDequeue that succeeds must not be called a second time to report it.
            while ( !( met = condition() ) && timer.Elapsed < Deadline )
            {
                relay.Pump();
                PolledSocket[] snapshot;
                lock ( sockets )
                    snapshot = sockets.ToArray();
                foreach ( var socket in snapshot )
                    socket.Pump();
                Thread.Sleep( 1 );
            }
            return met;
        }
        using var host = new RelayHost( Make, url, secret, key, null, anyLoopbackPort: true );
        host.Start();
        assert( Wait( () => host.State == RelayHostState.Ready ), "embrelay_library_host_ready" );
        Task<Wire> connecting = RelayGuest.ConnectAsync( Make, url, secret, key, anyLoopbackPort: true );
        assert( Wait( () => connecting.IsCompleted ) && connecting.Status == TaskStatus.RanToCompletion, "embrelay_library_guest_connected" );
        using var guest = connecting.Result;
        Wire accepted = null;
        assert( Wait( () => host.Accepted.TryDequeue( out accepted ) ), "embrelay_library_host_accepted" );
        using ( accepted )
        {
            guest.Send( new Packet { Kind = "hello", Peer = "library-guest", Name = "sealed-session-bytes" } );
            Packet arrived = null;
            assert( Wait( () => accepted.Incoming.TryDequeue( out arrived ) ) && arrived.Kind == "hello" && arrived.Name == "sealed-session-bytes", "embrelay_library_guest_hello_crosses" );
            accepted.Send( new Packet { Kind = "welcome", Session = "library-host" } );
            Packet returned = null;
            assert( Wait( () => guest.Incoming.TryDequeue( out returned ) ) && returned.Kind == "welcome" && returned.Session == "library-host", "embrelay_library_host_welcome_crosses" );
            // A multi-chunk packet (about 200 KiB, thirteen 16 KiB relay chunks) arrives whole through the relay.
            string big = new string( 'q', 200 * 1024 );
            accepted.Send( new Packet { Kind = "snapshot", Error = big } );
            Packet snapshot = null;
            assert( Wait( () => guest.Incoming.TryDequeue( out snapshot ) ) && snapshot.Error == big, "embrelay_library_chunked_packet_crosses" );
        }
    }

    /// <summary>
    /// The relay socket seam over ClientWebSocket, read by polling from the test loop rather than by a background receive loop, so every library callback
    /// (and so every relay frame) is driven from the same thread that pumps the relay. Test-only: the editor's adapter is SandboxRelaySocket.
    /// </summary>
    private sealed class PolledSocket : IRelaySocket
    {
        private readonly ClientWebSocket socket = NewClient();

        private readonly byte[] buffer = new byte[RelayLimits.MaxFrameBytes + 1024];

        private readonly SemaphoreSlim sending = new( 1, 1 );

        private Task<WebSocketReceiveResult> read;

        private int used, closedRaised;

        public bool IsConnected => socket.State == WebSocketState.Open;

        public event Action<string> TextReceived;

        public event Action<byte[]> BinaryReceived;

        public event Action<int, string> Closed;

        public async Task ConnectAsync( string url, CancellationToken token )
        {
            await socket.ConnectAsync( new Uri( url ), token );
            Volatile.Write( ref read, socket.ReceiveAsync( new ArraySegment<byte>( buffer ), CancellationToken.None ) );
        }

        public Task SendTextAsync( string text, CancellationToken token ) => Send( Encoding.UTF8.GetBytes( text ), WebSocketMessageType.Text, token );

        public Task SendBinaryAsync( ReadOnlyMemory<byte> data, CancellationToken token ) => Send( data.ToArray(), WebSocketMessageType.Binary, token );

        private async Task Send( byte[] data, WebSocketMessageType type, CancellationToken token )
        {
            await sending.WaitAsync( token );
            try
            {
                await socket.SendAsync( new ArraySegment<byte>( data ), type, true, token );
            }
            finally
            {
                sending.Release();
            }
        }

        public void Pump()
        {
            for ( int n = 0; n < 64; n++ )
            {
                var pending = Volatile.Read( ref read );
                if ( pending == null || !pending.IsCompleted )
                    return;
                read = null;
                if ( pending.Status != TaskStatus.RanToCompletion )
                {
                    RaiseClosed( 1006, "socket lost" );
                    return;
                }
                var result = pending.Result;
                if ( result.MessageType == WebSocketMessageType.Close )
                {
                    RaiseClosed( (int?)result.CloseStatus ?? 1000, result.CloseStatusDescription );
                    return;
                }
                used += result.Count;
                if ( result.EndOfMessage )
                {
                    byte[] data = buffer.AsSpan( 0, used ).ToArray();
                    used = 0;
                    if ( result.MessageType == WebSocketMessageType.Text )
                        TextReceived?.Invoke( Encoding.UTF8.GetString( data ) );
                    else
                        BinaryReceived?.Invoke( data );
                }
                try
                {
                    read = socket.ReceiveAsync( new ArraySegment<byte>( buffer, used, buffer.Length - used ), CancellationToken.None );
                }
                catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException )
                {
                    RaiseClosed( 1006, "socket lost" );
                    return;
                }
            }
        }

        private void RaiseClosed( int status, string reason )
        {
            if ( Interlocked.Exchange( ref closedRaised, 1 ) == 0 )
                Closed?.Invoke( status, reason );
        }

        public void Dispose()
        {
            socket.Dispose();
            RaiseClosed( 1000, "disposed" );
        }
    }
}
