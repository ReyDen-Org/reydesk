using Google.Protobuf;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Companion;

int count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
CompanionRequest Request(string op, string id = "request") => new() { Operation = op, RequestId = id };
foreach (var operation in new[] { "desktop", "control", "play_pause", "next", "previous", "volume_up", "volume_down", "mute", "lock", "clipboard_get", "clipboard_set" })
    Check(new CompanionPolicy().Accept(Request(operation), true, 10000), $"phone allowlist {operation}");
foreach (var operation in new[] { "powershell", "cmd", "file_delete", "open_path", "shutdown", "phone_action", "" })
    Check(!new CompanionPolicy().Accept(Request(operation), true, 10000), $"reject arbitrary operations {operation}");
foreach (var operation in new[] { "phone_action", "phone_dismiss", "phone_sync", "phone_pause", "phone_resume" })
    Check(new CompanionPolicy().Accept(Request(operation), false, 10000), $"PC allowlist {operation}");
Check(!new CompanionPolicy().Accept(Request("lock"), false, 10000), "direction separation");
var policy = new CompanionPolicy();
Check(policy.Accept(Request("lock"), true, 10000), "first request");
Check(!policy.Accept(Request("lock"), true, 12000), "replay rejected across windows");
Check(!new CompanionPolicy().Accept(Request("lock", ""), true, 10000), "empty ID");
Check(!new CompanionPolicy().Accept(Request("lock", new string('x', 65)), true, 10000), "oversized ID");
var huge = Request("clipboard_set"); huge.Text = new string('x', 8193);
Check(!new CompanionPolicy().Accept(huge, true, 10000), "clipboard length");
policy = new CompanionPolicy();
for (int i=0;i<20;i++) Check(policy.Accept(Request("mute", i.ToString()), true, 10000), "bounded request accepted");
Check(!policy.Accept(Request("mute", "21"), true, 10000), "rate limit");
Check(policy.Accept(Request("mute", "22"), true, 11000), "rate window recovery");
Check(!CompanionPolicy.ValidEvent(new PhoneHubEvent { Kind="unknown" }), "unknown event");
Check(!CompanionPolicy.ValidEvent(new PhoneHubEvent { Kind="upsert" }), "missing notification");
var notification = new PhoneNotification { Id="opaque", AppName="Messages", PackageName="example.messages", Title="سلام 👋", Text="متن / user@example", Revision="r1" };
notification.Actions.Add(new PhoneAction { Id="r1:a0", Label="Reply", AcceptsReply=true, AllowsFreeForm=true });
var envelope = new Envelope { ProtocolVersion=1, SessionId="session", PhoneHubEvent=new PhoneHubEvent { Kind="upsert", Notification=notification } };
var parsed = Envelope.Parser.ParseFrom(envelope.ToByteArray());
Check(parsed.SessionId == "session" && parsed.PhoneHubEvent.Notification.Text == notification.Text, "Unicode protocol roundtrip");
Check(CompanionPolicy.ValidEvent(parsed.PhoneHubEvent), "valid notification");
notification.IconPng = ByteString.CopyFrom(new byte[16385]);
Check(!CompanionPolicy.ValidEvent(envelope.PhoneHubEvent), "oversized icon");
notification.IconPng = ByteString.Empty;
notification.Text = new string('x', 4097);
Check(!CompanionPolicy.ValidEvent(envelope.PhoneHubEvent), "oversized preview");
notification.Text="";
for (int i=0;i<8;i++) notification.Actions.Add(new PhoneAction());
Check(!CompanionPolicy.ValidEvent(envelope.PhoneHubEvent), "action budget");
Check(CompanionPolicy.ValidEvent(new PhoneHubEvent { Kind="clear" }), "revocation clear");
Check(CompanionPolicy.ValidEvent(new PhoneHubEvent { Kind="snapshot_start" }), "snapshot reset");
Check(!CompanionPolicy.ValidEvent(new PhoneHubEvent { Kind="removed", RemovedId="" }), "invalid removal");
Check(CompanionPolicy.ValidEvent(new PhoneHubEvent { Kind="removed", RemovedId="opaque" }), "removal");
Check(new CompanionPolicy().Accept(Request("list_actions"), true, 10000), "bounded action discovery allowed");
var approved = Request("approved_action"); approved.ActionId = "local-id";
Check(new CompanionPolicy().Accept(approved, true, 10000), "registered action ID request allowed");
Check(!new CompanionPolicy().Accept(approved, false, 10000), "action invocation is phone to PC only");
Console.WriteLine($"PASS {count} companion security/protocol checks");
