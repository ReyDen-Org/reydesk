using ReyDesk.Windows.Companion;
using System.Text.Json;

var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var directory = Path.Combine(Path.GetTempPath(), "reydesk-developer-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var exe = Path.Combine(directory, "fixed-app.exe"); File.WriteAllBytes(exe, []);
    var path = Path.Combine(directory, "tasks.json");
    var runs = new List<FakeProcess>();
    var service = new DeveloperTaskService(path, task =>
    {
        var process = new FakeProcess("hello\nAPI_KEY=do-not-share\n" + new string('x', 6000) + "\nfinished\n", "stderr\n");
        runs.Add(process); return process;
    });
    Check(!service.Enabled, "developer mode defaults disabled");
    Check(!service.Add("Task", "relative.exe", directory, [], false), "relative executable rejected");
    Check(!service.Add("Task", exe, "relative", [], false), "relative working directory rejected");
    Check(!service.Add("Task", exe, @"\\server\private", [], false), "network working directory rejected");
    Check(!service.Add("Task", exe, directory, ["bad\nargument"], false), "multiline argument rejected");
    Check(!service.Add("Task\nlabel", exe, directory, [], false), "multiline display name rejected");
    var shell = Path.Combine(directory, "cmd.exe"); File.WriteAllBytes(shell, []);
    Check(!service.Add("Task", shell, directory, [], false), "shell executable rejected");
    var taskArguments = new[] { "private-fixed-argument" };
    Check(service.Add("Private output", exe, directory, taskArguments, false), "valid fixed registration accepted");
    var id = service.Snapshot().Single().Id;
    taskArguments[0] = "changed"; var snapshot = service.Snapshot(); snapshot[0].Arguments[0] = "changed again";
    Check(service.Snapshot().Single().Arguments[0] == "private-fixed-argument", "registration arguments copied defensively");
    var prompts = 0;
    var disabled = await service.InvokeAsync(id, false, (_, _) => { prompts++; return Task.FromResult(true); }, action => { action(); return true; });
    Check(!disabled.Success && prompts == 0 && runs.Count == 0, "disabled mode cannot prompt or start");
    using (var hidden = JsonDocument.Parse(service.ListPublic())) Check(hidden.RootElement.GetProperty("tasks").GetArrayLength() == 0, "disabled mode hides all task metadata");
    service.SetEnabled(true);
    var denied = await service.InvokeAsync(id, false, (_, _) => { prompts++; return Task.FromResult(false); }, action => { action(); return true; });
    Check(!denied.Success && runs.Count == 0, "declined start cannot execute");
    var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = service.InvokeAsync(id, false, (_, _) => consent.Task, action => { action(); return true; });
    service.SetEnabled(false); service.SetEnabled(true); consent.SetResult(true);
    Check(!(await pending).Success && runs.Count == 0, "disable then re-enable invalidates outstanding approval");
    var ownerCurrent = true;
    var ownerConsent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ownerPending = service.InvokeAsync(id, false, (_, _) => ownerConsent.Task, action => { if (!ownerCurrent) return false; action(); return true; });
    ownerCurrent = false; ownerConsent.SetResult(true);
    Check(!(await ownerPending).Success && runs.Count == 0, "owner revocation while approval awaits prevents start");
    var lockOutside = await service.InvokeAsync(id, false, (_, _) => Task.FromResult(true), _ =>
    {
        var change = Task.Run(() => service.SetEnabled(false));
        Check(change.Wait(TimeSpan.FromSeconds(2)), "authorization callback runs outside configuration lock");
        return false;
    });
    Check(!lockOutside.Success && runs.Count == 0, "authorization denial prevents execution");
    service.SetEnabled(true);
    async Task<bool> Invoke(string taskId, bool stop) => (await service.InvokeAsync(taskId, stop,
        (_, _) => { prompts++; return Task.FromResult(true); }, action => { action(); return true; })).Success;
    Check(await Invoke(id, false) && runs.Count == 1, "approved fixed task starts through isolated fake factory");
    Check(!await Invoke(id, false) && runs.Count == 1, "duplicate start cannot spawn another process");
    Check(!service.Remove(id), "running owned task cannot be removed");
    using (var status = JsonDocument.Parse(service.ListPublic()))
    {
        var task = status.RootElement.GetProperty("tasks")[0];
        Check(task.GetProperty("running").GetBoolean() && task.GetProperty("output").GetString() == "", "per-task default output opt-out hides captured text");
        Check(task.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "exitCode", "id", "label", "output", "running" }), "remote metadata excludes executable working directory and arguments");
    }
    var stopConsent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var oldStop = service.InvokeAsync(id, true, (_, _) => stopConsent.Task, action => { action(); return true; });
    runs[0].Finish(7);
    await WaitUntil(() => !IsRunning(service, id));
    Check(await Invoke(id, false) && runs.Count == 2, "completed task can start a new owned run");
    stopConsent.SetResult(true);
    Check(!(await oldStop).Success && runs[1].KillCalls == 0, "late stop approval cannot kill replacement run");
    Check(await Invoke(id, true) && runs[1].KillCalls == 1, "approved stop targets only currently owned run");
    await WaitUntil(() => !IsRunning(service, id));
    Check(service.Remove(id), "completed registration can be removed");
    Check(service.Add("Shared output", exe, directory, [], true), "explicit per-task output sharing accepted");
    var sharedId = service.Snapshot().Single().Id;
    Check(await Invoke(sharedId, false), "shared-output task starts via fake factory");
    using (var shared = JsonDocument.Parse(service.ListPublic()))
    {
        var output = shared.RootElement.GetProperty("tasks")[0].GetProperty("output").GetString()!;
        Check(output.Contains("hello") && output.Contains("stderr") && output.Contains("[redacted credential line]") && !output.Contains("do-not-share"), "stdout stderr credential lines redacted before sharing");
        Check(output.Contains("[oversized output line omitted]") && output.Length <= 4096, "oversized program output omitted and tail bounded");
        Check(service.ListPublic().Length <= 8192, "public task response remains bounded");
    }
    runs[^1].Finish(3); await WaitUntil(() => !IsRunning(service, sharedId));
    using (var exited = JsonDocument.Parse(service.ListPublic())) Check(exited.RootElement.GetProperty("tasks")[0].GetProperty("exitCode").GetInt32() == 3, "actual process exit code published after completion");
    var sanitized = DeveloperTaskService.Sanitize("\u001b[31mred\u001b[0m\npassword=hidden\n" + new string('z', 10000));
    Check(!sanitized.Contains('\u001b') && !sanitized.Contains("hidden") && sanitized.Length <= 4096, "sanitizer removes ANSI credential text and bounds output");
    File.WriteAllText(path, "{\"Enabled\":true,\"Tasks\":null}");
    Check(!new DeveloperTaskService(path, _ => throw new Exception()).Enabled, "malformed persisted configuration cannot enable mode");
    Console.WriteLine($"{checks} developer-task checks passed; no processes started");
}
finally { Directory.Delete(directory, true); }

static bool IsRunning(DeveloperTaskService service, string id)
{
    using var json = JsonDocument.Parse(service.ListPublic());
    return json.RootElement.GetProperty("tasks").EnumerateArray().Any(t => t.GetProperty("id").GetString() == id && t.GetProperty("running").GetBoolean());
}
static async Task WaitUntil(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (!condition()) await Task.Delay(10, timeout.Token);
}
sealed class FakeProcess(string stdout, string stderr) : IDeveloperTaskProcess
{
    private readonly TaskCompletionSource<bool> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TextReader StandardOutput { get; } = new StringReader(stdout);
    public TextReader StandardError { get; } = new StringReader(stderr);
    public int ExitCode { get; private set; }
    public int KillCalls { get; private set; }
    public Task WaitForExitAsync() => _exit.Task;
    public void Finish(int exitCode) { ExitCode = exitCode; _exit.TrySetResult(true); }
    public void Kill() { KillCalls++; Finish(-1); }
    public void Dispose() { StandardOutput.Dispose(); StandardError.Dispose(); }
}
