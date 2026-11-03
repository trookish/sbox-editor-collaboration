using System;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

/// <summary>
/// What the embedded relay has done since it started. <see cref="FramesRefused"/> counts frames and hellos the relay refused (oversized, malformed,
/// a wrong proof, a full room, an upgrade past the pending cap); <see cref="FramesThrottled"/> counts frames the room's rate limit held back until the
/// window had room again.
/// </summary>
public sealed record RelayStats( int Rooms, int Members, int PendingSockets, long FramesForwarded, long FramesRefused, long FramesThrottled, long BytesForwarded );

/// <summary>
/// The relay of <c>Tools/RelayWorker</c>, run inside the host's own editor so that nobody has to sign in anywhere: a Cloudflare Quick Tunnel exposes this
/// loopback listener, and the library's unchanged <see cref="RelayHost"/> and <see cref="RelayGuest"/> dial it. The wire contract is the worker's: the room
/// rides in <c>/relay?room=&lt;32 hex&gt;</c>, the first message is a text hello, the host's socket carries <c>[type][channel u32 BE][payload]</c> frames and a
/// guest's socket carries raw stream bytes. Like the worker it forwards opaque AES-GCM bytes and holds no key; it never logs or keeps a payload.
///
/// Threading. Nothing here runs off the caller's thread. The listener's accept, each WebSocket upgrade, receive, send and close are framework tasks that
/// are stored and checked with <see cref="Task.IsCompleted"/> from <see cref="Pump"/>, which the editor calls once per frame and the tests call in a loop.
/// The framework completes its IO on its own threads, but no continuation, callback or thread belonging to this (hotloaded) assembly ever runs there,
/// so a hotload can never find relay code mid-flight on a worker thread, room state needs no lock, and nothing has to be joined on <see cref="Dispose"/>.
/// A result is read with GetAwaiter().GetResult() only after the task has completed successfully, so it never blocks; there is no Task.Run, Wait or Result.
/// The price is latency: a frame can wait up to one editor frame between arriving and being sent on. <see cref="Pump"/> keeps it to one frame by
/// draining every completed receive and starting the onward send in the same call.
/// </summary>
public sealed class EmbeddedRelay : IDisposable
{
    /// <summary>
    /// Bytes waiting to be sent to one socket. Past this the reader is not keeping up, and the socket is closed rather than trimmed: every frame is a
    /// numbered piece of an AES-GCM stream, so dropping the oldest would end that session anyway, only later and less clearly.
    /// </summary>
    public const int MaxQueuedBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Sockets that have connected but not yet proved they hold an invite. Each costs a receive buffer, and anyone who learns the tunnel address can open
    /// them, so past this an upgrade is answered 503 instead of accepted. Proven members are bounded separately by <see cref="RelayLimits.MaxMembersPerRoom"/>.
    /// </summary>
    public const int MaxPendingSockets = 16;

    /// <summary>
    /// Rooms open at once. A relay the host runs for itself serves that host's session, so one room is normal; a few more cover a session restarted with
    /// a new room before the old socket has closed. Past this a host hello is refused with "limit".
    /// </summary>
    public const int MaxRooms = 4;

    // Headers cloudflared adds to every request it carries to the origin. A request with any of them came through the public tunnel, not from the host's
    // own editor, which dials localhost directly.
    private static readonly string[] TunnelHeaders = { "Cf-Connecting-Ip", "Cf-Ray", "X-Forwarded-For" };

    // A refusal or a goodbye is sent, then the close handshake is given this long before the socket is simply aborted.
    private const long CloseGraceMilliseconds = 2000;

    // Bounds the work one Pump does for one busy socket, so a flood on one socket cannot stall the editor frame or starve the other sockets.
    private const int MaxMessagesPerPump = 256;

    private const int MaxAcceptsPerPump = 16;

    // The worker's health answer, byte for byte (Response.json of { status, service, protocol }), so a health check cannot tell the two relays apart.
    private static readonly byte[] HealthBody = Encoding.UTF8.GetBytes( "{\"status\":\"ok\",\"service\":\"collab-relay\",\"protocol\":" + RelayProtocol.ProtocolVersion + "}" );

    private static readonly UTF8Encoding StrictUtf8 = new( false, true );

    private readonly HttpListener listener = new();

    private readonly Func<double> seconds;

    private readonly Dictionary<string, Room> rooms = new( StringComparer.Ordinal );

    private readonly List<Peer> peers = new();

    private readonly List<Upgrade> upgrades = new();

    private Task<HttpListenerContext> accept;

    private bool disposed;

    private long forwarded, refused, throttled, bytesForwarded;

    private sealed class Room
    {
        public Peer Host;

        public string HostProof, GuestProof;

        public uint Next;

        public readonly Dictionary<uint, Peer> Guests = new();

        // One window for the whole room, all members together, as RelayLimits specifies: it bounds what a session can make the host's uplink carry.
        public readonly RateWindow Rate = new( RelayLimits.MaxMessagesPerSecond );
    }

    private sealed class Upgrade
    {
        public Task<HttpListenerWebSocketContext> Task;

        public HttpListenerContext Context;

        public string RoomId;

        public bool ViaTunnel;
    }

    private sealed class Peer
    {
        public WebSocket Socket;

        public string RoomId;

        public bool ViaTunnel;

        public Room Room;

        public bool Host, Proven, Departed, Closing, Done, ClosedByHost;

        public uint Channel;

        public double ConnectedAt;

        public long ClosingSince;

        // One byte more than the largest frame, so a message that fills it without ending is known to be too large without reading any further.
        public readonly byte[] Buffer = new byte[RelayLimits.MaxFrameBytes + 1];

        public int Used, QueuedBytes;

        // A complete frame the room's rate limit would not let through yet. While it is held the socket is not read, so TCP flow control slows the sender
        // instead of the relay dropping a piece of an encrypted stream.
        public byte[] Held;

        public Task<WebSocketReceiveResult> Receiving;

        public Task Sending, ClosingTask;

        public readonly Queue<(byte[] Bytes, WebSocketMessageType Type)> Outbound = new();

        public WebSocketCloseStatus CloseCode;

        public string CloseReason;
    }

    /// <param name="port">The loopback port to listen on. The host editor dials its own relay through the engine, which allows plain <c>ws://</c> to loopback
    /// only on ports 80, 443, 8080 and 8443, so production uses one of those; tests use any free port.</param>
    /// <param name="seconds">A monotonic clock in seconds, read for the proof deadline and the rate window. Tests inject one they control.</param>
    public EmbeddedRelay( int port, Func<double> seconds = null )
    {
        if ( port < 1 || port > 65535 )
            throw new ArgumentOutOfRangeException( nameof( port ), port, "The relay port must be between 1 and 65535." );
        Port = port;
        // Loopback by name, never a wildcard ("+" or "*"): the tunnel reaches the relay from this same computer, nothing else should, and a wildcard
        // prefix would also need an administrator's URL reservation.
        Prefix = "http://localhost:" + port + "/";
        listener.Prefixes.Add( Prefix );
        listener.IgnoreWriteExceptions = true;
        this.seconds = seconds ?? ( () => Environment.TickCount64 / 1000.0 );
    }

    public int Port { get; }

    /// <summary>The single prefix the listener registers.</summary>
    public string Prefix { get; }

    public bool IsListening => !disposed && listener.IsListening;

    public RelayStats Stats
    {
        get
        {
            int members = 0;
            foreach ( var room in rooms.Values )
                members += 1 + room.Guests.Count;
            return new RelayStats( rooms.Count, members, PendingCount(), forwarded, refused, throttled, bytesForwarded );
        }
    }

    /// <summary>Starts listening. Throws <see cref="InvalidOperationException"/> with a message the dock can show when the port cannot be taken.</summary>
    public void Start()
    {
        if ( disposed )
            throw new ObjectDisposedException( nameof( EmbeddedRelay ) );
        if ( listener.IsListening )
            return;
        try
        {
            listener.Start();
            accept = listener.GetContextAsync();
        }
        catch ( Exception ex ) when ( ex is HttpListenerException || ex is InvalidOperationException || ex is ObjectDisposedException )
        {
            throw new InvalidOperationException( "The local relay could not listen on localhost port " + Port
                + ". The port may already be in use by another program; close that program or choose a different port.", ex );
        }
    }

    /// <summary>Does every piece of work that is ready: new requests, finished upgrades, received frames, sends, closes, expired hellos. Never blocks.</summary>
    public void Pump()
    {
        if ( disposed || !listener.IsListening )
            return;
        AcceptRequests();
        FinishUpgrades();
        double now = seconds();
        // Indexed rather than foreach: handling one socket can end others (a host leaving closes its guests), but sockets are only added in FinishUpgrades.
        for ( int i = 0; i < peers.Count; i++ )
            Service( peers[i], now );
        for ( int i = peers.Count - 1; i >= 0; i-- )
        {
            if ( !peers[i].Done )
                continue;
            Discard( peers[i] );
            peers.RemoveAt( i );
        }
    }

    // ---- HTTP ----------------------------------------------------------------------------------------------------------

    private void AcceptRequests()
    {
        for ( int n = 0; n < MaxAcceptsPerPump && accept != null && accept.IsCompleted; n++ )
        {
            HttpListenerContext context = null;
            if ( accept.Status == TaskStatus.RanToCompletion )
                context = accept.GetAwaiter().GetResult();
            // A failed accept (a client that vanished mid-request) must not stop the listener, and the next accept is posted before this request is
            // handled, so a slow request never delays listening.
            try
            {
                accept = listener.IsListening ? listener.GetContextAsync() : null;
            }
            catch ( Exception ex ) when ( ex is HttpListenerException || ex is ObjectDisposedException || ex is InvalidOperationException )
            {
                accept = null;
            }
            if ( context != null )
                Handle( context );
        }
    }

    private void Handle( HttpListenerContext context )
    {
        try
        {
            // The prefix makes the system accept only requests addressed to "localhost", but that is a check of the Host header, which any machine that can
            // reach this port can write. The tunnel connects from this computer, so a request from any other address is refused before anything else.
            var remote = context.Request.RemoteEndPoint;
            if ( remote == null || !IPAddress.IsLoopback( remote.Address ) )
            {
                Reply( context, 404 );
                return;
            }
            string raw = context.Request.RawUrl ?? "";
            int query = raw.IndexOf( '?' );
            string path = query < 0 ? raw : raw.Substring( 0, query );
            // The file route is on the allow-list for a later task; until it exists it is answered exactly like any other unknown path.
            if ( !RelayLimits.IsServedPath( path ) || path.StartsWith( "/files/", StringComparison.Ordinal ) )
            {
                Reply( context, 404 );
                return;
            }
            if ( path == "/health" )
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = HealthBody.Length;
                context.Response.OutputStream.Write( HealthBody, 0, HealthBody.Length );
                context.Response.Close();
                return;
            }
            // From here the path is /relay, and the checks and status codes are the worker's.
            if ( !context.Request.IsWebSocketRequest )
            {
                Reply( context, 426 );
                return;
            }
            string room = context.Request.QueryString["room"];
            if ( !RelayProtocol.TryParseHex( room, RelayProtocol.RoomBytes, out _ ) )
            {
                Reply( context, 400 );
                return;
            }
            if ( PendingCount() >= MaxPendingSockets )
            {
                refused++;
                Reply( context, 503 );
                return;
            }
            bool viaTunnel = false;
            foreach ( string header in TunnelHeaders )
                viaTunnel |= !string.IsNullOrEmpty( context.Request.Headers[header] );
            upgrades.Add( new Upgrade { Task = context.AcceptWebSocketAsync( null, TimeSpan.FromSeconds( 30 ) ), Context = context, RoomId = room, ViaTunnel = viaTunnel } );
        }
        catch ( Exception ex ) when ( ex is HttpListenerException || ex is InvalidOperationException || ex is ObjectDisposedException || ex is WebSocketException || ex is ArgumentException )
        {
            // A malformed upgrade (a bad Sec-WebSocket-Key, an unsupported version) or a client that left: refuse it and keep serving everyone else.
            Abort( context );
        }
    }

    private static void Reply( HttpListenerContext context, int status )
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentLength64 = 0;
            context.Response.Close();
        }
        catch ( Exception ex ) when ( ex is HttpListenerException || ex is InvalidOperationException || ex is ObjectDisposedException )
        {
            Abort( context );
        }
    }

    private static void Abort( HttpListenerContext context )
    {
        try
        {
            context?.Response.Abort();
        }
        catch ( Exception ex ) when ( ex is HttpListenerException || ex is InvalidOperationException || ex is ObjectDisposedException )
        {
            // Already gone.
        }
    }

    private void FinishUpgrades()
    {
        for ( int i = upgrades.Count - 1; i >= 0; i-- )
        {
            Upgrade upgrade = upgrades[i];
            if ( !upgrade.Task.IsCompleted )
                continue;
            upgrades.RemoveAt( i );
            if ( upgrade.Task.Status != TaskStatus.RanToCompletion )
            {
                Abort( upgrade.Context );
                continue;
            }
            // The room was validated from the address before the upgrade; no header the client sent is consulted for it.
            var peer = new Peer { Socket = upgrade.Task.GetAwaiter().GetResult().WebSocket, RoomId = upgrade.RoomId, ViaTunnel = upgrade.ViaTunnel, ConnectedAt = seconds() };
            peers.Add( peer );
            Read( peer );
        }
    }

    private int PendingCount()
    {
        // A closing socket is not counted: it is gone within the close grace, and counting it would let refused strangers hold the cap for that long.
        int pending = upgrades.Count;
        foreach ( var peer in peers )
        {
            if ( !peer.Proven && !peer.Closing )
                pending++;
        }
        return pending;
    }

    // ---- sockets -------------------------------------------------------------------------------------------------------

    private void Service( Peer peer, double now )
    {
        if ( peer.Done )
            return;
        if ( !peer.Proven && !peer.Closing && RelayLimits.ProofExpired( peer.ConnectedAt, now, false ) )
            Refuse( peer, "bad-hello", "No hello arrived in time." );

        if ( peer.Held != null && !peer.Closing )
        {
            if ( !TryForward( peer, peer.Held, now ) )
            {
                Flush( peer );
                return;
            }
            peer.Held = null;
            Read( peer );
        }
        for ( int n = 0; n < MaxMessagesPerPump && !peer.Closing && peer.Held == null && peer.Receiving != null && peer.Receiving.IsCompleted; n++ )
        {
            var receiving = peer.Receiving;
            peer.Receiving = null;
            if ( receiving.Status != TaskStatus.RanToCompletion )
            {
                Lost( peer );
                return;
            }
            OnReceived( peer, receiving.GetAwaiter().GetResult(), now );
        }
        Flush( peer );
        if ( !peer.Closing || peer.Done )
            return;
        if ( peer.ClosingTask == null && peer.Sending == null && peer.Outbound.Count == 0 )
        {
            try
            {
                peer.ClosingTask = peer.Socket.CloseOutputAsync( peer.CloseCode, peer.CloseReason, CancellationToken.None );
            }
            catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException )
            {
                peer.Done = true;
                return;
            }
        }
        if ( peer.ClosingTask == null || !peer.ClosingTask.IsCompleted )
        {
            if ( Elapsed( peer ) )
                peer.Done = true;
            return;
        }
        // The close frame is out. Aborting now could reset the connection before the client has read what was sent (Windows discards unread received
        // data on a reset, refusal message included), so the socket is kept until the client answers the close or the grace runs out.
        if ( peer.ClosingTask.Status != TaskStatus.RanToCompletion )
        {
            peer.Done = true;
            return;
        }
        if ( peer.Receiving == null )
        {
            peer.Used = 0;
            Read( peer );
        }
        if ( peer.Receiving.IsCompleted || Elapsed( peer ) )
            peer.Done = true;
    }

    // The close grace is socket hygiene, not policy, so it runs on real time: a test that freezes the injected clock must not keep closed sockets forever.
    private static bool Elapsed( Peer peer ) => Environment.TickCount64 - peer.ClosingSince >= CloseGraceMilliseconds;

    private static void Read( Peer peer )
    {
        try
        {
            peer.Receiving = peer.Socket.ReceiveAsync( new ArraySegment<byte>( peer.Buffer, peer.Used, peer.Buffer.Length - peer.Used ), CancellationToken.None );
        }
        catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException )
        {
            peer.Receiving = Task.FromException<WebSocketReceiveResult>( ex );
        }
    }

    private void OnReceived( Peer peer, WebSocketReceiveResult result, double now )
    {
        if ( result.MessageType == WebSocketMessageType.Close )
        {
            End( peer, WebSocketCloseStatus.NormalClosure, "left" );
            return;
        }
        peer.Used += result.Count;
        if ( peer.Used > RelayLimits.MaxFrameBytes )
        {
            // Closed as soon as the frame is known to be too big; the rest of it is never read.
            refused++;
            End( peer, WebSocketCloseStatus.MessageTooBig, "frame too big" );
            return;
        }
        if ( !result.EndOfMessage )
        {
            Read( peer );
            return;
        }
        byte[] frame = peer.Buffer.AsSpan( 0, peer.Used ).ToArray();
        peer.Used = 0;
        if ( !peer.Proven )
            Hello( peer, result.MessageType, frame );
        else if ( result.MessageType != WebSocketMessageType.Binary )
            End( peer, WebSocketCloseStatus.InvalidMessageType, "binary only" );
        else if ( !TryForward( peer, frame, now ) )
            peer.Held = frame;
        if ( !peer.Closing && peer.Held == null )
            Read( peer );
    }

    private void Hello( Peer peer, WebSocketMessageType type, byte[] data )
    {
        if ( type != WebSocketMessageType.Text )
        {
            Refuse( peer, "bad-hello", "The first message must be a text hello." );
            return;
        }
        string text;
        try
        {
            text = StrictUtf8.GetString( data );
        }
        catch ( ArgumentException )
        {
            Refuse( peer, "bad-hello", "The control message is not valid UTF-8." );
            return;
        }
        if ( !RelayProtocol.TryParseControl( text, out var hello, out string error ) )
        {
            Refuse( peer, "bad-hello", error );
            return;
        }
        string invalid = RelayProtocol.ValidateHello( hello, out _, out _, out _, out _ );
        if ( invalid != null )
        {
            Refuse( peer, hello.Version != RelayProtocol.ProtocolVersion ? "version" : "bad-hello", invalid );
            return;
        }
        if ( !string.Equals( hello.Room, peer.RoomId, StringComparison.Ordinal ) )
        {
            Refuse( peer, "bad-hello", "The room identifier does not match the address." );
            return;
        }
        if ( hello.Type == RelayProtocol.HostHello )
            HostHello( peer, hello );
        else
            GuestHello( peer, hello );
    }

    private void HostHello( Peer peer, RelayControl hello )
    {
        // No access key is configured on a relay the host runs for itself, so any key the hello carries is accepted, as the worker does with none set.
        // What stands in for the key is where the hello came from: only the host's own editor opens a room, and it never dials through the tunnel.
        if ( peer.ViaTunnel )
        {
            Refuse( peer, "bad-hello", "A host connects to its own relay from the same computer, not through the public address." );
            return;
        }
        rooms.TryGetValue( peer.RoomId, out var old );
        if ( old == null && rooms.Count >= MaxRooms )
        {
            Refuse( peer, "limit", "This relay already has as many rooms open as it allows." );
            return;
        }
        if ( old != null )
        {
            if ( !FixedEqual( old.HostProof, hello.HostProof ) )
            {
                Refuse( peer, "room-taken", "A different host owns this room." );
                return;
            }
            // The same host returning after a blip wins over its old socket. The old socket's guests are closed with it, because the new connection has no
            // record of their channels. All are marked departed first so none of them tells a host that no longer owns the room about the others.
            rooms.Remove( peer.RoomId );
            old.Host.Departed = true;
            foreach ( var guest in old.Guests.Values )
            {
                guest.Departed = true;
                End( guest, WebSocketCloseStatus.EndpointUnavailable, "host restarted" );
            }
            End( old.Host, WebSocketCloseStatus.NormalClosure, "replaced" );
        }
        var room = new Room { Host = peer, HostProof = hello.HostProof, GuestProof = hello.GuestProof, Next = old?.Next ?? 0 };
        rooms[peer.RoomId] = room;
        peer.Room = room;
        peer.Host = true;
        peer.Proven = true;
        Queue( peer, Encoding.UTF8.GetBytes( RelayProtocol.BuildOk() ), WebSocketMessageType.Text );
    }

    private void GuestHello( Peer peer, RelayControl hello )
    {
        if ( !rooms.TryGetValue( peer.RoomId, out var room ) )
        {
            Refuse( peer, "no-host", "The host is not connected." );
            return;
        }
        if ( !FixedEqual( room.GuestProof, hello.Proof ) )
        {
            Refuse( peer, "bad-proof", "The proof was not accepted." );
            return;
        }
        if ( RelayLimits.AdmitMember( 1 + room.Guests.Count ) != null )
        {
            Refuse( peer, "full", "The room is full." );
            return;
        }
        peer.Room = room;
        peer.Channel = ++room.Next;
        peer.Proven = true;
        room.Guests.Add( peer.Channel, peer );
        Queue( peer, Encoding.UTF8.GetBytes( RelayProtocol.BuildOk() ), WebSocketMessageType.Text );
        Queue( room.Host, RelayProtocol.BuildFrame( RelayFrameType.Open, peer.Channel, ReadOnlySpan<byte>.Empty ), WebSocketMessageType.Binary );
    }

    // Equal-length comparison that does not stop at the first difference, as the worker's sameText.
    private static bool FixedEqual( string a, string b )
    {
        if ( a == null || b == null || a.Length != b.Length )
            return false;
        int diff = 0;
        for ( int i = 0; i < a.Length; i++ )
            diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>
    /// Forwards one frame from a proven member, or returns false when the room's rate window is full, in which case the caller holds the frame and stops
    /// reading that socket until the window has room. The relay never drops a frame for rate: each one is a numbered piece of the session's AES-GCM
    /// stream, so a dropped frame would end that member's session at the next frame, and a large scene snapshot is hundreds of frames sent back to back.
    /// </summary>
    private bool TryForward( Peer peer, byte[] data, double now )
    {
        if ( data.Length == 0 )
        {
            refused++;
            return true;
        }
        Room room = peer.Room;
        if ( peer.Host )
        {
            if ( !RelayProtocol.TryParseFrame( data, out var kind, out uint channel, out _ ) )
            {
                refused++;
                End( peer, WebSocketCloseStatus.ProtocolError, "bad frame" );
                return true;
            }
            // A frame for a channel that has already gone is ignored, as the worker does; the host learns of the departure from the Close frame.
            if ( !room.Guests.TryGetValue( channel, out var guest ) )
                return true;
            if ( kind == RelayFrameType.Close )
            {
                guest.ClosedByHost = true;
                End( guest, WebSocketCloseStatus.NormalClosure, "closed by host" );
                return true;
            }
            if ( kind != RelayFrameType.Data || data.Length == RelayProtocol.HeaderBytes )
                return true;
            if ( !Admit( peer, room, now ) )
                return false;
            Queue( guest, data.AsSpan( RelayProtocol.HeaderBytes ).ToArray(), WebSocketMessageType.Binary );
            bytesForwarded += data.Length - RelayProtocol.HeaderBytes;
        }
        else
        {
            if ( !Admit( peer, room, now ) )
                return false;
            Queue( room.Host, RelayProtocol.BuildFrame( RelayFrameType.Data, peer.Channel, data ), WebSocketMessageType.Binary );
            bytesForwarded += data.Length;
        }
        forwarded++;
        return true;
    }

    private bool Admit( Peer peer, Room room, double now )
    {
        if ( room.Rate.TryConsume( now ) )
            return true;
        // Counted once per held frame, not once per editor frame spent waiting for it.
        if ( peer.Held == null )
            throttled++;
        return false;
    }

    private void Refuse( Peer peer, string code, string message )
    {
        refused++;
        Queue( peer, Encoding.UTF8.GetBytes( RelayProtocol.BuildError( code, message ) ), WebSocketMessageType.Text );
        End( peer, WebSocketCloseStatus.PolicyViolation, code );
    }

    private void Queue( Peer peer, byte[] bytes, WebSocketMessageType type )
    {
        if ( peer.Closing || peer.Done )
            return;
        if ( peer.QueuedBytes + bytes.Length > MaxQueuedBytes )
        {
            // A reader this far behind cannot catch up, and nothing queued for it could be sent before a close frame, so it is aborted at once.
            refused++;
            End( peer, WebSocketCloseStatus.PolicyViolation, "outbound limit" );
            peer.Outbound.Clear();
            peer.QueuedBytes = 0;
            peer.Done = true;
            return;
        }
        peer.Outbound.Enqueue( ( bytes, type ) );
        peer.QueuedBytes += bytes.Length;
        // Started now rather than at the target's turn in this Pump, so a frame is never held back a further editor frame.
        Flush( peer );
    }

    private void Flush( Peer peer )
    {
        while ( !peer.Done )
        {
            if ( peer.Sending != null )
            {
                if ( !peer.Sending.IsCompleted )
                    return;
                bool failed = peer.Sending.Status != TaskStatus.RanToCompletion;
                peer.Sending = null;
                if ( failed )
                {
                    Lost( peer );
                    return;
                }
            }
            if ( peer.Outbound.Count == 0 )
                return;
            var next = peer.Outbound.Dequeue();
            peer.QueuedBytes -= next.Bytes.Length;
            try
            {
                peer.Sending = peer.Socket.SendAsync( new ArraySegment<byte>( next.Bytes ), next.Type, true, CancellationToken.None );
            }
            catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException )
            {
                Lost( peer );
                return;
            }
        }
    }

    // The socket failed underneath (the client vanished): the room is told as for a departure, and the socket is discarded without a close handshake.
    private void Lost( Peer peer )
    {
        End( peer, WebSocketCloseStatus.EndpointUnavailable, "socket lost" );
        peer.Outbound.Clear();
        peer.QueuedBytes = 0;
        peer.Done = true;
    }

    /// <summary>Starts closing a socket and applies the worker's departure rules: a host leaving ends its room and closes its guests; a guest leaving is a Close frame to the host.</summary>
    private void End( Peer peer, WebSocketCloseStatus status, string reason )
    {
        if ( peer.Closing )
            return;
        peer.Closing = true;
        peer.Held = null;
        peer.ClosingSince = Environment.TickCount64;
        peer.CloseCode = status;
        peer.CloseReason = reason;
        if ( peer.Departed || peer.Room == null )
            return;
        peer.Departed = true;
        Room room = peer.Room;
        if ( peer.Host )
        {
            // Only the current host ends the room; a replaced socket closing later must not tear down its successor's room.
            if ( rooms.TryGetValue( peer.RoomId, out var current ) && ReferenceEquals( room, current ) )
            {
                rooms.Remove( peer.RoomId );
                foreach ( var guest in room.Guests.Values )
                {
                    guest.Departed = true;
                    End( guest, WebSocketCloseStatus.EndpointUnavailable, "host left" );
                }
                room.Guests.Clear();
            }
        }
        else
        {
            room.Guests.Remove( peer.Channel );
            // Skipped after the host's own Close frame, so the host is not sent a second Close for a channel it closed itself.
            if ( !peer.ClosedByHost )
                Queue( room.Host, RelayProtocol.BuildFrame( RelayFrameType.Close, peer.Channel, ReadOnlySpan<byte>.Empty ), WebSocketMessageType.Binary );
        }
    }

    private static void Discard( Peer peer )
    {
        try
        {
            peer.Socket.Abort();
            peer.Socket.Dispose();
        }
        catch ( Exception ex ) when ( ex is WebSocketException || ex is ObjectDisposedException || ex is InvalidOperationException )
        {
            // Already torn down.
        }
    }

    /// <summary>Closes every socket without waiting for any handshake, stops the listener, and returns.</summary>
    public void Dispose()
    {
        if ( disposed )
            return;
        disposed = true;
        try
        {
            listener.Stop();
        }
        catch ( ObjectDisposedException )
        {
            // A failed Start leaves the listener already closed.
        }
        foreach ( var peer in peers )
            Discard( peer );
        foreach ( var upgrade in upgrades )
            Abort( upgrade.Context );
        try
        {
            listener.Close();
        }
        catch ( ObjectDisposedException )
        {
            // Closed by the failed Start as well.
        }
        peers.Clear();
        upgrades.Clear();
        rooms.Clear();
        accept = null;
    }
}
