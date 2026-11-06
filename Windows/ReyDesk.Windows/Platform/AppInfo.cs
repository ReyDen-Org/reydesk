using System.Reflection;

namespace ReyDesk.Windows.Platform;

/// <summary>The one place the app version comes from. The value is ReyDesk/VERSION, stamped into the assembly by the csproj.</summary>
internal static class AppInfo
{
    /// <summary>Plain version such as 1.0.0, without the git sha suffix.</summary>
    internal static string Version { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppInfo).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plain = info?.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(plain)) return plain;
        var v = assembly.GetName().Version;
        return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
