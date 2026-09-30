using System;
using System.Threading;
using System.Threading.Tasks;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// <see cref="IRelaySocket"/> over the engine's own <see cref="WebSocket"/>. An editor script cannot use the
/// framework's <c>ClientWebSocket</c> directly for anything the engine polices, and this class is the reason
/// the relay is an option at all: the engine documents WebSockets as its supported way to reach an external
/// service, and its allow-list (DNS names only, no private ranges, no IP addresses) applies to every connect.
/// </summary>
public sealed class SandboxRelaySocket : IRelaySocket
{
    // The engine's default receive buffer is 64 KiB; a relay message is at most 16 KiB plus a five-byte
    // header, so the default has generous headroom and a hostile relay cannot make a bigger allocation.
    private readonly WebSocket socket = new WebSocket();

    private int closedRaised;

    public bool IsConnected => socket.IsConnected;

    public event Action<string> TextReceived;

    public event Action<byte[]> BinaryReceived;

    public event Action<int, string> Closed;

    public SandboxRelaySocket()
    {
        socket.OnMessageReceived += message => TextReceived?.Invoke(message);
        // The span is only valid for the duration of the callback, so it is copied before it is queued.
        socket.OnDataReceived += data => BinaryReceived?.Invoke(data.ToArray());
        socket.OnDisconnected += (status, reason) =>
        {
            if (Interlocked.Exchange(ref closedRaised, 1) == 0)
            {
                Closed?.Invoke(status, reason);
            }
        };
    }

    public Task ConnectAsync(string url, CancellationToken cancellationToken)
    {
        // Validated again here so a bad address fails with the library's own sentence rather than the
        // engine's generic "Access to '...' is not allowed."
        if (!RelayProtocol.TryValidateUrl(url, out _, out string error))
        {
            throw new InvalidOperationException(error);
        }
        return socket.Connect(url, cancellationToken);
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        return socket.Send(text).AsTask();
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        return socket.Send(data.ToArray()).AsTask();
    }

    public void Dispose()
    {
        socket.Dispose();
    }
}
