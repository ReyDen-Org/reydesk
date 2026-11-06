namespace ReyDesk.Windows.Rendezvous;

/// <summary>One canonical spelling of the rendezvous URL, identical on Windows and Android (T18 H1 step 3). The client
/// signs this exact string, so "wss://Host/v1/ws/" and "wss://host/v1/ws" must not be two identities: trim, lower-case
/// scheme and host, drop the default port and trailing slashes, no credentials, query or fragment. wss:// only;
/// ws:// is accepted for dev hosts when cleartext is allowed (Debug builds).</summary>
internal static class RendezvousUrl
{
#if DEBUG
    public const bool DevCleartextDefault = true;
#else
    public const bool DevCleartextDefault = false;
#endif

    public static bool IsDevHost(string host) =>
        host is "127.0.0.1" or "localhost" or "10.0.2.2" || host.EndsWith(".local", StringComparison.Ordinal);

    public static bool TryNormalize(string? input, bool allowCleartextForDev, out string url)
    {
        url = "";
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        var scheme = uri.Scheme.ToLowerInvariant();
        var secure = scheme == "wss";
        if (!secure && !(scheme == "ws" && allowCleartextForDev && IsDevHost(host))) return false;
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        var path = uri.AbsolutePath.TrimEnd('/');
        url = $"{scheme}://{host}{port}{path}";
        return true;
    }

    public static string Require(string? input, bool allowCleartextForDev, string paramName) =>
        TryNormalize(input, allowCleartextForDev, out var url)
            ? url
            : throw new ArgumentException("Rendezvous URL must be wss://.", paramName);
}
