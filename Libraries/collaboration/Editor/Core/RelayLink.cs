using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TeamCreate;

/// <summary>
/// The one thing the relay client needs from a WebSocket. The editor implements it over
/// <c>Sandbox.WebSocket</c>, the only socket API an editor script may use; tests implement it over the
/// framework's client. Events may fire on any thread and must not block.
/// </summary>
public interface IRelaySocket : IDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(string url, CancellationToken cancellationToken);

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    event Action<string> TextReceived;

    event Action<byte[]> BinaryReceived;

    event Action<int, string> Closed;
}

/// <summary>
/// The relay answered and said no. Deliberately not an <see cref="IOException"/>: automatic recovery
/// retries transient network faults, and a wrong code or a refused key does not get better by asking again.
/// </summary>
public sealed class RelayRefusedException : InvalidOperationException
{
    public string Code { get; }

    public RelayRefusedException(string code, string message)
        : base(message)
    {
        Code = code;
    }
}

public static class RelayGuest
{
    public const int DefaultTimeoutSeconds = 12;

    /// <summary>
    /// Dials the relay, proves knowledge of the invite secret, and returns a <see cref="Wire"/> that talks
    /// to the host through it. From here on the session behaves exactly as it does over direct TCP,
    /// including the encryption, because the relay only ever carries the sealed bytes.
    /// </summary>
    public static async Task<Wire> ConnectAsync(Func<IRelaySocket> factory, string url, byte[] secret, byte[] sessionKey, int timeoutSeconds = DefaultTimeoutSeconds, bool anyLoopbackPort = false)
    {
        if (!RelayProtocol.TryValidateUrl(url, out _, out string urlError, anyLoopbackPort))
        {
            throw new InvalidOperationException(urlError);
        }
        IRelaySocket socket = factory();
        RelayPipe pipe = null;
        TaskCompletionSource<RelayControl> answer = new TaskCompletionSource<RelayControl>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            pipe = new RelayPipe((data, ct) => new ValueTask(socket.SendBinaryAsync(data, ct)), () => TryClose(socket));
            RelayPipe livePipe = pipe;
            socket.TextReceived += text =>
            {
                if (RelayProtocol.TryParseControl(text, out RelayControl control, out _))
                {
                    answer.TrySetResult(control);
                }
            };
            socket.BinaryReceived += bytes =>
            {
                livePipe.Feed(bytes);
            };
            socket.Closed += (status, reason) =>
            {
                answer.TrySetException(new IOException("The relay closed the connection" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason)));
                livePipe.CompleteFromPeer();
            };
            try
            {
                await socket.ConnectAsync(RelayProtocol.ConnectUrl(url, secret), timeout.Token).ConfigureAwait(false);
                await socket.SendTextAsync(RelayProtocol.BuildGuestHello(secret), timeout.Token).ConfigureAwait(false);
                RelayControl reply = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                if (reply.Type != RelayProtocol.Ok)
                {
                    throw new RelayRefusedException(reply.Code, RelayProtocol.UserMessage(reply.Code, reply.Message));
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("The relay did not answer within " + timeoutSeconds + " seconds.");
            }
            return new Wire(pipe, null, sessionKey, initiator: true);
        }
        catch (RelayRefusedException)
        {
            Cleanup(pipe, socket);
            throw;
        }
        catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is InvalidOperationException)
        {
            Cleanup(pipe, socket);
            throw;
        }
        catch (Exception ex)
        {
            Cleanup(pipe, socket);
            // Every remaining failure is the network or the socket layer refusing: report it as a lost
            // route so recovery classifies it as retryable rather than terminal.
            throw new IOException("Could not reach the relay: " + CollaborationDiagnostics.Bounded(ex.Message), ex);
        }
    }

    private static void Cleanup(RelayPipe pipe, IRelaySocket socket)
    {
        if (pipe != null)
        {
            pipe.Dispose();
        }
        else
        {
            TryClose(socket);
        }
    }

    internal static void TryClose(IRelaySocket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch
        {
        }
    }
}

public enum RelayHostState
{
    Idle,
    Connecting,
    Ready,
    Reconnecting,
    Failed,
    Stopped
}

/// <summary>
/// The host's standing connection to the relay. It registers the session's room, then turns every guest the
/// relay announces into a <see cref="Wire"/> on <see cref="Accepted"/>, which the session server drains exactly
/// as it drains its TCP listener. Everything is Task-based and polled through <see cref="State"/>; no thread
/// is created, because the editor's hotloader must be able to suspend every managed thread it knows about.
/// </summary>
public sealed class RelayHost : IDisposable
{
    public const int MaxChannels = 16;

    public const int MaxReconnectAttempts = 8;

    private readonly Func<IRelaySocket> factory;

    private readonly string url;

    private readonly byte[] secret;

    private readonly byte[] sessionKey;

    private readonly string accessKey;

    private readonly TimeSpan[] backoff;

    private readonly bool anyLoopbackPort;

    private readonly CancellationTokenSource stop = new CancellationTokenSource();

    private readonly object gate = new object();

    private readonly Dictionary<uint, RelayPipe> channels = new Dictionary<uint, RelayPipe>();

    private IRelaySocket socket;

    private volatile RelayHostState state = RelayHostState.Idle;

    private volatile string status;

    private volatile string failure;

    private int started;

    public ConcurrentQueue<Wire> Accepted { get; } = new ConcurrentQueue<Wire>();

    public RelayHostState State => state;

    /// <summary>What the dock shows while the relay route is not simply working.</summary>
    public string Status => status;

    /// <summary>Set only when the route is given up on; a terminal refusal or an exhausted retry budget.</summary>
    public string Failure => failure;

    public int ChannelCount
    {
        get
        {
            lock (gate)
            {
                return channels.Count;
            }
        }
    }

    public RelayHost(Func<IRelaySocket> factory, string url, byte[] secret, byte[] sessionKey, string accessKey, TimeSpan[] backoff = null, bool anyLoopbackPort = false)
    {
        this.anyLoopbackPort = anyLoopbackPort;
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.url = url;
        this.secret = secret;
        this.sessionKey = sessionKey;
        this.accessKey = string.IsNullOrWhiteSpace(accessKey) ? null : accessKey.Trim();
        this.backoff = backoff ?? new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15) };
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return;
        }
        if (!RelayProtocol.TryValidateUrl(url, out _, out string error, anyLoopbackPort))
        {
            Fail(error);
            return;
        }
        _ = Run();
    }

    private async Task Run()
    {
        int attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            state = attempt == 0 ? RelayHostState.Connecting : RelayHostState.Reconnecting;
            status = attempt == 0 ? "Connecting to the relay…" : "Relay connection lost; reconnecting (attempt " + attempt + " of " + MaxReconnectAttempts + ")…";
            TaskCompletionSource closedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Attach(closedSignal).ConfigureAwait(false);
                attempt = 0;
                state = RelayHostState.Ready;
                status = null;
                await closedSignal.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (RelayRefusedException refused) when (RelayProtocol.IsTerminal(refused.Code))
            {
                Fail(refused.Message);
                return;
            }
            catch (InvalidOperationException policy) when (policy is not RelayRefusedException && policy is not ObjectDisposedException)
            {
                // The socket layer itself said no (the engine's allow-list, an unusable address). Asking again
                // returns the same answer, so it ends here instead of spending the whole retry budget.
                Fail(CollaborationDiagnostics.Bounded(policy.Message, 200));
                return;
            }
            catch (Exception ex)
            {
                status = "Relay unavailable: " + CollaborationDiagnostics.Bounded(ex.Message, 160);
            }
            DropAllChannels();
            DisposeSocket();
            if (stop.IsCancellationRequested)
            {
                break;
            }
            attempt++;
            if (attempt > MaxReconnectAttempts)
            {
                Fail("The relay could not be reached after " + MaxReconnectAttempts + " attempts. Friends can still join on the local network, or restart the session once the relay is back.");
                return;
            }
            try
            {
                await Task.Delay(backoff[Math.Min(attempt - 1, backoff.Length - 1)], stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        state = RelayHostState.Stopped;
    }

    private async Task Attach(TaskCompletionSource closedSignal)
    {
        IRelaySocket fresh = factory();
        lock (gate)
        {
            socket = fresh;
        }
        TaskCompletionSource<RelayControl> answer = new TaskCompletionSource<RelayControl>(TaskCreationOptions.RunContinuationsAsynchronously);
        fresh.TextReceived += text =>
        {
            if (RelayProtocol.TryParseControl(text, out RelayControl control, out _))
            {
                answer.TrySetResult(control);
            }
        };
        fresh.BinaryReceived += bytes => OnFrame(fresh, bytes);
        fresh.Closed += (code, reason) =>
        {
            answer.TrySetException(new IOException("The relay closed the connection" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason)));
            closedSignal.TrySetResult();
        };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(RelayGuest.DefaultTimeoutSeconds));
        try
        {
            await fresh.ConnectAsync(RelayProtocol.ConnectUrl(url, secret), timeout.Token).ConfigureAwait(false);
            await fresh.SendTextAsync(RelayProtocol.BuildHostHello(secret, accessKey), timeout.Token).ConfigureAwait(false);
            RelayControl reply = await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            if (reply.Type != RelayProtocol.Ok)
            {
                throw new RelayRefusedException(reply.Code, RelayProtocol.UserMessage(reply.Code, reply.Message));
            }
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            throw new TimeoutException("The relay did not answer within " + RelayGuest.DefaultTimeoutSeconds + " seconds.");
        }
    }

    private void OnFrame(IRelaySocket from, byte[] message)
    {
        if (!RelayProtocol.TryParseFrame(message, out RelayFrameType type, out uint channel, out ReadOnlySpan<byte> payload))
        {
            return;
        }
        switch (type)
        {
            case RelayFrameType.Open:
                OpenChannel(from, channel);
                break;
            case RelayFrameType.Data:
                {
                    RelayPipe pipe;
                    lock (gate)
                    {
                        channels.TryGetValue(channel, out pipe);
                    }
                    if (pipe != null && !pipe.Feed(payload.ToArray()))
                    {
                        RemoveChannel(channel);
                    }
                    break;
                }
            case RelayFrameType.Close:
                {
                    RelayPipe pipe;
                    lock (gate)
                    {
                        channels.Remove(channel, out pipe);
                    }
                    pipe?.CompleteFromPeer();
                    break;
                }
        }
    }

    private void OpenChannel(IRelaySocket from, uint channel)
    {
        lock (gate)
        {
            if (channels.Count >= MaxChannels || channels.ContainsKey(channel))
            {
                _ = SendClose(from, channel);
                return;
            }
        }
        RelayPipe pipe = new RelayPipe((data, ct) => new ValueTask(from.SendBinaryAsync(RelayProtocol.BuildFrame(RelayFrameType.Data, channel, data.Span), ct)), () => OnPipeClosed(from, channel));
        lock (gate)
        {
            channels[channel] = pipe;
        }
        Accepted.Enqueue(new Wire(pipe, null, sessionKey, initiator: false, accepted: true));
    }

    private void OnPipeClosed(IRelaySocket from, uint channel)
    {
        bool known;
        lock (gate)
        {
            known = channels.Remove(channel);
        }
        if (known)
        {
            _ = SendClose(from, channel);
        }
    }

    private void RemoveChannel(uint channel)
    {
        RelayPipe pipe;
        lock (gate)
        {
            channels.Remove(channel, out pipe);
        }
        pipe?.Dispose();
    }

    private static async Task SendClose(IRelaySocket from, uint channel)
    {
        try
        {
            using CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await from.SendBinaryAsync(RelayProtocol.BuildFrame(RelayFrameType.Close, channel, ReadOnlySpan<byte>.Empty), timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // The relay tears the channel down when this socket closes; a lost Close changes nothing.
        }
    }

    private void DropAllChannels()
    {
        RelayPipe[] pipes;
        lock (gate)
        {
            pipes = new RelayPipe[channels.Count];
            channels.Values.CopyTo(pipes, 0);
            channels.Clear();
        }
        foreach (RelayPipe pipe in pipes)
        {
            pipe.CompleteFromPeer();
        }
    }

    private void DisposeSocket()
    {
        IRelaySocket old;
        lock (gate)
        {
            old = socket;
            socket = null;
        }
        if (old != null)
        {
            RelayGuest.TryClose(old);
        }
    }

    private void Fail(string message)
    {
        failure = message;
        status = message;
        state = RelayHostState.Failed;
        DropAllChannels();
        DisposeSocket();
    }

    public void Dispose()
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        DropAllChannels();
        DisposeSocket();
        state = RelayHostState.Stopped;
        Wire leftover;
        while (Accepted.TryDequeue(out leftover))
        {
            leftover.Dispose();
        }
    }
}
