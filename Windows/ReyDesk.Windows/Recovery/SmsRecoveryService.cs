namespace ReyDesk.Windows.Recovery;

// Backend account ownership, verified recipients, billing/quota and provider
// delivery receipts are required before enabling this optional convenience.
internal sealed record SmsRecoveryRequest(string OwnedHostId, string VerifiedRecipientId, string Purpose, string RequestId);
internal sealed record SmsRecoveryResult(bool Accepted, string Status, string? ProviderReceipt = null);
internal interface ISmsRecoveryService
{
    bool IsConfigured { get; }
    Task<SmsRecoveryResult> RequestAsync(SmsRecoveryRequest request, CancellationToken cancellation);
}
internal sealed class UnconfiguredSmsRecoveryService : ISmsRecoveryService
{
    public bool IsConfigured => false;
    public Task<SmsRecoveryResult> RequestAsync(SmsRecoveryRequest request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new SmsRecoveryResult(false, "Unavailable: authenticated backend, verified recipient and SMS provider are not configured."));
    }
}
