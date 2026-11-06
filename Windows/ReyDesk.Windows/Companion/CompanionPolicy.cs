using System;
using System.Collections.Generic;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Companion;

// One instance per authenticated connection. IDs cannot be replayed, including
// failed actions; exhausting the bounded budget requires a new pairing session.
internal sealed class CompanionPolicy
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _window;
    private int _count;
    public static bool IsPhoneOperation(string operation) => operation is
        "phone_action" or "phone_dismiss" or "phone_sync" or "phone_pause" or "phone_resume";
    public bool Accept(CompanionRequest request, bool fromPhone, long nowMs)
    {
        if (request.RequestId.Length is < 1 or > 64 || request.Text.Length > 8192 ||
            request.NotificationId.Length > 256 || request.ActionId.Length > 256) return false;
        if (fromPhone ? request.Operation is not ("desktop" or "control" or "play_pause" or "next" or
            "previous" or "volume_up" or "volume_down" or "mute" or "lock" or "clipboard_get" or "clipboard_set" or "list_actions" or "approved_action" or "developer_status" or "developer_start" or "developer_stop")
            : !IsPhoneOperation(request.Operation)) return false;
        lock (_gate)
        {
            if (nowMs - _window >= 1000) { _window = nowMs; _count = 0; }
            if (_count >= 20 || _seen.Count >= 4096 || !_seen.Add(request.RequestId)) return false;
            _count++;
            return true;
        }
    }
    public static bool ValidEvent(PhoneHubEvent evt) => evt.Kind switch
    {
        "status" => evt.State is not null && evt.State.PhoneName.Length <= 128 && evt.State.Detail.Length <= 512,
        "snapshot_start" or "snapshot_end" or "clear" => true,
        "removed" => evt.RemovedId.Length is > 0 and <= 256,
        "upsert" => evt.Notification is { } n && n.Id.Length is > 0 and <= 256 &&
            n.PackageName.Length <= 256 && n.AppName.Length <= 128 && n.Title.Length <= 512 &&
            n.Text.Length <= 4096 && n.GroupKey.Length <= 256 && n.Revision.Length <= 64 && n.Category.Length <= 64 &&
            n.IconPng.Length <= 16384 && n.Actions.Count <= 8 && System.Linq.Enumerable.All(n.Actions,
                a => a.Id.Length <= 256 && a.Label.Length <= 128 && a.ReplyChoices.Count <= 16 &&
                    System.Linq.Enumerable.All(a.ReplyChoices, s => s.Length <= 256)),
        _ => false,
    };
}
