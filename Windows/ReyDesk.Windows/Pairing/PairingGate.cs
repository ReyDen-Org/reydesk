namespace ReyDesk.Windows.Pairing;

/// <summary>Process-local attempt budget and atomic challenge consumption.</summary>
internal sealed class PairingGate
{
    public const int SasFailuresBeforePause = 3;
    public const long SasPauseMs = 5 * 60_000;

    private readonly object _sync = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ChallengeState> _challenges = new();
    private readonly Queue<long> _arrivals = new();
    private readonly Queue<long> _sasFailures = new();
    private long _sasPausedUntil;
    private readonly Dictionary<string, Queue<long>> _peerFailures = new();

    public const int PeerFailuresBeforeBlock = 5;
    public const long PeerWindowMs = 10 * 60_000;

    /// <summary>True while <paramref name="peer"/> (key fingerprint) has used its failed-attempt budget in the last 10 minutes.</summary>
    public bool IsPeerBlocked(string peer, long nowMs)
    {
        lock (_sync)
        {
            if (!_peerFailures.TryGetValue(peer, out var q)) return false;
            while (q.TryPeek(out var at) && nowMs - at >= PeerWindowMs) q.Dequeue();
            if (q.Count == 0) { _peerFailures.Remove(peer); return false; }
            return q.Count >= PeerFailuresBeforeBlock;
        }
    }

    public void RecordPeerFailure(string peer, long nowMs)
    {
        lock (_sync)
        {
            if (_peerFailures.Count >= 256)
                foreach (var stale in _peerFailures.Where(e => e.Value.Count == 0 || nowMs - e.Value.Peek() >= PeerWindowMs).Select(e => e.Key).ToList())
                    _peerFailures.Remove(stale);
            if (!_peerFailures.TryGetValue(peer, out var q)) _peerFailures[peer] = q = new Queue<long>();
            while (q.TryPeek(out var at) && nowMs - at >= PeerWindowMs) q.Dequeue();
            q.Enqueue(nowMs);
        }
    }

    public void ClearPeerFailures(string peer)
    {
        lock (_sync) _peerFailures.Remove(peer);
    }

    public bool AllowConnection(long nowMs)
    {
        lock (_sync)
        {
            while (_arrivals.TryPeek(out var at) && nowMs - at >= 60_000) _arrivals.Dequeue();
            if (_arrivals.Count >= 20) return false;
            _arrivals.Enqueue(nowMs);
            return true;
        }
    }

    /// <summary>Counts one attempt against a challenge object: a displayed offer (QR) or one connection (reconnect).</summary>
    public bool TryAttempt(object offer)
    {
        lock (_sync)
        {
            var state = _challenges.GetOrCreateValue(offer);
            return !state.Consumed && ++state.Attempts <= 5;
        }
    }

    public bool IsConsumed(object offer)
    {
        lock (_sync) return _challenges.TryGetValue(offer, out var state) && state.Consumed;
    }

    public bool TryConsume(object offer)
    {
        lock (_sync)
        {
            if (!_challenges.TryGetValue(offer, out var state) || state.Consumed || state.Attempts > 5) return false;
            state.Consumed = true;
            return true;
        }
    }

    /// <summary>A SAS the owner rejected, a mismatched commitment, or a phone that walked away before confirming.
    /// Three within five minutes pause every SAS method (M2, M4) for five minutes.</summary>
    public void RecordSasFailure(long nowMs)
    {
        lock (_sync)
        {
            while (_sasFailures.TryPeek(out var at) && nowMs - at >= SasPauseMs) _sasFailures.Dequeue();
            _sasFailures.Enqueue(nowMs);
            if (_sasFailures.Count >= SasFailuresBeforePause)
            {
                _sasPausedUntil = nowMs + SasPauseMs;
                _sasFailures.Clear();
            }
        }
    }

    public bool IsSasPaused(long nowMs)
    {
        lock (_sync) return nowMs < _sasPausedUntil;
    }

    private sealed class ChallengeState
    {
        public bool Consumed;
        public int Attempts;
    }
}
