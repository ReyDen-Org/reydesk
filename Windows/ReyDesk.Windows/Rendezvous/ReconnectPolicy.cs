using ReyDesk.Protocol.Rendezvous.V1;

namespace ReyDesk.Windows.Rendezvous;

internal enum ReconnectAction { Stop, Reregister, Retry }

/// <summary>What to do after a failed connection attempt (T18 H1 step 4). Pure, so it is unit tested.</summary>
internal static class ReconnectPolicy
{
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    /// <summary>A server cannot park the client longer than this with a retry-after value.</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultRateLimited = TimeSpan.FromSeconds(30);

    /// <summary>BAD_SIGNATURE and KEY_MISMATCH never succeed on retry and only feed the server's limits: stop and surface.
    /// UNKNOWN_ID and REVOKED drop the saved ID and register a fresh one at once (only when an ID was saved). Everything else (incl. RATE_LIMITED) backs off.</summary>
    public static ReconnectAction Decide(AuthError error) => error switch
    {
        AuthError.BadSignature or AuthError.KeyMismatch => ReconnectAction.Stop,
        AuthError.UnknownId or AuthError.Revoked => ReconnectAction.Reregister,
        _ => ReconnectAction.Retry,
    };

    /// <summary>1, 2, 4 ... seconds with +-20% jitter, never above 60 s, never below the server's retry-after.
    /// <paramref name="jitter"/> is in [-0.2, 0.2].</summary>
    public static TimeSpan Delay(int attempt, TimeSpan? retryAfter, double jitter)
    {
        var baseSeconds = Math.Min(MaxBackoff.TotalSeconds, 1 << Math.Clamp(attempt, 0, 6));
        var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, baseSeconds * (1 + Math.Clamp(jitter, -0.2, 0.2))));
        var floor = retryAfter is { } r && r > TimeSpan.Zero ? (r > MaxRetryAfter ? MaxRetryAfter : r) : TimeSpan.Zero;
        return backoff > floor ? backoff : floor;
    }
}
