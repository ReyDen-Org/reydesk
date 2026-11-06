using ReyDesk.Protocol.Rendezvous.V1;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Pairing;

/// <summary>Everything the on-PC approval dialog needs. Nothing here is a secret except <see cref="Sas"/>, which is shown
/// to the owner and must never be logged.</summary>
/// <param name="Method">Which flow asks. TrustedAttended is the "Pixel 8 wants to connect" Accept/Decline prompt.</param>
/// <param name="Peer">Name the phone reports (unauthenticated text, display only).</param>
/// <param name="PeerKeyId">Canonical 12-char Key ID of the phone's key (use <c>KeyId.Display</c>); derived locally from the key.</param>
/// <param name="Sas">"482 913" with ASCII digits for the SAS methods (LanSas, RemoteCode), else null; the UI may localize digits.</param>
/// <param name="Medium">Local (LAN) or Remote (rendezvous pipe).</param>
/// <param name="AllowRemoteDefault">Initial state of the "Also allow over the internet" checkbox. For Remote enrollment it is
/// true and the checkbox is not offered: a remote pairing is always remote-allowed.</param>
/// <param name="AlreadyTrusted">The same key is already trusted (re-pair or reconnect prompt).</param>
/// <param name="Timeout">How long the listener waits; the token passed to the gate is cancelled when it elapses.</param>
internal sealed record PairingApprovalRequest(PairingMethod Method, DeviceIdentity Peer, string PeerKeyId, string? Sas,
    ConnectionMedium Medium, bool AllowRemoteDefault, bool AlreadyTrusted, TimeSpan Timeout, bool CompareOnly = false);

/// <summary>AllowRemote is honoured only for LAN enrollment; the listener forces true for remote pairings and ignores it for reconnect prompts.</summary>
internal readonly record struct PairingApprovalResult(bool Approved, bool AllowRemote = false)
{
    public static PairingApprovalResult Declined => new(false);
}

/// <summary>The always-approve-on-PC gate. The first remote pairing never completes without an owner click here (T11 wires
/// the dialog). Implementations must honour the token (close the dialog on cancel) and must not log the request.</summary>
internal interface IPairingApprovalGate
{
    Task<PairingApprovalResult> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken ct);

    /// <summary>Shows the SAS on the PC while the phone compares it (spec 2.2 step 4: both screens show the code before the phone
    /// confirms). Nothing is trusted here. The task completes with true only when the owner cancels on the PC; the caller cancels
    /// <paramref name="ct"/> to close it. The default shows nothing and never completes on its own.</summary>
    Task<bool> ShowComparisonAsync(PairingApprovalRequest request, CancellationToken ct) =>
        Task.Delay(System.Threading.Timeout.Infinite, ct).ContinueWith(_ => false, TaskScheduler.Default);
}

/// <summary>Compatibility shim for the old Func-based approval callback (SessionCoordinator.ApproveNewDevice). T10 replaces it
/// with an <see cref="IPairingApprovalGate"/> and deletes this class.</summary>
internal sealed class LegacyApprovalGate(Func<DeviceIdentity, Task<bool>> approve) : IPairingApprovalGate
{
    public async Task<PairingApprovalResult> RequestApprovalAsync(PairingApprovalRequest request, CancellationToken ct) =>
        new(await approve(request.Peer).ConfigureAwait(false));
}

/// <summary>Where a pairing connection came from. For Remote, Purpose, RequesterFingerprint and RequesterDeskId come from the
/// rendezvous IncomingPipe frame (server-supplied, so treated as hints and cross-checked, never as trust).</summary>
internal sealed record PairingContext(ConnectionMedium Medium, PipePurpose Purpose = PipePurpose.Unspecified,
    byte[]? RequesterFingerprint = null, string? RequesterDeskId = null)
{
    public static PairingContext Lan { get; } = new(ConnectionMedium.Local);
}

/// <summary>A successfully paired or reconnected, authenticated stream, ready to become a SignalingConnection. The receiver owns
/// <see cref="Owner"/> (disposing it closes the TLS stream and the socket or pipe).</summary>
internal sealed record PairedSession(Stream Stream, IDisposable Owner, ConnectionMedium Medium, bool WasUnattended,
    byte[] PeerPublicKey, DeviceIdentity Peer, PairingMethod Method);
