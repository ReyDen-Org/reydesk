using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReyDesk.Windows.Updates;

internal enum UpdateCheckStatus
{
    NotConfigured, InvalidRepository, UpdateAvailable, UpToDate, SourceUnavailable,
    RateLimited, AccessDenied, HttpError, NetworkError, TimedOut, Cancelled, InvalidRelease,
}

internal sealed record UpdateCheckResult(UpdateCheckStatus Status, string? LatestVersion = null,
    Uri? ReleaseUri = null, DateTimeOffset? PublishedAt = null, string? Notes = null);

/// <summary>Reads the reyden.ir release manifest (latest.json). Never downloads or runs an installer:
/// the result only carries the page link, which the UI opens in the browser.</summary>
internal sealed class UpdateChecker
{
    public const string ManifestUrl = "https://reyden.ir/dl/reydesk/latest.json";
    public const string PageUrl = "https://reyden.ir/reydesk";
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = 1024 * 1024,
    };
    private readonly HttpClient _client;

    public static Version CurrentVersion { get; } = NormalizeVersion(
        typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(1, 0, 0));
    public static string DisplayVersion => CurrentVersion.Revision == 0
        ? CurrentVersion.ToString(3) : CurrentVersion.ToString(4);

    public UpdateChecker(HttpClient? client = null) => _client = client ?? SharedClient;

    /// <param name="language">"en" or "fa": picks the release notes language.</param>
    public async Task<UpdateCheckResult> CheckAsync(string language = "en",
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
            request.Headers.UserAgent.ParseAdd($"ReyDesk/{DisplayVersion}");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateCheckStatus.SourceUnavailable);
            if ((int)response.StatusCode == 429) return new(UpdateCheckStatus.RateLimited);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                return new(UpdateCheckStatus.AccessDenied);
            if (!response.IsSuccessStatusCode) return new(UpdateCheckStatus.HttpError);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseManifest(json, CurrentVersion, language);
        }
        catch (OperationCanceledException)
        {
            return new(cancellationToken.IsCancellationRequested ? UpdateCheckStatus.Cancelled : UpdateCheckStatus.TimedOut);
        }
        catch (HttpRequestException) { return new(UpdateCheckStatus.NetworkError); }
        catch (JsonException) { return new(UpdateCheckStatus.InvalidRelease); }
    }

    internal static UpdateCheckResult ParseManifest(string json, Version currentVersion, string language = "en")
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var tag) || tag.ValueKind != JsonValueKind.String
            || !TryParseReleaseVersion(tag.GetString(), out var version))
            return new(UpdateCheckStatus.InvalidRelease);
        var releaseUri = new Uri(PageUrl);
        if (root.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.String
            && TryGetPageUri(page.GetString(), out var pageUri) && pageUri is not null) releaseUri = pageUri;
        else if (root.TryGetProperty("page", out _)) return new(UpdateCheckStatus.InvalidRelease);

        DateTimeOffset? publishedAt = root.TryGetProperty("released", out var date)
            && date.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(date.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate) ? parsedDate : null;
        string? notes = null;
        if (root.TryGetProperty("notes", out var n))
        {
            if (n.ValueKind == JsonValueKind.String) notes = n.GetString();
            else if (n.ValueKind == JsonValueKind.Object)
            {
                if (n.TryGetProperty(language, out var l) && l.ValueKind == JsonValueKind.String) notes = l.GetString();
                else if (n.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String) notes = en.GetString();
            }
        }
        if (notes?.Length > 2000) notes = notes[..2000];
        return new(version > NormalizeVersion(currentVersion) ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate,
            tag.GetString(), releaseUri, publishedAt, notes);
    }

    internal static bool TryParseReleaseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (tag is null || !Regex.IsMatch(tag, @"\Av?\d+\.\d+\.\d+(?:\.\d+)?\z", RegexOptions.IgnoreCase)
            || !Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return false;
        version = NormalizeVersion(parsed);
        return true;
    }

    // Only https://reyden.ir pages are ever opened from the manifest.
    internal static bool TryGetPageUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps || candidate.Host != "reyden.ir"
            || !candidate.IsDefaultPort || candidate.UserInfo.Length != 0
            || candidate.Fragment.Length != 0) return false;
        uri = candidate;
        return true;
    }

    private static Version NormalizeVersion(Version version) => new(version.Major, version.Minor,
        Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
