using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Net;

namespace TeamCreate;

public static class CollaborationTransportDiagnostics
{
    public static string BoundedStoredDiagnostic(string value)
    {
        return CollaborationDiagnostics.Bounded(value);
    }

    public static string BoundedExceptionDiagnostic(string context, Exception error)
    {
        return CollaborationDiagnostics.DescribeExternalFailure(context, error);
    }
}

public sealed class Wire : IDisposable
{
    public const int MaxBytes = 8388608;

    public const string DecryptionFailure = "Decryption failed. The invite code or session key does not match the host's.";

    public const string PreambleFailure = "The peer did not speak the collaboration encryption protocol (unexpected preamble).";

    public const string PeerClosedFailure = "The peer closed the connection before the session was established.";

    private const int ConnectionSaltBytes = 16;

    private const int PreambleBytes = 20;

    private static readonly byte[] PreambleMagic = new byte[4] { 84, 67, 83, 49 };

    private const string ConnectionKeyInfo = "TeamCreate/v4-conn";

    private readonly Stream stream;

    private readonly IDisposable owner;

    private readonly byte[] sessionKey;

    private readonly bool initiator;

    private readonly byte[] connectionSalt;

    private AeadFraming framing;

    private readonly TaskCompletionSource keyReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly CancellationTokenSource stop = new CancellationTokenSource();

    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(256);

    private readonly object failureGate = new object();

    private string failure;

    private bool receivedAny;

    public ConcurrentQueue<Packet> Incoming { get; } = new ConcurrentQueue<Packet>();

    public string Failure
    {
        get
        {
            lock (failureGate)
            {
                return failure;
            }
        }
    }

    public string StoredFailure => (Failure == null) ? null : CollaborationTransportDiagnostics.BoundedStoredDiagnostic(Failure);

    public Wire(TcpClient socket, byte[] sessionKey = null, bool initiator = false)
        : this(OpenTcp(socket), socket, sessionKey, initiator)
    {
    }

    // The framing, encryption and sequencing all live above this constructor and see only a byte
    // stream, so a TCP socket and a relay-carried pipe are interchangeable. `owner` is what closes the
    // underlying resource (the TcpClient, or the relay socket); when it is null the stream closes itself.
    public Wire(Stream stream, IDisposable owner, byte[] sessionKey = null, bool initiator = false)
    {
        this.stream = stream;
        this.owner = owner;
        this.sessionKey = sessionKey;
        this.initiator = initiator;
        if ((sessionKey != null) & initiator)
        {
            connectionSalt = RandomNumberGenerator.GetBytes(16);
            framing = new AeadFraming(DeriveConnectionKey(sessionKey, connectionSalt), initiator);
        }
        _ = Read();
        _ = Write();
    }

    private static Stream OpenTcp(TcpClient socket)
    {
        socket.NoDelay = true;
        return socket.GetStream();
    }

    public const int DefaultConnectSeconds = 8;

    public static async Task<Wire> Connect(string address, int port, byte[] sessionKey = null, bool initiator = false, int timeoutSeconds = DefaultConnectSeconds)
    {
        TcpClient tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(address, port).WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
            return new Wire(tcp, sessionKey, initiator);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    internal static byte[] DeriveConnectionKey(byte[] baseKey, byte[] salt)
    {
        if (baseKey == null || baseKey.Length == 0)
        {
            throw new ArgumentException("A connection key requires a non-empty session key.", "baseKey");
        }
        if (salt == null || salt.Length != 16)
        {
            throw new ArgumentOutOfRangeException("salt", $"A connection salt is exactly {16} bytes.");
        }
        return InviteCode.HkdfSha256(baseKey, salt, Encoding.ASCII.GetBytes("TeamCreate/v4-conn"), 32);
    }

    public void Send(Packet p)
    {
        if (Failure != null)
        {
            throw new IOException(Failure);
        }
        byte[] array = JsonSerializer.SerializeToUtf8Bytes(p);
        int num = ((sessionKey == null) ? 8388608 : 8388580);
        if (array.Length > num || !outgoing.Writer.TryWrite(array))
        {
            throw new IOException("Connection queue or message limit exceeded; edit retained locally");
        }
    }

    private async Task Read()
    {
        try
        {
            byte[] header = new byte[4];
            bool flag = sessionKey != null && !initiator;
            bool flag2 = flag;
            if (flag2)
            {
                flag2 = !(await ReadPreambleAsync(stream, header));
            }
            if (flag2)
            {
                return;
            }
            while (!stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, stop.Token);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length < 1 || length > 8388608)
                {
                    throw new IOException("Invalid frame size");
                }
                byte[] bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, stop.Token);
                bytes = OpenFrame(bytes);
                if (bytes == null)
                {
                    break;
                }
                Packet packet = JsonSerializer.Deserialize<Packet>(bytes) ?? throw new IOException("Invalid packet");
                if (Incoming.Count >= 256)
                {
                    throw new IOException("Receive queue exceeded");
                }
                Incoming.Enqueue(packet);
                receivedAny = true;
            }
        }
        catch (Exception ex)
        {
            Exception e = ex;
            RecordLoopFailure(e);
        }
    }

    private async Task<bool> ReadPreambleAsync(Stream stream, byte[] header)
    {
        await stream.ReadExactlyAsync(header, stop.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > 8388608)
        {
            FailSession("The peer did not speak the collaboration encryption protocol (unexpected preamble).");
            return false;
        }
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, stop.Token);
        if (length != 20 || !((ReadOnlySpan<byte>)body.AsSpan(0, PreambleMagic.Length)).SequenceEqual((ReadOnlySpan<byte>)PreambleMagic))
        {
            FailSession(PreambleVerdict(body));
            return false;
        }
        byte[] salt = new byte[16];
        Array.Copy(body, PreambleMagic.Length, salt, 0, 16);
        framing = new AeadFraming(DeriveConnectionKey(sessionKey, salt), initiator);
        keyReady.TrySetResult();
        return true;
    }

    private static string PreambleVerdict(ReadOnlySpan<byte> body)
    {
        return ((body.Length > 0 && body[0] == 123) || IsDirectionTag(body)) ? "Decryption failed. The invite code or session key does not match the host's." : "The peer did not speak the collaboration encryption protocol (unexpected preamble).";
    }

    private static bool IsDirectionTag(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 4 && bytes[0] == 67 && bytes[1] == 84 && bytes[2] == 45 && (bytes[3] == 65 || bytes[3] == 66);
    }

    private static bool IsKeyedTraffic(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= PreambleMagic.Length && (bytes.Slice(0, PreambleMagic.Length).SequenceEqual(PreambleMagic) || IsDirectionTag(bytes));
    }

    private byte[] OpenFrame(byte[] bytes)
    {
        if (framing == null)
        {
            if (IsKeyedTraffic(bytes))
            {
                FailSession("Decryption failed. The invite code or session key does not match the host's.");
                return null;
            }
            return bytes;
        }
        try
        {
            return framing.Open(bytes);
        }
        catch (CryptographicException ex)
        {
            // A replay or a gap is a different fault from a wrong key, and telling the user their invite
            // code is wrong when the stream was tampered with would send them looking in the wrong place.
            FailSession(ex.Message == AeadFraming.SequenceFailure ? AeadFraming.SequenceFailure : "Decryption failed. The invite code or session key does not match the host's.");
            return null;
        }
    }

    private async Task Write()
    {
        try
        {
            if (sessionKey != null && !initiator)
            {
                await keyReady.Task.WaitAsync(stop.Token);
            }
            bool preamble = sessionKey != null && initiator;
            await foreach (byte[] jsonBytes in outgoing.Reader.ReadAllAsync(stop.Token))
            {
                if (preamble)
                {
                    preamble = false;
                    await WritePreambleAsync(stream);
                }
                byte[] bytes = ((framing == null) ? jsonBytes : framing.Seal(jsonBytes));
                // Header and body go out as one write. Over TCP that saves a syscall; over the relay it
                // is the difference between one WebSocket message per frame and two.
                byte[] frame = new byte[4 + bytes.Length];
                BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
                bytes.CopyTo(frame, 4);
                await stream.WriteAsync(frame, stop.Token);
            }
        }
        catch (Exception ex)
        {
            Exception e = ex;
            RecordLoopFailure(e);
        }
    }

    private async Task WritePreambleAsync(Stream stream)
    {
        byte[] frame = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(frame, 20);
        PreambleMagic.CopyTo(frame, 4);
        connectionSalt.CopyTo(frame, 4 + PreambleMagic.Length);
        await stream.WriteAsync(frame, stop.Token);
    }

    private void FailSession(string message)
    {
        if (!TryRecordFailure(message))
        {
            return;
        }
        stop.Cancel();
        CloseTransport();
    }

    private void CloseTransport()
    {
        try
        {
            (owner ?? stream).Dispose();
        }
        catch
        {
        }
        if (owner != null)
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
            }
        }
    }

    private bool TryRecordFailure(string message)
    {
        lock (failureGate)
        {
            if (failure != null)
            {
                return false;
            }
            failure = message;
        }
        return true;
    }

    private void RecordLoopFailure(Exception error)
    {
        if (!stop.IsCancellationRequested)
        {
            TryRecordFailure((receivedAny || !IsPeerClosure(error)) ? CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Transport failed: ", error) : "The peer closed the connection before the session was established.");
        }
    }

    private static bool IsPeerClosure(Exception error)
    {
        bool flag = ((error is EndOfStreamException || error is SocketException) ? true : false);
        return flag || (error is IOException && error.InnerException is SocketException);
    }

    public void Dispose()
    {
        stop.Cancel();
        CloseTransport();
        outgoing.Writer.TryComplete();
    }
}

public sealed class Listener : IDisposable
{
    private readonly TcpListener listener;

    private readonly CancellationTokenSource stop = new CancellationTokenSource();

    private readonly byte[] sessionKey;

    public ConcurrentQueue<Wire> Accepted { get; } = new ConcurrentQueue<Wire>();

    public string Failure { get; private set; }

    public string StoredFailure => (Failure == null) ? null : CollaborationTransportDiagnostics.BoundedStoredDiagnostic(Failure);

    public Listener(string address, int port, byte[] sessionKey = null)
    {
        listener = new TcpListener(IPAddress.Parse(address), port);
        listener.Start();
        this.sessionKey = sessionKey;
        _ = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                ConcurrentQueue<Wire> accepted = Accepted;
                accepted.Enqueue(new Wire(await listener.AcceptTcpClientAsync(stop.Token), sessionKey));
            }
        }
        catch (Exception ex)
        {
            Exception e = ex;
            if (!stop.IsCancellationRequested)
            {
                Failure = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Transport failed: ", e);
            }
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        Wire result;
        while (Accepted.TryDequeue(out result))
        {
            result.Dispose();
        }
    }
}
