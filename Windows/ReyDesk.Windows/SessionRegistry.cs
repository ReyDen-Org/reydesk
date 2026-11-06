using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReyDesk.Windows;

/// <summary>Identity-based ownership: late callbacks must not evict a replacement.
/// The coordinator serializes these operations with its session-state lock.</summary>
internal sealed class SessionRegistry<T> where T : class
{
    private readonly Dictionary<string, T> _current = new();
    public T? Replace(string id, T entry)
    {
        _current.TryGetValue(id, out var previous);
        _current[id] = entry;
        return previous;
    }
    public T? Get(string id) => _current.TryGetValue(id, out var entry) ? entry : null;
    public bool IsCurrent(string id, T entry) => ReferenceEquals(Get(id), entry);
    public bool Remove(string id, T entry) => IsCurrent(id, entry) && _current.Remove(id);
    public T[] Drain()
    {
        var entries = _current.Values.ToArray();
        _current.Clear();
        return entries;
    }
}

internal sealed class SessionTeardown(Action closeSession, Action closeConnection) : IDisposable
{
    private int _closed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { closeSession(); }
        finally { closeConnection(); }
    }
}
