using ReyDesk.Protocol.V1;
using System.Security.Cryptography;
using System.Text;

namespace ReyDesk.Windows.PhoneHub;

// Session-only bounded duplicate suppression. Saturation suppresses alerts rather than replaying old IDs.
internal sealed class PhoneAlertGate
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private long? _last;
    public bool Observe(PhoneNotification? previous, PhoneNotification current, bool eligible, long now)
    {
        // Refreshes rotate native action handles; only newer, changed visible content is a new message.
        var meaningful = previous is null || (previous.Redacted == current.Redacted &&
            current.Revision != previous.Revision && current.PostedAtUnixMs > previous.PostedAtUnixMs &&
            (current.Redacted || current.Title != previous.Title || current.Text != previous.Text));
        var content = current.Redacted ? "" : current.Title + "\n" + current.Text;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(current.PostedAtUnixMs + "\n" + content)));
        return Observe(current.Id, fingerprint, eligible && meaningful, now);
    }
    public bool Observe(string id, string revision, bool eligible, long now)
    {
        if (id.Length is < 1 or > 256 || revision.Length is < 1 or > 128 || _seen.Count >= 2048 || !_seen.Add(id + ":" + revision)) return false;
        if (!eligible || (_last is { } last && now - last < 30_000)) return false;
        _last = now; return true;
    }
}
