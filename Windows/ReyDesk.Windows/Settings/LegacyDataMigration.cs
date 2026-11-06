using System;
using System.IO;

namespace ReyDesk.Windows.Settings;

/// <summary>One-time copy of the pre-rename data folder (%LOCALAPPDATA%/ReyScreen) to
/// %LOCALAPPDATA%/ReyDesk. Copy only: the old folder is never moved or deleted.
/// DPAPI blobs are per-user, so copied identity/trust files stay valid.</summary>
internal static class LegacyDataMigration
{
    private const string LegacyFolder = "ReyScreen";
    private const string CurrentFolder = "ReyDesk";

    /// <summary>Returns a log line when a copy was attempted, otherwise null.</summary>
    internal static string? Run()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var legacy = Path.Combine(root, LegacyFolder);
            var current = Path.Combine(root, CurrentFolder);
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return null;
            CopyDirectory(legacy, current);
            return $"[Migration] Copied legacy data folder '{legacy}' to '{current}' (old folder kept).";
        }
        catch (Exception ex)
        {
            return $"[Migration] Legacy data copy failed: {ex}";
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
