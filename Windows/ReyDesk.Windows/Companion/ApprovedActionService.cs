using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Companion;

internal sealed record ApprovedAction(string Id, string Label, string Profile, string Executable, string[] Arguments, string Shortcut)
{
    [JsonIgnore] public string TargetWindowTitle { get; init; } = "";
}

/// <summary>Local registrations only. Remote peers see safe metadata and may supply only an ID.</summary>
internal sealed class ApprovedActionService
{
    public static ApprovedActionService Instance => Shared.Instance;
    private static class Shared { internal static readonly ApprovedActionService Instance = new(); }
    private readonly object _sync = new();
    private readonly string _path;
    private readonly Func<ApprovedAction, nint, bool> _execute;
    private readonly List<ApprovedAction> _actions = new();
    private static readonly HashSet<string> CommandHosts = new(StringComparer.OrdinalIgnoreCase)
        { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "bash.exe", "sh.exe", "wsl.exe", "rundll32.exe", "regsvr32.exe" };
    private ApprovedActionService() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReyDesk", "approved-actions.json")) { }
    // An isolated path/executor also permits focused verification without user
    // settings, process launch or native keyboard input.
    internal ApprovedActionService(string path, Func<ApprovedAction, nint, bool>? execute = null)
    {
        _path = path;
        _execute = execute ?? Execute;
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 512 * 1024) return;
            var loaded = JsonSerializer.Deserialize<ApprovedAction[]>(File.ReadAllText(_path));
            if (loaded is null) return;
            foreach (var action in loaded.Take(24))
                if (Valid(action) && !_actions.Any(a => a.Id == action.Id)) _actions.Add(Clone(action));
        }
        catch (Exception) { Console.WriteLine("[Actions] Local registrations could not be loaded."); }
    }
    private static ApprovedAction Clone(ApprovedAction action) => action with { Arguments = action.Arguments.ToArray() };
    public ApprovedAction[] Snapshot() { lock (_sync) return _actions.Select(Clone).ToArray(); }
    public string ListPublic()
    {
        var items = Snapshot().Select(a => new { id = a.Id, label = a.Label, profile = a.Profile }).ToList();
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var json = JsonSerializer.Serialize(items, options);
        while (json.Length > 8192 && items.Count > 0) { items.RemoveAt(items.Count - 1); json = JsonSerializer.Serialize(items, options); }
        return json;
    }
    private static bool Plain(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl);
    private static bool Valid(ApprovedAction? action)
    {
        if (action is null || action.Executable is null || action.Shortcut is null || !Guid.TryParseExact(action.Id, "N", out _) || !Plain(action.Label, 80) || !Plain(action.Profile, 48) ||
            action.Arguments is null || action.Arguments.Length > 16 || action.Arguments.Any(a => a is null || a.Length > 1024 || a.Contains('\0') || a.Contains('\r') || a.Contains('\n'))) return false;
        if (action.Shortcut.Length > 0) return action.Executable.Length == 0 && action.Arguments.Length == 0 && TryShortcut(action.Shortcut, out _);
        return action.Executable.Length <= 1024 && Path.IsPathFullyQualified(action.Executable) &&
            !action.Executable.StartsWith(@"\\", StringComparison.Ordinal) &&
            Path.GetExtension(action.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            !CommandHosts.Contains(Path.GetFileName(action.Executable)) && File.Exists(action.Executable);
    }
    public bool Add(string label, string profile, string executable, string[] arguments, string shortcut)
    {
        var action = new ApprovedAction(Guid.NewGuid().ToString("N"), label.Trim(), profile.Trim(), executable, arguments.ToArray(), shortcut);
        if (!Valid(action)) return false;
        lock (_sync)
        {
            if (_actions.Count >= 24) return false;
            _actions.Add(action);
            try { Persist(); return true; }
            catch (Exception) { _actions.Remove(action); return false; }
        }
    }
    public bool Remove(string id)
    {
        lock (_sync)
        {
            var index = _actions.FindIndex(a => a.Id == id);
            if (index < 0) return false;
            var action = _actions[index]; _actions.RemoveAt(index);
            try { Persist(); return true; }
            catch (Exception) { _actions.Insert(index, action); return false; }
        }
    }
    public bool Update(string id, string label, string profile, string executable, string[] arguments, string shortcut)
    {
        var replacement = new ApprovedAction(id, label.Trim(), profile.Trim(), executable, arguments.ToArray(), shortcut);
        if (!Valid(replacement)) return false;
        lock (_sync)
        {
            var index = _actions.FindIndex(a => a.Id == id);
            if (index < 0) return false;
            var original = _actions[index]; _actions[index] = replacement;
            try { Persist(); return true; }
            catch (Exception) { _actions[index] = original; return false; }
        }
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_actions));
        File.Move(temp, _path, true);
    }
    public async Task<CompanionResult> InvokeAsync(string id, Func<ApprovedAction, Task<bool>> confirm, Func<Action, bool> authorized)
    {
        ApprovedAction? action;
        lock (_sync) action = _actions.FirstOrDefault(a => a.Id == id);
        if (action is null || !Valid(action)) return new() { Detail = "Action is unavailable." };
        var target = action.Shortcut.Length > 0 ? GetForegroundWindow() : 0;
        uint targetProcess = 0;
        if (target != 0) GetWindowThreadProcessId(target, out targetProcess);
        var title = new StringBuilder(257);
        if (target != 0) GetWindowText(target, title, title.Capacity);
        if (!await confirm(Clone(action) with { TargetWindowTitle = title.ToString() })) return new() { Detail = "Action was not approved on the PC." };
        var started = false;
        try
        {
            lock (_sync)
            {
                // Removed/changed registrations cannot survive an open consent dialog.
                if (!_actions.Any(a => ReferenceEquals(a, action)) || !Valid(action)) return new() { Detail = "Action is no longer registered." };
                if (!authorized(() => {
                    // HWND values may be recycled while approval is open.
                    if (action.Shortcut.Length > 0 && (targetProcess == 0 ||
                        GetWindowThreadProcessId(target, out var currentProcess) == 0 || currentProcess != targetProcess)) return;
                    started = _execute(Clone(action), target);
                })) return new() { Detail = "Connection permission expired." };
            }
            return new() { Success = started, Detail = started ? (action.Shortcut.Length > 0 ? "Shortcut sent; application response may vary." : "Application started.") : "Action could not start or the target application is unavailable." };
        }
        catch (Exception) { return new() { Detail = "Action could not start." }; }
    }

    private static bool Execute(ApprovedAction action, nint target)
    {
        if (action.Shortcut.Length > 0)
        {
            if (target == 0 || !IsWindow(target) || !SetForegroundWindow(target) || GetForegroundWindow() != target) return false;
            return SendShortcut(action.Shortcut);
        }
        var info = new ProcessStartInfo(action.Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(action.Executable)! };
        foreach (var argument in action.Arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        return process is not null;
    }

    private static bool TryShortcut(string shortcut, out ushort[] keys)
    {
        keys = Array.Empty<ushort>();
        var parts = shortcut.Split('+');
        if (parts.Length is < 2 or > 4 || parts[^1].Length != 1 || parts[^1][0] is < 'A' or > 'Z') return false;
        var modifiers = new List<ushort>();
        foreach (var part in parts[..^1])
        {
            ushort key = part switch { "Ctrl" => 0x11, "Shift" => 0x10, "Alt" => 0x12, _ => 0 };
            if (key == 0 || modifiers.Contains(key)) return false;
            modifiers.Add(key);
        }
        modifiers.Add(parts[^1][0]); keys = modifiers.ToArray(); return true;
    }
    private static bool SendShortcut(string shortcut)
    {
        if (!TryShortcut(shortcut, out var keys)) return false;
        var inputs = keys.Select(key => Key(key, false)).Concat(keys.Reverse().Select(key => Key(key, true))).ToArray();
        var count = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (count != inputs.Length)
        {
            var release = keys.Reverse().Select(key => Key(key, true)).ToArray();
            SendInput((uint)release.Length, release, Marshal.SizeOf<NativeInput>());
        }
        return count == inputs.Length;
    }
    private static NativeInput Key(ushort key, bool up) => new() { Type = 1, Value = new InputValue { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0 } } };
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputValue Value; }
    [StructLayout(LayoutKind.Explicit)] private struct InputValue { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
