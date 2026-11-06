using System.Security.Cryptography;
using System.Text;

namespace ReyDesk.Windows.Pairing;

internal enum RemoteCodeCheck { Ok, Wrong, Dead }

/// <summary>The host half of the typed remote code (M4): created only when the owner presses "Add phone over internet",
/// 5 minute TTL, 5 wrong attempts then destroyed, single use. The bytes are wiped as soon as the slot dies.</summary>
internal sealed class RemoteCodeSlot
{
    public const int MaxWrongAttempts = 5;
    private readonly object _sync = new();
    private byte[]? _code;
    private int _wrong;
    private bool _used;

    public long ExpiresAtUnixMs { get; }

    public RemoteCodeSlot(string canonicalCode, long expiresAtUnixMs)
    {
        _code = Encoding.ASCII.GetBytes(canonicalCode);
        ExpiresAtUnixMs = expiresAtUnixMs;
    }

    public bool IsAlive(long nowMs)
    {
        lock (_sync)
        {
            ExpireLocked(nowMs);
            return _code is not null;
        }
    }

    /// <summary>Constant-time compare. A wrong code counts; the fifth wrong code (or expiry, or prior success) destroys the slot,
    /// after which even the right code is refused.</summary>
    public RemoteCodeCheck Check(string presented, long nowMs)
    {
        lock (_sync)
        {
            ExpireLocked(nowMs);
            if (_code is null) return RemoteCodeCheck.Dead;
            var candidate = Encoding.UTF8.GetBytes(presented);
            var ok = CryptographicOperations.FixedTimeEquals(_code, candidate);
            CryptographicOperations.ZeroMemory(candidate);
            if (ok) return RemoteCodeCheck.Ok;
            if (++_wrong >= MaxWrongAttempts) WipeLocked();
            return RemoteCodeCheck.Wrong;
        }
    }

    /// <summary>Single use: true exactly once, and wipes the code.</summary>
    public bool TryConsume(long nowMs)
    {
        lock (_sync)
        {
            ExpireLocked(nowMs);
            if (_code is null || _used) return false;
            _used = true;
            WipeLocked();
            return true;
        }
    }

    public void Wipe() { lock (_sync) WipeLocked(); }

    private void ExpireLocked(long nowMs) { if (_code is not null && nowMs >= ExpiresAtUnixMs) WipeLocked(); }

    private void WipeLocked()
    {
        if (_code is not null) CryptographicOperations.ZeroMemory(_code);
        _code = null;
    }
}
