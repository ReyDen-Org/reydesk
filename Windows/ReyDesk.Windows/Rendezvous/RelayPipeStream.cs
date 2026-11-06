using System.Threading.Channels;

namespace ReyDesk.Windows.Rendezvous;

/// <summary>Ordered duplex byte stream over a server-relayed pipe. Wrap it in SslStream.
/// Reads: bounded queue (backpressure to the websocket). Writes: split into frames of at most 16 KiB
/// and awaited on the shared websocket writer. End of stream = remote PipeClose; connection loss = IOException.</summary>
internal sealed class RelayPipeStream : Stream
{
    internal const int MaxChunk = 16 * 1024;
    private const int QueueFrames = 64; // 1 MiB, the per-pipe cap of the service

    private readonly RendezvousClient _owner;
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueFrames) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private byte[]? _leftover;
    private int _leftoverOffset;
    private int _closed; // 0 open, 1 closed (either side)
    private Exception? _failure;

    internal RelayPipeStream(RendezvousClient owner, string pipeId, byte[] peerSpki)
    {
        _owner = owner;
        PipeId = pipeId;
        PeerSpki = peerSpki;
    }

    public string PipeId { get; }
    public byte[] PeerSpki { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    // ---- called by RendezvousClient ----
    internal ValueTask EnqueueAsync(byte[] data, CancellationToken ct) =>
        IsClosed ? ValueTask.CompletedTask : _incoming.Writer.WriteAsync(data, ct);

    /// <summary>Remote end closed (clean EOF, failure null) or the connection died (failure set).</summary>
    internal void RemoteEnded(Exception? failure)
    {
        _failure = failure;
        Interlocked.Exchange(ref _closed, 1);
        _incoming.Writer.TryComplete();
    }

    // ---- Stream ----
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        while (true)
        {
            if (_leftover is not null)
            {
                var n = Math.Min(buffer.Length, _leftover.Length - _leftoverOffset);
                _leftover.AsMemory(_leftoverOffset, n).CopyTo(buffer);
                _leftoverOffset += n;
                if (_leftoverOffset >= _leftover.Length) _leftover = null;
                return n;
            }
            if (!await _incoming.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (_failure is { } f) throw new IOException("Relay connection lost.", f);
                return 0;
            }
            if (_incoming.Reader.TryRead(out var chunk) && chunk.Length > 0)
            {
                _leftover = chunk;
                _leftoverOffset = 0;
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override int Read(Span<byte> buffer)
    {
        var tmp = new byte[buffer.Length];
        var n = ReadAsync(tmp.AsMemory()).AsTask().GetAwaiter().GetResult();
        tmp.AsSpan(0, n).CopyTo(buffer);
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (buffer.Length > 0)
        {
            if (IsClosed) throw new IOException("Pipe is closed.", _failure);
            var n = Math.Min(buffer.Length, MaxChunk);
            await _owner.SendPipeDataAsync(PipeId, buffer.Slice(0, n), ct).ConfigureAwait(false);
            buffer = buffer.Slice(n);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _incoming.Writer.TryComplete();
            _owner.ClosePipeLocal(PipeId);
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        return ValueTask.CompletedTask;
    }
}
