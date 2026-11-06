using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace ReyDesk.Windows.Settings;

/// <summary>
/// "Start with Windows" (spec §20/§46) via the standard per-user Run key - no
/// elevation needed, no Task Scheduler/service complexity, and it's removed
/// cleanly if the app is uninstalled by hand (the registry value just points at
/// a path that stops existing).
/// </summary>
internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReyDesk";
    private const string LegacyValueName = "ReyScreen";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string || key?.GetValue(LegacyValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return;
            key.SetValue(ValueName, $"\"{exePath}\"");
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
    }
}
