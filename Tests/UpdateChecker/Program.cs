using System.Net;
using System.Text.Json;
using ReyDesk.Windows.Updates;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
string Manifest(string version = "1.1.0", string page = "https://reyden.ir/reydesk") => JsonSerializer.Serialize(new
{
    version, released = "2026-11-06", notes = new { en = "Release notes", fa = "یادداشت انتشار" },
    windows = new { url = "https://reyden.ir/dl/ReyDesk-Setup-" + version + ".exe", sha256 = "00" },
    android = new { url = "https://reyden.ir/dl/ReyDesk-" + version + ".apk", sha256 = "00" }, page,
});
var current = new Version(1, 0, 0);
Check(UpdateChecker.TryParseReleaseVersion("v1.2.3", out var parsed) && parsed == new Version(1, 2, 3, 0), "Stable semver normalized");
Check(UpdateChecker.TryParseReleaseVersion("1.2.3", out _), "Plain semver accepted");
Check(!UpdateChecker.TryParseReleaseVersion("1.2.3-beta", out _), "Prerelease version rejected");
Check(!UpdateChecker.TryParseReleaseVersion("latest", out _), "Unversioned value rejected");
Check(UpdateChecker.ParseManifest(Manifest(), current).Status == UpdateCheckStatus.UpdateAvailable, "New release");
Check(UpdateChecker.ParseManifest(Manifest("1.0.0"), current).Status == UpdateCheckStatus.UpToDate, "Equal versions");
Check(UpdateChecker.ParseManifest(Manifest("0.9.0"), current).Status == UpdateCheckStatus.UpToDate, "Newer local build never downgraded");
foreach (var url in new[] { "http://reyden.ir/reydesk", "https://reyden.ir.evil.test/reydesk", "https://evil.test/reydesk", "https://user@reyden.ir/reydesk", "https://reyden.ir:444/reydesk", "javascript:alert(1)" })
    Check(UpdateChecker.ParseManifest(Manifest(page: url), current).Status == UpdateCheckStatus.InvalidRelease, "Reject untrusted page URL " + url);
Check(UpdateChecker.ParseManifest("{}", current).Status == UpdateCheckStatus.InvalidRelease, "Missing required data");
Check(UpdateChecker.ParseManifest("[]", current).Status == UpdateCheckStatus.InvalidRelease, "Non-object manifest");
var result = UpdateChecker.ParseManifest(Manifest(), current);
Check(result.ReleaseUri?.AbsoluteUri == "https://reyden.ir/reydesk" && result.PublishedAt?.Year == 2026 && result.Notes == "Release notes", "Manifest metadata (en notes)");
Check(UpdateChecker.ParseManifest(Manifest(), current, "fa").Notes == "یادداشت انتشار", "Persian notes");
Check(UpdateChecker.ParseManifest(Manifest(), current, "de").Notes == "Release notes", "Unknown language falls back to en");

var handler = new FakeHandler();
using var client = new HttpClient(handler);
var checker = new UpdateChecker(client);
handler.Response = () => new(HttpStatusCode.OK) { Content = new StringContent(Manifest()) };
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.UpdateAvailable, "Actual checker response parsing");
Check(handler.LastUri?.AbsoluteUri == "https://reyden.ir/dl/reydesk/latest.json" && handler.HasUserAgent && handler.Requests == 1, "Fixed manifest URL and required header");
handler.Response = () => new(HttpStatusCode.NotFound);
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.SourceUnavailable, "404 does not claim current");
handler.Response = () => new((HttpStatusCode)429);
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.RateLimited, "Rate limit reported");
handler.Response = () => new(HttpStatusCode.Forbidden);
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.AccessDenied, "Access denied separated");
handler.Response = () => new(HttpStatusCode.ServiceUnavailable);
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.HttpError, "Server error not current");
handler.Response = () => new(HttpStatusCode.OK) { Content = new StringContent("broken json") };
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.InvalidRelease, "Malformed JSON rejected");
handler.Error = new HttpRequestException("offline");
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.NetworkError, "Offline not current");
handler.Error = new OperationCanceledException();
Check((await checker.CheckAsync()).Status == UpdateCheckStatus.TimedOut, "Timeout reported");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
Check((await checker.CheckAsync("en", cancelled.Token)).Status == UpdateCheckStatus.Cancelled, "Cancellation separated");
Console.WriteLine($"UpdateChecker: {passed} PASS (isolated HTTP fixtures)");
if (args.Contains("--live"))
{
    var live = await new UpdateChecker().CheckAsync();
    Console.WriteLine($"Live manifest {UpdateChecker.ManifestUrl}: {live.Status}; latest={live.LatestVersion ?? "unknown"}");
}

sealed class FakeHandler : HttpMessageHandler
{
    public int Requests;
    public Uri? LastUri;
    public bool HasUserAgent;
    public Func<HttpResponseMessage> Response = () => new(HttpStatusCode.OK);
    public Exception? Error;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        LastUri = request.RequestUri;
        HasUserAgent = request.Headers.UserAgent.Any();
        return Error is null ? Task.FromResult(Response()) : Task.FromException<HttpResponseMessage>(Error);
    }
}
