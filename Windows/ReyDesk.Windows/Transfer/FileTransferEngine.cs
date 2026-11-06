using Google.Protobuf;
using ReyDesk.Protocol.V1;
using System.Security.Cryptography;

namespace ReyDesk.Windows.Transfer;

/// <summary>Destination chosen locally. Open only after hash verification. Cleanup
/// must delete only a newly created destination, never a pre-existing user file.</summary>
internal sealed record TransferDestination(Func<Stream> OpenWrite, Action DiscardCreatedDestination);
internal sealed record TransferStatus(string Id, long Bytes, long Total, string State);

/// <summary>One session, one bounded transfer, no resumptions or arbitrary paths.
/// File contents stage in an owned temporary file before the chosen destination opens.</summary>
internal sealed class FileTransferEngine : IDisposable
{
    public const int ChunkBytes = 16384;
    public const long MaximumBytes = 100 * 1024 * 1024;
    private readonly Func<Envelope, bool> _send;
    private readonly Func<bool> _authorized;
    private readonly Func<FileTransferOffer, Task<TransferDestination?>> _consent;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly Timer _timer;
    private Sending? _sending;
    private Receiving? _receiving;
    public event Action<TransferStatus>? Progress;
    private sealed class Sending
    {
        public required string Id;
        public required CancellationTokenSource Cancellation;
        public TaskCompletionSource<bool> Ready = NewCompletion();
        public TaskCompletionSource<bool>? Acknowledgement;
        public long Expected;
        public bool Final;
    }
    private sealed class Receiving
    {
        public required FileTransferOffer Offer;
        public required CancellationTokenSource Cancellation;
        public TransferDestination? Destination;
        public FileStream? Staging;
        public string? TempPath;
        public IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Offset;
        public long Deadline = Environment.TickCount64 + 30_000;
    }
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FileTransferEngine(Func<Envelope, bool> send, Func<bool> authorized,
        Func<FileTransferOffer, Task<TransferDestination?>> receiveConsent)
    {
        _send = send; _authorized = authorized; _consent = receiveConsent;
        _timer = new Timer(_ => _ = ExpireAsync(), null, 1000, 1000);
    }
    public static bool SafeName(string value) => value.Length is > 0 and <= 160 &&
        value is not "." and not ".." && !value.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '\0');
    public static bool ValidOffer(FileTransferOffer offer) => offer.TransferId.Length is > 0 and <= 64 &&
        SafeName(offer.FileName) && offer.FileSizeBytes <= MaximumBytes && offer.Sha256Hex.Length == 64 &&
        offer.Sha256Hex.All(Uri.IsHexDigit);
    private bool Allowed => !_lifetime.IsCancellationRequested && _authorized();
    private bool Send(Envelope envelope) => Allowed && _send(envelope);
    private void Report(string id, long bytes, long total, string state)
    {
        // UI observers cannot turn a verified transfer into a protocol failure.
        foreach (Action<TransferStatus> observer in Progress?.GetInvocationList() ?? [])
            try { observer(new(id, bytes, total, state)); } catch { }
    }
    private static void RequestCancellation(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Takes ownership of a readable seekable local stream. Display name is metadata only.</summary>
    public async Task<bool> SendAsync(Stream source, string displayName, long length)
    {
        using (source)
        {
            if (!Allowed || !source.CanRead || !source.CanSeek || !SafeName(displayName) || length is < 0 or > MaximumBytes) return false;
            var state = new Sending { Id = Guid.NewGuid().ToString("N"), Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token) };
            lock (_sync)
            {
                if (_sending is not null || _receiving is not null || _used.Count >= 128) { state.Cancellation.Dispose(); return false; }
                _sending = state; _used.Add(state.Id);
            }
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[ChunkBytes]; long hashed = 0;
                source.Position = 0;
                while (true)
                {
                    EnsureAllowed(state.Cancellation.Token);
                    var read = await source.ReadAsync(buffer, state.Cancellation.Token);
                    if (read == 0) break;
                    hashed += read; if (hashed > length) throw new InvalidDataException();
                    hash.AppendData(buffer, 0, read);
                }
                if (hashed != length) throw new InvalidDataException();
                var offer = new FileTransferOffer { TransferId = state.Id, FileName = displayName,
                    FileSizeBytes = (ulong)length, Sha256Hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() };
                if (!Send(new Envelope { FileTransferOffer = offer }) || !await state.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30), state.Cancellation.Token)) throw new IOException();
                source.Position = 0; long offset = 0;
                do
                {
                    EnsureAllowed(state.Cancellation.Token);
                    var wanted = (int)Math.Min(ChunkBytes, length - offset);
                    var read = 0;
                    while (read < wanted)
                    {
                        var count = await source.ReadAsync(buffer.AsMemory(read, wanted - read), state.Cancellation.Token);
                        if (count == 0) throw new EndOfStreamException();
                        read += count;
                    }
                    var final = offset + read == length;
                    lock (_sync) { state.Expected = offset + read; state.Final = final; state.Acknowledgement = NewCompletion(); }
                    if (!Send(new Envelope { FileTransferChunk = new FileTransferChunk { TransferId = state.Id, Offset = (ulong)offset,
                        Data = ByteString.CopyFrom(buffer, 0, read), Final = final } })) throw new IOException();
                    if (!await state.Acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(30), state.Cancellation.Token)) throw new IOException();
                    offset += read; Report(state.Id, offset, length, final ? "completed" : "sending");
                    if (final) return true;
                } while (true);
            }
            catch (Exception) { SendCancel(state.Id); Report(state.Id, 0, length, "failed_or_canceled"); return false; }
            finally
            {
                lock (_sync) if (ReferenceEquals(_sending, state)) _sending = null;
                state.Cancellation.Dispose();
            }
        }
    }

    public async Task HandleAsync(Envelope envelope)
    {
        if (!Allowed) { Cancel(); return; }
        if (envelope.FileTransferResponse is { } response)
        {
            lock (_sync) if (_sending is { } sending && sending.Id == response.TransferId)
                sending.Ready.TrySetResult(response.Decision == FileTransferDecision.Accept);
            return;
        }
        if (envelope.FileTransferProgress is { } progress)
        {
            lock (_sync) if (_sending is { } sending && sending.Id == progress.TransferId)
            {
                var valid = progress.BytesTransferred == (ulong)sending.Expected &&
                    progress.State == (sending.Final ? FileTransferState.Completed : FileTransferState.InProgress);
                sending.Acknowledgement?.TrySetResult(valid);
            }
            return;
        }
        if (envelope.FileTransferControl is { } control)
        {
            CancellationTokenSource? sendingCancellation = null, receivingCancellation = null;
            lock (_sync)
            {
                if (_sending is { } sending && sending.Id == control.TransferId) sendingCancellation = sending.Cancellation;
                if (_receiving is { } receiving && receiving.Offer.TransferId == control.TransferId) receivingCancellation = receiving.Cancellation;
            }
            RequestCancellation(sendingCancellation); RequestCancellation(receivingCancellation);
            await AbortReceiveAsync(control.TransferId, true); return;
        }
        if (envelope.FileTransferOffer is { } offer) { await OfferAsync(offer); return; }
        if (envelope.FileTransferChunk is { } chunk) await ChunkAsync(chunk);
    }

    private async Task OfferAsync(FileTransferOffer offer)
    {
        Receiving? state = null;
        lock (_sync)
        {
            if (ValidOffer(offer) && _sending is null && _receiving is null && _used.Count < 128 && _used.Add(offer.TransferId))
                _receiving = state = new Receiving { Offer = offer.Clone(), Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token) };
        }
        if (state is null) { SendResponse(offer.TransferId, false); return; }
        try
        {
            var destination = await _consent(state.Offer.Clone()).WaitAsync(TimeSpan.FromSeconds(30), state.Cancellation.Token);
            await _receiveGate.WaitAsync(state.Cancellation.Token);
            try
            {
                EnsureAllowed(state.Cancellation.Token);
                if (destination is null || !ReferenceEquals(_receiving, state)) throw new IOException();
                state.Destination = destination;
                state.TempPath = Path.Combine(Path.GetTempPath(), "reydesk-transfer-" + Guid.NewGuid().ToString("N") + ".part");
                state.Staging = new FileStream(state.TempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, ChunkBytes, FileOptions.Asynchronous);
                state.Deadline = Environment.TickCount64 + 30_000;
                if (!SendResponse(offer.TransferId, true)) throw new IOException();
                Report(offer.TransferId, 0, (long)offer.FileSizeBytes, "receiving");
            }
            finally { _receiveGate.Release(); }
        }
        catch (Exception) { SendResponse(offer.TransferId, false); await AbortReceiveAsync(offer.TransferId); }
    }

    private async Task ChunkAsync(FileTransferChunk chunk)
    {
        await _receiveGate.WaitAsync();
        try
        {
            var state = _receiving;
            if (state is null || state.Offer.TransferId != chunk.TransferId) { SendCancel(chunk.TransferId); return; }
            try
            {
                EnsureAllowed(state.Cancellation.Token);
                if (state.Staging is null || state.Destination is null || chunk.Data.Length > ChunkBytes ||
                    chunk.Offset != (ulong)state.Offset || (ulong)state.Offset + (ulong)chunk.Data.Length > state.Offer.FileSizeBytes ||
                    chunk.Final != ((ulong)state.Offset + (ulong)chunk.Data.Length == state.Offer.FileSizeBytes) ||
                    (!chunk.Final && chunk.Data.Length == 0)) throw new InvalidDataException();
                await state.Staging.WriteAsync(chunk.Data.Memory, state.Cancellation.Token);
                state.Hash.AppendData(chunk.Data.Span); state.Offset += chunk.Data.Length;
                if (chunk.Final)
                {
                    if (!Convert.ToHexString(state.Hash.GetHashAndReset()).Equals(state.Offer.Sha256Hex, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                    EnsureAllowed(state.Cancellation.Token);
                    state.Staging.Position = 0;
                    try
                    {
                        using var destination = state.Destination.OpenWrite();
                        var buffer = new byte[ChunkBytes]; int count;
                        while ((count = await state.Staging.ReadAsync(buffer, state.Cancellation.Token)) > 0)
                        {
                            EnsureAllowed(state.Cancellation.Token);
                            await destination.WriteAsync(buffer.AsMemory(0, count), state.Cancellation.Token);
                        }
                        await destination.FlushAsync(state.Cancellation.Token);
                        EnsureAllowed(state.Cancellation.Token);
                    }
                    catch { state.Destination.DiscardCreatedDestination(); throw; }
                }
                Send(new Envelope { FileTransferProgress = new FileTransferProgress { TransferId = chunk.TransferId,
                    BytesTransferred = (ulong)state.Offset, State = chunk.Final ? FileTransferState.Completed : FileTransferState.InProgress } });
                Report(chunk.TransferId, state.Offset, (long)state.Offer.FileSizeBytes, chunk.Final ? "completed" : "receiving");
                state.Deadline = Environment.TickCount64 + 30_000;
                if (chunk.Final) CleanupReceive(state);
            }
            catch (Exception) { SendCancel(chunk.TransferId); Report(chunk.TransferId, state.Offset, (long)state.Offer.FileSizeBytes, "failed_or_canceled"); CleanupReceive(state); }
        }
        finally { _receiveGate.Release(); }
    }

    private void EnsureAllowed(CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); if (!Allowed) throw new UnauthorizedAccessException(); }
    private bool SendResponse(string id, bool accept) => id.Length <= 64 && Send(new Envelope { FileTransferResponse = new FileTransferResponse { TransferId = id, Decision = accept ? FileTransferDecision.Accept : FileTransferDecision.Reject } });
    private void SendCancel(string id) { if (id.Length <= 64) Send(new Envelope { FileTransferControl = new FileTransferControl { TransferId = id, RequestedState = FileTransferState.Canceled } }); }
    private async Task AbortReceiveAsync(string? id = null, bool report = false)
    {
        await _receiveGate.WaitAsync();
        try
        {
            if (_receiving is { } state && (id is null || state.Offer.TransferId == id))
            {
                if (report) Report(state.Offer.TransferId, state.Offset, (long)state.Offer.FileSizeBytes, "failed_or_canceled");
                CleanupReceive(state);
            }
        }
        finally { _receiveGate.Release(); }
    }
    private void CleanupReceive(Receiving state)
    {
        lock (_sync) if (ReferenceEquals(_receiving, state)) _receiving = null;
        RequestCancellation(state.Cancellation);
        try { state.Staging?.Dispose(); }
        finally
        {
            state.Hash.Dispose(); state.Cancellation.Dispose();
            try { if (state.TempPath is { } path) File.Delete(path); }
            catch (IOException) { Console.WriteLine("[Transfer] Temporary transfer cleanup unavailable."); }
            catch (UnauthorizedAccessException) { Console.WriteLine("[Transfer] Temporary transfer cleanup unavailable."); }
        }
    }
    private async Task ExpireAsync()
    {
        try
        {
            var allowed = Allowed;
            Receiving? expired;
            lock (_sync) expired = _receiving is { } state && (!allowed || state.Deadline <= Environment.TickCount64) ? state : null;
            if (expired is null) return;
            RequestCancellation(expired.Cancellation);
            await _receiveGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_receiving, expired)) return;
                SendCancel(expired.Offer.TransferId);
                Report(expired.Offer.TransferId, expired.Offset, (long)expired.Offer.FileSizeBytes, "timed_out");
                CleanupReceive(expired);
            }
            finally { _receiveGate.Release(); }
        }
        catch (Exception) { Console.WriteLine("[Transfer] Temporary transfer cleanup unavailable."); }
    }
    public void Cancel()
    {
        string? sending; string? receiving;
        CancellationTokenSource? sendingCancellation, receivingCancellation;
        lock (_sync)
        {
            sending = _sending?.Id; receiving = _receiving?.Offer.TransferId;
            sendingCancellation = _sending?.Cancellation; receivingCancellation = _receiving?.Cancellation;
        }
        RequestCancellation(sendingCancellation); RequestCancellation(receivingCancellation);
        if (sending is not null) SendCancel(sending);
        if (receiving is not null) SendCancel(receiving);
        if (receiving is not null) _ = AbortReceiveAsync(receiving, report: true);
    }
    public void Dispose() { _lifetime.Cancel(); _timer.Dispose(); Cancel(); }
}
