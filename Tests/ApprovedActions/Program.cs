using ReyDesk.Windows.Companion;
using System.Text.Json;

var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var directory = Path.Combine(Path.GetTempPath(), "reydesk-approved-actions-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var executable = Path.Combine(directory, "local-app.exe"); File.WriteAllBytes(executable, []);
    var path = Path.Combine(directory, "actions.json");
    var executions = 0; ApprovedAction? executed = null;
    // This executor is the only execution endpoint in these tests. It cannot
    // launch processes or send native input; even approved calls remain inert.
    var service = new ApprovedActionService(path, (action, _) => { executions++; executed = action; return true; });
    Check(!service.Add("Bad", "Profile", "relative.exe", [], ""), "relative executable rejected");
    Check(!service.Add("Bad", "Profile", @"\\server\app.exe", [], ""), "network executable rejected");
    Check(!service.Add("Bad\nLabel", "Profile", executable, [], ""), "control characters in metadata rejected");
    Check(!service.Add("Bad", "Profile", executable, ["a\nb"], ""), "multiline arguments rejected");
    Check(!service.Add("Bad", "Profile", executable, [new string('x', 1025)], ""), "oversized argument rejected");
    Check(!service.Add("Bad", "Profile", executable, new string[17], ""), "excess arguments rejected");
    Check(!service.Add("Bad", "Profile", "", [], "Ctrl+Ctrl+A"), "duplicate shortcut modifier rejected");
    Check(!service.Add("Bad", "Profile", "", [], "Win+R"), "unsupported shortcut rejected");
    Check(!service.Add("Bad", "Profile", executable, [], "Ctrl+A"), "mixed executable and shortcut rejected");
    var shell = Path.Combine(directory, "powershell.exe"); File.WriteAllBytes(shell, []);
    Check(!service.Add("Bad", "Profile", shell, [], ""), "command interpreter executable rejected");
    var arguments = new[] { "private argument", "quoted argument with spaces" };
    Check(service.Add("My action", "Work", executable, arguments, ""), "valid fixed executable registration accepted");
    arguments[0] = "mutated";
    var selected = service.Snapshot().Single(); var id = selected.Id;
    selected.Arguments[0] = "snapshot mutation";
    Check(service.Snapshot().Single().Arguments[0] == "private argument", "registrations own defensive argument copies");
    using (var metadata = JsonDocument.Parse(service.ListPublic()))
    {
        var item = metadata.RootElement[0];
        Check(item.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "id", "label", "profile" }), "public metadata exposes only safe ID label profile fields");
        Check(!service.ListPublic().Contains(executable) && !service.ListPublic().Contains("private argument"), "public metadata excludes local path and arguments");
    }
    var confirmations = 0;
    var denied = await service.InvokeAsync(id, _ => { confirmations++; return Task.FromResult(false); }, action => { action(); return true; });
    Check(!denied.Success && executions == 0, "declining one invocation never executes");
    var revoked = await service.InvokeAsync(id, _ => { confirmations++; return Task.FromResult(true); }, _ => false);
    Check(!revoked.Success && executions == 0, "revoked owner gate prevents execution after approval");
    for (var i = 0; i < 2; i++)
    {
        var result = await service.InvokeAsync(id, action => { confirmations++; action.Arguments[0] = "dialog mutation"; return Task.FromResult(true); }, action => { action(); return true; });
        Check(result.Success && executed!.Arguments[0] == "private argument", "each approved invocation uses immutable local registration " + i);
    }
    Check(confirmations == 4 && executions == 2, "approval required separately for every invocation");
    var changed = await service.InvokeAsync(id, _ => { service.Update(id, "Changed", "Work", executable, ["new"], ""); return Task.FromResult(true); }, action => { action(); return true; });
    Check(!changed.Success && executions == 2, "registration edit during approval invalidates consent");
    var removed = await service.InvokeAsync(id, _ => { service.Remove(id); return Task.FromResult(true); }, action => { action(); return true; });
    Check(!removed.Success && executions == 2, "registration removal during approval invalidates consent");
    var unknownPrompts = 0;
    var missing = await service.InvokeAsync(Guid.NewGuid().ToString("N"), _ => { unknownPrompts++; return Task.FromResult(true); }, action => { action(); return true; });
    Check(!missing.Success && unknownPrompts == 0 && executions == 2, "unknown remote ID cannot prompt or execute");
    Check(service.Add("Pending", "Work", executable, [], ""), "pending approval registration accepted");
    var pendingId = service.Snapshot().Single().Id;
    var pendingApproval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ownerCurrent = true;
    var pending = service.InvokeAsync(pendingId, _ => pendingApproval.Task, action => { if (!ownerCurrent) return false; action(); return true; });
    ownerCurrent = false; pendingApproval.SetResult(true);
    Check(!(await pending).Success && executions == 2, "revoke while approval awaits cannot run after late confirmation");
    service.Remove(pendingId);
    Check(new ApprovedActionService(path, (_, _) => false).Snapshot().Length == 0, "remove persists to isolated settings");
    var valid = new ApprovedAction(Guid.NewGuid().ToString("N"), "Restored", "Profile", executable, [], "");
    File.WriteAllText(path, JsonSerializer.Serialize(new object?[] { null, valid, valid, valid with { Id = "invalid" }, valid with { Id = Guid.NewGuid().ToString("N"), Arguments = null! } }));
    var loaded = new ApprovedActionService(path, (_, _) => false);
    Check(loaded.Snapshot().Length == 1, "malformed and duplicate persisted registrations filtered");
    File.WriteAllText(path, "{invalid json");
    Check(new ApprovedActionService(path, (_, _) => false).Snapshot().Length == 0, "corrupt persisted configuration fails closed");
    Console.WriteLine($"{checks} approved-action checks passed; no process or input executed");
}
finally { Directory.Delete(directory, true); }
