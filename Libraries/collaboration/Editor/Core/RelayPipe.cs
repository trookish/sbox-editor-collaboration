using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TeamCreate;

/// <summary>
/// A byte stream that rides on discrete messages: what arrives is fed in with <see cref="Feed"/>, what
/// is written leaves through a send delegate in chunks no larger than a relay message. The session's
/// framing, encryption and sequencing sit above a <see cref="Stream"/> and never learn that the bytes
/// travel through a relay instead of straight over TCP.
/// </summary>
public sealed class RelayPipe : Stream
{
    // A reader that stops draining must not let the buffer grow without bound: past this the pipe closes,
    // which the session sees as a lost connection and recovers from, instead of the editor running out of memory.
    public const int MaxBufferedBytes = 32 * 1024 * 1024;

    private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send;

    private readonly Action closed;

    private byte[] current = Array.Empty<byte>();

    private int offset;

    private long buffered;

    private int disposed;

    public RelayPipe(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send, Action closed = null)
    {
        this.send = send ?? throw new ArgumentNullException(nameof(send));
        this.closed = closed;
    }

    public bool IsClosed => Volatile.Read(ref disposed) != 0;

    public long BufferedBytes => Interlocked.Read(ref buffered);

    /// <summary>Queues received bytes. Returns false, and closes the pipe, when it is closed or over its buffer budget.</summary>
    public bool Feed(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return !IsClosed;
        }
        if (IsClosed)
        {
            return false;
        }
        if (Interlocked.Add(ref buffered, data.Length) > MaxBufferedBytes)
        {
            Dispose();
            return false;
        }
        return incoming.Writer.TryWrite(data);
    }

    /// <summary>Marks the far end as gone without calling the close callback: the peer already knows.</summary>
    public void CompleteFromPeer()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            incoming.Writer.TryComplete();
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0)
        {
            return 0;
        }
        while (true)
        {
            if (offset < current.Length)
            {
                int count = Math.Min(destination.Length, current.Length - offset);
                current.AsMemory(offset, count).CopyTo(destination);
                offset += count;
                Interlocked.Add(ref buffered, -count);
                return count;
            }
            try
            {
                current = await incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                offset = 0;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        while (source.Length > 0)
        {
            if (IsClosed)
            {
                throw new IOException("The relay connection is closed.");
            }
            int count = Math.Min(source.Length, RelayProtocol.MaxChunkBytes);
            await send(source.Slice(0, count), cancellationToken).ConfigureAwait(false);
            source = source.Slice(count);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            incoming.Writer.TryComplete();
            try
            {
                closed?.Invoke();
            }
            catch
            {
                // A close notification that cannot be delivered changes nothing: the pipe is already closed.
            }
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The relay pipe is asynchronous only; a synchronous read would block the editor's frame.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The relay pipe is asynchronous only.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
