using ReyDesk.Windows.Identity;
using ReyDesk.Windows.Pairing;
using ReyDesk.Windows.Rendezvous;

namespace ReyDesk.Windows.Remote;

/// <summary>Adapts the device identity store to the rendezvous client. DeviceIdentityService itself does not declare the interface
/// because several standalone test projects compile that file without the Rendezvous sources.</summary>
internal sealed class RendezvousIdentityAdapter(DeviceIdentityService identity) : IRendezvousIdentity
{
    public byte[] PublicKey => identity.PublicKey;
    public byte[] Sign(byte[] data) => identity.Sign(data);
    public string? DeskId => identity.DeskId;
    public void SetDeskId(string? deskId) => identity.SetDeskId(deskId);
}

/// <summary>One approval waiting for the owner. <see cref="Request"/>.Sas is "482 913" with ASCII digits: draw it as is (Latin
/// digits, left to right) even in RTL languages, never localize the digits. Call <see cref="Resolve"/> exactly once from the UI;
/// the request's Timeout is enforced by the gate, which resolves it as declined and raises PendingChanged.</summary>
internal sealed class PendingApproval
{
    private readonly TaskCompletionSource<PairingApprovalResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PendingApproval(PairingApprovalRequest request)
    {
        Request = request;
        ExpiresAt = DateTimeOffset.UtcNow + request.Timeout;
    }

    public PairingApprovalRequest Request { get; }
    public DateTimeOffset ExpiresAt { get; }
    internal Task<PairingApprovalResult> Result => _result.Task;
    public bool IsResolved => _result.Task.IsCompleted;

    /// <param name="allowRemote">Only honoured for LAN enrollment (the "Also allow over the internet" checkbox).</param>
    public void Resolve(bool approved, bool allowRemote = false) =>
        _result.TrySetResult(approved ? new PairingApprovalResult(true, allowRemote) : PairingApprovalResult.Declined);

    internal void Cancel() => _result.TrySetResult(PairingApprovalResult.Declined);
}

/// <summary>The always-approve-on-PC gate. While <see cref="UiAttached"/> is true (T11 sets it when its dialog host is alive) a
/// request becomes <see cref="Pending"/> and waits for <see cref="PendingApproval.Resolve"/>. Without a UI it falls back to
/// <see cref="Fallback"/> (the legacy SessionCoordinator.DeviceApprovalRequested dialog), and with neither it declines.</summary>
internal sealed class RemotePairingApprovalGate : IPairingApprovalGate
{
    private readonly object _sync = new();
    private PendingApproval? _pending;

    public bool UiAttached { get; set; }
    public Func<PairingApprovalRequest, CancellationToken, Task<PairingApprovalResult>>? Fallback { get; set; }

    /// <summary>The request the UI should show right now, or null. Raised on whichever thread changed it.</summary>
    public PendingApproval? Pending { get { lock (_sync) return _pending; } }
    public event Action? PendingChanged;

    public async Task<PairingApprovalResult> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken ct)
    {
        if (!UiAttached)
        {
            var fallback = Fallback;
            return fallback is null ? PairingApprovalResult.Declined : await fallback(request, ct).ConfigureAwait(false);
        }
        var pending = new PendingApproval(request);
        lock (_sync) _pending = pending;
        PendingChanged?.Invoke();
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var registration = linked.Token.Register(pending.Cancel);
        try { return await pending.Result.ConfigureAwait(false); }
        finally
        {
            pending.Cancel();
            lock (_sync) if (ReferenceEquals(_pending, pending)) _pending = null;
            PendingChanged?.Invoke();
        }
    }

    /// <summary>The SAS compare dialog (no Approve button). Any owner close before <paramref name="ct"/> fires counts as cancel.</summary>
    public async Task<bool> ShowComparisonAsync(PairingApprovalRequest request, CancellationToken ct)
    {
        if (!UiAttached)
        {
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            return false;
        }
        var pending = new PendingApproval(request with { CompareOnly = true });
        lock (_sync) _pending = pending;
        PendingChanged?.Invoke();
        using var registration = ct.Register(pending.Cancel);
        try
        {
            await pending.Result.ConfigureAwait(false);
            return !ct.IsCancellationRequested;
        }
        finally
        {
            pending.Cancel();
            lock (_sync) if (ReferenceEquals(_pending, pending)) _pending = null;
            PendingChanged?.Invoke();
        }
    }
}
