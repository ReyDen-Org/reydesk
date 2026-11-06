using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.PhoneHub;

/// <summary>Bounded, session-only active notification projection. Never writes message data to disk.</summary>
internal sealed class PhoneInbox
{
    public const int MaxItems = 200;
    private readonly Dictionary<string, PhoneNotification> _items = new();
    public IReadOnlyCollection<PhoneNotification> Items => _items.Values;
    public PhoneHubState? State { get; private set; }
    public bool Syncing { get; private set; }
    public DateTimeOffset? LastSeen { get; private set; }

    public bool Apply(PhoneHubEvent update)
    {
        if (update.Kind.Length > 24) return false;
        switch (update.Kind)
        {
            case "status":
                if (update.State is null || update.State.PhoneName.Length > 256 || update.State.Detail.Length > 1024) return false;
                State = update.State.Clone();
                if (!State.AccessGranted || !State.Enabled) _items.Clear();
                break;
            case "snapshot_start": _items.Clear(); Syncing = true; break;
            case "snapshot_end": Syncing = false; break;
            case "clear": _items.Clear(); Syncing = false; break;
            case "removed":
                if (update.RemovedId.Length > 256) return false;
                _items.Remove(update.RemovedId);
                break;
            case "upsert":
                var item = update.Notification;
                if (State is not { AccessGranted: true, Enabled: true } || item is null ||
                    string.IsNullOrEmpty(item.Id) || item.Id.Length > 256 || item.PackageName.Length > 256 ||
                    item.AppName.Length > 256 || item.Title.Length > 2048 || item.Text.Length > 8192 ||
                    item.Revision.Length > 128 || item.GroupKey.Length > 512 || item.Category.Length > 64 ||
                    !SafeIcon(item.IconPng.Span) || item.Actions.Count > 12 ||
                    item.Actions.Any(a => a.Id.Length > 256 || a.Label.Length > 256 ||
                        a.ReplyChoices.Count > 10 || a.ReplyChoices.Any(c => c.Length > 1024))) return false;
                var safe = item.Clone();
                // A redacted event must never leave a stale title, action, or text in the projection.
                if (safe.Redacted) { safe.Title = ""; safe.Text = ""; safe.Actions.Clear(); }
                _items[item.Id] = safe;
                if (_items.Count > MaxItems)
                {
                    var oldest = _items.Values.OrderBy(n => n.PostedAtUnixMs).ThenBy(n => n.Id, StringComparer.Ordinal).First();
                    _items.Remove(oldest.Id);
                }
                break;
            default: return false;
        }
        LastSeen = DateTimeOffset.UtcNow;
        return true;
    }

    public void Clear()
    {
        _items.Clear(); State = null; Syncing = false; LastSeen = null;
    }

    private static bool SafeIcon(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return true;
        if (bytes.Length is < 24 or > 32768 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        return width is > 0 and <= 256 && height is > 0 and <= 256;
    }
}
