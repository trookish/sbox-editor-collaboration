using System.Net.WebSockets;
using TeamCreate;

namespace TeamCreate.RelayTests;

/// <summary>
/// The relay socket seam over the framework's client. The editor's adapter wraps Sandbox.WebSocket
/// instead; this one exists so the same client code can be exercised against a real server offline.
/// </summary>
public sealed class ClientSocket : IRelaySocket
{
    private readonly ClientWebSocket socket = new ClientWebSocket();

    private readonly CancellationTokenSource lifetime = new CancellationTokenSource();

    private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);

    private int closedRaised;

    /// <summary>Lets a test observe or tamper with every binary message on its way in.</summary>
    public Func<byte[], IEnumerable<byte[]>> InboundBinaryFilter { get; set; }

    /// <summary>Lets a test observe every byte handed to the relay.</summary>
    public Action<byte[]> OutboundObserver { get; set; }

    public bool IsConnected => socket.State == WebSocketState.Open;

    public event Action<string> TextReceived;

    public event Action<byte[]> BinaryReceived;

    public event Action<int, string> Closed;

    public async Task ConnectAsync(string url, CancellationToken cancellationToken)
    {
        await socket.ConnectAsync(new Uri(url), cancellationToken);
        _ = ReceiveLoop();
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        Send(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, cancellationToken);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        byte[] copy = data.ToArray();
        OutboundObserver?.Invoke(copy);
        return Send(copy, WebSocketMessageType.Binary, cancellationToken);
    }

    private async Task Send(byte[] bytes, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), type, true, cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task ReceiveLoop()
    {
        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), lifetime.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        RaiseClosed((int?)result.CloseStatus ?? 0, result.CloseStatusDescription);
                        return;
                    }
                    total += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    TextReceived?.Invoke(System.Text.Encoding.UTF8.GetString(buffer, 0, total));
                }
                else
                {
                    byte[] message = buffer.AsSpan(0, total).ToArray();
                    IEnumerable<byte[]> delivered = InboundBinaryFilter == null ? new[] { message } : InboundBinaryFilter(message);
                    foreach (byte[] item in delivered)
                    {
                        BinaryReceived?.Invoke(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RaiseClosed(1006, ex.Message);
        }
    }

    private void RaiseClosed(int status, string reason)
    {
        if (Interlocked.Exchange(ref closedRaised, 1) == 0)
        {
            Closed?.Invoke(status, reason);
        }
    }

    public void Dispose()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        socket.Dispose();
        RaiseClosed(1000, "disposed");
    }
}
