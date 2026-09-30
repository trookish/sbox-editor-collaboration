using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace TeamCreate.Relay;

/// <summary>
/// Routes sealed bytes between one host and its guests. It authenticates who may attach — a host by
/// access key and room proof, a guest by proof of the invite secret — and never holds anything that could
/// decrypt the traffic it forwards.
/// </summary>
public sealed class RelayServer
{
    private sealed class Peer
    {
        public WebSocket Socket;

        public string Address;

        public Channel<(WebSocketMessageType Type, byte[] Data)> Outgoing;

        public Task Sender;

        public CancellationTokenSource Lifetime = new CancellationTokenSource();

        public double Tokens;

        public long LastRefill = Environment.TickCount64;

        public long LastSeen = Environment.TickCount64;

        public int Closed;
    }

    private sealed class Room
    {
        public string Id;

        public byte[] HostProof;

        public byte[] GuestProof;

        public Peer Host;

        public readonly Dictionary<uint, Peer> Guests = new Dictionary<uint, Peer>();

        public uint NextChannel;
    }

    private sealed class AddressState
    {
        public int Connections;

        public int Pending;

        public int Failures;

        public long WindowStart = Environment.TickCount64;
    }

    private readonly RelayOptions options;

    private readonly object gate = new object();

    private readonly Dictionary<string, Room> rooms = new Dictionary<string, Room>(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, AddressState> addresses = new ConcurrentDictionary<string, AddressState>(StringComparer.Ordinal);

    public RelayServer(RelayOptions options)
    {
        this.options = options;
    }

    public int RoomCount
    {
        get
        {
            lock (gate)
            {
                return rooms.Count;
            }
        }
    }

    public int GuestCount(string roomId)
    {
        lock (gate)
        {
            return rooms.TryGetValue(roomId, out Room room) ? room.Guests.Count : 0;
        }
    }

    public async Task HandleAsync(WebSocket socket, string address, CancellationToken requestAborted)
    {
        AddressState state = addresses.GetOrAdd(address, _ => new AddressState());
        lock (state)
        {
            if (state.Connections >= options.MaxConnectionsPerAddress || state.Pending >= options.MaxPendingPerAddress || IsThrottled(state))
            {
                state.Failures++;
                _ = RefuseAsync(socket, "limit", "Too many connections from this address.");
                return;
            }
            state.Connections++;
            state.Pending++;
        }
        Peer peer = new Peer
        {
            Socket = socket,
            Address = address,
            Outgoing = Channel.CreateBounded<(WebSocketMessageType, byte[])>(new BoundedChannelOptions(options.MaxQueuedMessages) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }),
            Tokens = options.BurstBytes
        };
        bool pendingReleased = false;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, peer.Lifetime.Token);
            peer.Sender = SendLoop(peer, linked.Token);
            RelayControl hello = await ReadHello(peer, linked.Token).ConfigureAwait(false);
            if (hello == null)
            {
                return;
            }
            string problem = RelayProtocol.ValidateHello(hello, out byte[] room, out byte[] hostProof, out byte[] guestProof, out byte[] proof);
            if (problem != null)
            {
                await RefuseAsync(socket, hello.Version != RelayProtocol.ProtocolVersion ? "version" : "bad-hello", problem).ConfigureAwait(false);
                return;
            }
            lock (state)
            {
                state.Pending--;
                pendingReleased = true;
            }
            string roomId = RelayProtocol.Hex(room);
            if (hello.Type == RelayProtocol.HostHello)
            {
                await RunHost(peer, state, roomId, hello, hostProof, guestProof, linked.Token).ConfigureAwait(false);
            }
            else
            {
                await RunGuest(peer, state, roomId, proof, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            lock (state)
            {
                state.Connections--;
                if (!pendingReleased)
                {
                    state.Pending--;
                }
            }
            await CloseAsync(peer).ConfigureAwait(false);
        }
    }

    private async Task RunHost(Peer peer, AddressState state, string roomId, RelayControl hello, byte[] hostProof, byte[] guestProof, CancellationToken ct)
    {
        if (!options.AccessKeyAccepted(hello.AccessKey))
        {
            NoteFailure(state);
            await RefuseAsync(peer.Socket, "bad-key", "The access key was not accepted.").ConfigureAwait(false);
            return;
        }
        Room room;
        Peer displaced = null;
        lock (gate)
        {
            if (!rooms.TryGetValue(roomId, out room))
            {
                if (rooms.Count >= options.MaxRooms)
                {
                    room = null;
                }
                else
                {
                    room = new Room { Id = roomId, HostProof = hostProof, GuestProof = guestProof };
                    rooms[roomId] = room;
                }
            }
            else if (!CryptographicOperations.FixedTimeEquals(room.HostProof, hostProof))
            {
                room = null;
                NoteFailure(state);
                _ = RefuseAsync(peer.Socket, "room-taken", "A different host owns this room.");
                return;
            }
            else
            {
                // The same host returning after a network blip. Its old socket may not have noticed yet, so the
                // newcomer wins: the old connection is dropped and its guests are closed with it.
                displaced = room.Host;
            }
            if (room != null)
            {
                room.Host = peer;
            }
        }
        if (room == null)
        {
            await RefuseAsync(peer.Socket, "limit", "The relay has no free rooms.").ConfigureAwait(false);
            return;
        }
        if (displaced != null)
        {
            await CloseAsync(displaced).ConfigureAwait(false);
        }
        Enqueue(peer, WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes(RelayProtocol.BuildOk()));
        try
        {
            byte[] buffer = new byte[RelayProtocol.MaxMessageBytes];
            while (!ct.IsCancellationRequested)
            {
                (WebSocketMessageType type, int length) = await ReceiveAsync(peer, buffer, ct).ConfigureAwait(false);
                if (type == WebSocketMessageType.Close || length < 0)
                {
                    break;
                }
                if (type != WebSocketMessageType.Binary || !TryReadFrame(buffer, length, out RelayFrameType frame, out uint channel, out byte[] payloadCopy))
                {
                    break;
                }
                Peer guest;
                lock (gate)
                {
                    room.Guests.TryGetValue(channel, out guest);
                }
                if (guest == null)
                {
                    continue;
                }
                if (frame == RelayFrameType.Data)
                {
                    Enqueue(guest, WebSocketMessageType.Binary, payloadCopy);
                }
                else if (frame == RelayFrameType.Close)
                {
                    await CloseAsync(guest).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Peer[] orphans;
            lock (gate)
            {
                if (ReferenceEquals(room.Host, peer))
                {
                    room.Host = null;
                    rooms.Remove(roomId);
                    orphans = room.Guests.Values.ToArray();
                    room.Guests.Clear();
                }
                else
                {
                    orphans = Array.Empty<Peer>();
                }
            }
            foreach (Peer guest in orphans)
            {
                await CloseAsync(guest).ConfigureAwait(false);
            }
        }
    }

    // Parsing lives outside the async method because a span cannot be held across an await; the copy is
    // what gets queued for the guest anyway.
    private static bool TryReadFrame(byte[] buffer, int length, out RelayFrameType frame, out uint channel, out byte[] payload)
    {
        payload = null;
        if (!RelayProtocol.TryParseFrame(buffer.AsSpan(0, length), out frame, out channel, out ReadOnlySpan<byte> body))
        {
            return false;
        }
        payload = body.ToArray();
        return true;
    }

    private async Task RunGuest(Peer peer, AddressState state, string roomId, byte[] proof, CancellationToken ct)
    {
        Room room;
        uint channel;
        Peer host;
        lock (gate)
        {
            rooms.TryGetValue(roomId, out room);
            if (room == null || room.Host == null)
            {
                _ = RefuseAsync(peer.Socket, "no-host", "The host is not connected.");
                return;
            }
            if (!CryptographicOperations.FixedTimeEquals(room.GuestProof, proof))
            {
                NoteFailure(state);
                _ = RefuseAsync(peer.Socket, "bad-proof", "The proof was not accepted.");
                return;
            }
            if (room.Guests.Count >= options.MaxGuestsPerRoom)
            {
                _ = RefuseAsync(peer.Socket, "full", "The room is full.");
                return;
            }
            channel = ++room.NextChannel;
            room.Guests[channel] = peer;
            host = room.Host;
        }
        Enqueue(peer, WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes(RelayProtocol.BuildOk()));
        Enqueue(host, WebSocketMessageType.Binary, RelayProtocol.BuildFrame(RelayFrameType.Open, channel, ReadOnlySpan<byte>.Empty));
        try
        {
            byte[] buffer = new byte[RelayProtocol.MaxChunkBytes];
            while (!ct.IsCancellationRequested)
            {
                (WebSocketMessageType type, int length) = await ReceiveAsync(peer, buffer, ct).ConfigureAwait(false);
                if (type != WebSocketMessageType.Binary || length < 0)
                {
                    break;
                }
                Enqueue(host, WebSocketMessageType.Binary, RelayProtocol.BuildFrame(RelayFrameType.Data, channel, buffer.AsSpan(0, length)));
            }
        }
        finally
        {
            bool wasMember;
            lock (gate)
            {
                wasMember = room.Guests.Remove(channel);
            }
            if (wasMember && !host.Lifetime.IsCancellationRequested)
            {
                Enqueue(host, WebSocketMessageType.Binary, RelayProtocol.BuildFrame(RelayFrameType.Close, channel, ReadOnlySpan<byte>.Empty));
            }
        }
    }

    private async Task<RelayControl> ReadHello(Peer peer, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.HelloTimeoutSeconds));
        byte[] buffer = new byte[RelayProtocol.MaxControlChars * 4];
        try
        {
            (WebSocketMessageType type, int length) = await ReceiveAsync(peer, buffer, timeout.Token).ConfigureAwait(false);
            if (type != WebSocketMessageType.Text || length < 0)
            {
                await RefuseAsync(peer.Socket, "bad-hello", "The first message must be a text hello.").ConfigureAwait(false);
                return null;
            }
            if (!RelayProtocol.TryParseControl(System.Text.Encoding.UTF8.GetString(buffer, 0, length), out RelayControl control, out string error))
            {
                await RefuseAsync(peer.Socket, "bad-hello", error).ConfigureAwait(false);
                return null;
            }
            return control;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await RefuseAsync(peer.Socket, "bad-hello", "No hello arrived in time.").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Reads one whole message, refusing anything larger than the buffer, and charges it against the connection's
    /// byte budget. A negative length means the connection ended or misbehaved and the caller should stop.
    /// </summary>
    private async Task<(WebSocketMessageType, int)> ReceiveAsync(Peer peer, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (true)
        {
            using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(options.IdleTimeoutSeconds));
            WebSocketReceiveResult result;
            try
            {
                result = await peer.Socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return (WebSocketMessageType.Close, -1);
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (WebSocketMessageType.Close, -1);
            }
            peer.LastSeen = Environment.TickCount64;
            total += result.Count;
            if (!Charge(peer, result.Count))
            {
                await RefuseAsync(peer.Socket, "limit", "This connection exceeded its bandwidth limit.").ConfigureAwait(false);
                return (WebSocketMessageType.Close, -1);
            }
            if (result.EndOfMessage)
            {
                return (result.MessageType, total);
            }
            if (total >= buffer.Length)
            {
                await RefuseAsync(peer.Socket, "limit", "A message exceeded the size limit.", WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                return (WebSocketMessageType.Close, -1);
            }
        }
    }

    private bool Charge(Peer peer, int bytes)
    {
        long now = Environment.TickCount64;
        double elapsed = Math.Max(0, now - peer.LastRefill) / 1000.0;
        peer.LastRefill = now;
        peer.Tokens = Math.Min(options.BurstBytes, peer.Tokens + elapsed * options.MaxBytesPerSecond) - bytes;
        return peer.Tokens >= 0;
    }

    private void Enqueue(Peer to, WebSocketMessageType type, byte[] data)
    {
        if (to.Closed != 0)
        {
            return;
        }
        if (!to.Outgoing.Writer.TryWrite((type, data)))
        {
            // A receiver that cannot keep up is disconnected rather than allowed to hold the queue's memory.
            _ = CloseAsync(to);
        }
    }

    private static async Task SendLoop(Peer peer, CancellationToken ct)
    {
        try
        {
            await foreach ((WebSocketMessageType type, byte[] data) in peer.Outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (peer.Socket.State != WebSocketState.Open)
                {
                    break;
                }
                await peer.Socket.SendAsync(new ArraySegment<byte>(data), type, true, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is WebSocketException || ex is ObjectDisposedException)
        {
        }
    }

    private static async Task CloseAsync(Peer peer)
    {
        if (Interlocked.Exchange(ref peer.Closed, 1) != 0)
        {
            return;
        }
        peer.Outgoing.Writer.TryComplete();
        try
        {
            // Give queued frames (an error reply, a final Close) a moment to leave before the socket goes.
            if (peer.Sender != null)
            {
                await Task.WhenAny(peer.Sender, Task.Delay(500)).ConfigureAwait(false);
            }
            if (peer.Socket.State == WebSocketState.Open || peer.Socket.State == WebSocketState.CloseReceived)
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await peer.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                peer.Lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task RefuseAsync(WebSocket socket, string code, string message, WebSocketCloseStatus status = WebSocketCloseStatus.PolicyViolation)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                byte[] reply = System.Text.Encoding.UTF8.GetBytes(RelayProtocol.BuildError(code, message));
                await socket.SendAsync(new ArraySegment<byte>(reply), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                await socket.CloseOutputAsync(status, code, timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private void NoteFailure(AddressState state)
    {
        lock (state)
        {
            state.Failures++;
        }
    }

    private bool IsThrottled(AddressState state)
    {
        long now = Environment.TickCount64;
        if (now - state.WindowStart > options.FailureWindowSeconds * 1000L)
        {
            state.WindowStart = now;
            state.Failures = 0;
        }
        return state.Failures >= options.MaxFailuresPerAddress;
    }
}
