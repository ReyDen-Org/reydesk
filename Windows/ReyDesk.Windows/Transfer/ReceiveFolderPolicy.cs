namespace ReyDesk.Windows.Transfer;

/// <summary>Only an owner-selected local folder; collisions always use CreateNew.</summary>
internal static class ReceiveFolderPolicy
{
    public static bool SafeName(string name)
    {
        if (!FileTransferEngine.SafeName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) return false;
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    public static FileStream CreateUnique(string folder, string name, out string createdPath)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder) || !SafeName(name)) throw new IOException("Receive folder unavailable.");
        var root = Path.GetFullPath(folder);
        for (var index = 0; index < 1000; index++)
        {
            var candidate = index == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}";
            var path = Path.Combine(root, candidate);
            try { var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); createdPath = path; return stream; }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("No unused receive filename available.");
    }
}
