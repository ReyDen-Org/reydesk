using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Companion;

internal sealed record DeveloperTaskDefinition(string Id, string Label, string Executable, string WorkingDirectory, string[] Arguments, bool ShareOutput);
internal interface IDeveloperTaskProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync();
    void Kill();
}
internal sealed class DeveloperTaskService
{
    public static DeveloperTaskService Instance => Shared.Instance;
    private static class Shared { internal static readonly DeveloperTaskService Instance = new(); }
    private readonly object _sync = new();
    private readonly string _path;
    private bool _enabled;
    private long _configurationVersion;
    private readonly Func<DeveloperTaskDefinition, IDeveloperTaskProcess?> _start;
    private readonly List<DeveloperTaskDefinition> _tasks = new();
    private readonly Dictionary<string, Run> _runs = new();
    private sealed class Run
    {
        public required IDeveloperTaskProcess Process;
        public string Output = "";
        public int? ExitCode;
        public bool Running = true;
    }
    private sealed record Configuration(bool Enabled, DeveloperTaskDefinition[] Tasks);
    internal DeveloperTaskService(string? path = null, Func<DeveloperTaskDefinition, IDeveloperTaskProcess?>? start = null)
    {
        _start = start ?? Start;
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReyDesk", "developer-tasks.json");
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 256 * 1024) return;
            var saved = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(_path));
            if (saved is null) return;
            if (saved.Tasks is null) return;
            foreach (var task in saved.Tasks.Take(12)) if (Valid(task) && _tasks.All(t => t.Id != task.Id)) _tasks.Add(Clone(task));
            _enabled = saved.Enabled;
        }
        catch (Exception) { Console.WriteLine("[Developer] Local configuration could not be loaded."); }
    }
    public bool Enabled { get { lock (_sync) return _enabled; } }
    private static DeveloperTaskDefinition Clone(DeveloperTaskDefinition task) => task with { Arguments = task.Arguments.ToArray() };
    public DeveloperTaskDefinition[] Snapshot() { lock (_sync) return _tasks.Select(Clone).ToArray(); }
    private static readonly HashSet<string> CommandHosts = new(StringComparer.OrdinalIgnoreCase)
        { "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "wsl.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe" };
    private static bool Valid(DeveloperTaskDefinition? task) => task is not null && Guid.TryParseExact(task.Id, "N", out _) &&
        task.Label is { Length: > 0 and <= 80 } && !task.Label.Any(char.IsControl) &&
        task.Executable is { Length: > 0 and <= 1024 } && Path.IsPathFullyQualified(task.Executable) && !task.Executable.StartsWith(@"\\") &&
        Path.GetExtension(task.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) && !CommandHosts.Contains(Path.GetFileName(task.Executable)) && File.Exists(task.Executable) &&
        task.WorkingDirectory is { Length: > 0 and <= 1024 } && Path.IsPathFullyQualified(task.WorkingDirectory) && !task.WorkingDirectory.StartsWith(@"\\") && Directory.Exists(task.WorkingDirectory) &&
        task.Arguments is { Length: <= 16 } && task.Arguments.All(a => a is not null && a.Length <= 1024 && !a.Contains('\0') && !a.Contains('\r') && !a.Contains('\n'));
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(new Configuration(_enabled, _tasks.ToArray())));
        File.Move(_path + ".tmp", _path, true);
    }
    public bool SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            var before = _enabled; _enabled = enabled;
            try { Save(); if (before != enabled) _configurationVersion++; return true; } catch (Exception) { _enabled = before; return false; }
        }
    }
    public bool Add(string label, string absoluteExe, string folder, string[] args, bool shareOutput)
    {
        var task = new DeveloperTaskDefinition(Guid.NewGuid().ToString("N"), label.Trim(), absoluteExe, folder, args.ToArray(), shareOutput);
        if (!Valid(task)) return false;
        lock (_sync)
        {
            if (_tasks.Count >= 12) return false;
            _tasks.Add(task);
            try { Save(); return true; } catch (Exception) { _tasks.Remove(task); return false; }
        }
    }
    public bool Remove(string id)
    {
        lock (_sync)
        {
            if (_runs.TryGetValue(id, out var run) && run.Running) return false;
            var index = _tasks.FindIndex(t => t.Id == id); if (index < 0) return false;
            var task = _tasks[index]; _tasks.RemoveAt(index);
            try { Save(); _runs.Remove(id); return true; } catch (Exception) { _tasks.Insert(index, task); return false; }
        }
    }
    public string ListPublic()
    {
        lock (_sync)
        {
            var tasks = (_enabled ? _tasks : new List<DeveloperTaskDefinition>()).Select(t =>
            {
                _runs.TryGetValue(t.Id, out var run);
                return new { id = t.Id, label = t.Label, running = run?.Running == true, exitCode = run?.ExitCode,
                    output = t.ShareOutput && run is not null ? Sanitize(run.Output) : "" };
            }).ToList();
            string Serialize() => JsonSerializer.Serialize(new { enabled = _enabled, tasks }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var json = Serialize();
            while (json.Length > 8192 && tasks.Count > 0) { tasks.RemoveAt(tasks.Count - 1); json = Serialize(); }
            return json;
        }
    }
    // Best-effort redaction of common credential-bearing lines. Sharing remains an
    // explicit local per-task opt-in because arbitrary program output may be private.
    internal static string Sanitize(string output)
    {
        var clean = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        clean = new string(clean.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray());
        return string.Join('\n', clean.Split('\n').Select(line => Regex.IsMatch(line, @"(?i)(password|passwd|secret|token|authorization|api.?key|connectionstring)") ? "[redacted credential line]" : line)).TakeLast(4096).Aggregate(new StringBuilder(), (b,c) => b.Append(c)).ToString();
    }
    public async Task<CompanionResult> InvokeAsync(string id, bool stop, Func<DeveloperTaskDefinition, bool, Task<bool>> confirm, Func<Action, bool> authorized)
    {
        DeveloperTaskDefinition? definition;
        Run? expectedRun; long configurationVersion;
        lock (_sync)
        {
            definition = _enabled ? _tasks.FirstOrDefault(t => t.Id == id) : null;
            _runs.TryGetValue(id, out expectedRun);
            configurationVersion = _configurationVersion;
        }
        if (definition is null || !Valid(definition)) return new() { Detail = "Developer Mode is disabled or task is not registered." };
        if (!await confirm(Clone(definition), stop)) return new() { Detail = "Task action was not approved on the PC." };
        var success = false;
        try
        {
            if (!authorized(() =>
            {
                lock (_sync)
                {
                    if (!_enabled || configurationVersion != _configurationVersion || !_tasks.Any(t => ReferenceEquals(t, definition)) || !Valid(definition)) return;
                    _runs.TryGetValue(id, out var existing);
                    if (stop)
                    {
                        if (existing?.Running != true || !ReferenceEquals(existing, expectedRun)) return;
                        existing.Process.Kill(); success = true; return;
                    }
                    if (existing?.Running == true) return;
                    var process = _start(Clone(definition));
                    if (process is null) return;
                    var run = new Run { Process = process }; _runs[id] = run;
                    _ = ObserveAsync(run); success = true;
                }
            })) return new() { Detail = "Connection permission expired." };
            return new() { Success = success, Detail = success ? (stop ? "Stop requested for the owned task process." : "Registered task started; refresh for actual status.") : "Task state changed or action could not run." };
        }
        catch (Exception) { return new() { Detail = "Task could not be started or stopped." }; }
    }
    private sealed class OwnedProcess(Process process) : IDeveloperTaskProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync() => process.WaitForExitAsync();
        public void Kill() => process.Kill(entireProcessTree: true);
        public void Dispose() => process.Dispose();
    }
    private static IDeveloperTaskProcess? Start(DeveloperTaskDefinition definition)
    {
        var info = new ProcessStartInfo(definition.Executable) { WorkingDirectory = definition.WorkingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in definition.Arguments) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        try { if (process.Start()) return new OwnedProcess(process); }
        catch { process.Dispose(); throw; }
        process.Dispose(); return null;
    }
    private async Task ObserveAsync(Run run)
    {
        async Task ReadAsync(TextReader reader)
        {
            var buffer = new char[256]; int count;
            var line = new StringBuilder(); bool oversized = false;
            void Commit()
            {
                var value = oversized ? "[oversized output line omitted]" : Sanitize(line.ToString());
                lock (_sync) { run.Output += value + "\n"; if (run.Output.Length > 8192) run.Output = run.Output[^8192..]; }
                line.Clear(); oversized = false;
            }
            while ((count = await reader.ReadAsync(buffer)) > 0)
                for (var i = 0; i < count; i++)
                {
                    var c = buffer[i];
                    if (c == '\n') Commit();
                    else if (!oversized) { if (line.Length >= 4096) { oversized = true; line.Clear(); } else line.Append(c); }
                }
            if (line.Length > 0 || oversized) Commit();
        }
        try
        {
            await Task.WhenAll(ReadAsync(run.Process.StandardOutput), ReadAsync(run.Process.StandardError), run.Process.WaitForExitAsync());
            lock (_sync) run.ExitCode = run.Process.ExitCode;
        }
        catch (Exception) { lock (_sync) run.Output = "Task output/status unavailable."; }
        finally { lock (_sync) run.Running = false; run.Process.Dispose(); }
    }
}
