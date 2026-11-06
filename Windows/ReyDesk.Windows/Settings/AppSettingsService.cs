using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyDesk.Windows.Settings;

internal enum AppLanguage { English, Persian }

/// <summary>What the window's close (X) button does. Spec §20 describes tray-minimize
/// as the norm for background services, but defaults to fully exiting here per an
/// explicit user request - it's a per-user choice, not fixed either way.</summary>
internal enum CloseButtonBehavior { ExitApp, MinimizeToTray }

/// <summary>
/// Persisted user-facing app settings (spec §46 "General"). Language, and
/// close-button behavior and bounded streaming ceilings are persisted. Streaming
/// changes apply when the next session snapshots this service's preferences.
/// </summary>
internal sealed class AppSettingsService
{
    private const string FileName = "settings.json";

    public AppLanguage Language { get; private set; }
    public CloseButtonBehavior CloseButtonBehavior { get; private set; }
    public int StreamMaxHeight { get; private set; } = 1080;
    public int StreamMaxFps { get; private set; } = 60;
    public int StreamBitrateKbps { get; private set; } = 12000;
    public string ReceiveFolder { get; private set; } = "";
    public string StreamPreset { get; private set; } = "Custom";
    /// <summary>Internet access master switch (default off).</summary>
    public bool RemoteAllowed { get; private set; }
    /// <summary>Opt-in TURN relay for internet sessions (default off).</summary>
    public bool RelayAllowed { get; private set; }
    /// <summary>Built-in rendezvous service, used when no override is stored.</summary>
    public const string DefaultRendezvousUrl = "wss://rdv.reyden.ir/v1/ws";
    /// <summary>Stored Advanced override; empty means use the default.</summary>
    public string RendezvousUrlOverride { get; private set; } = "";
    /// <summary>Effective rendezvous URL: the override if set, else <see cref="DefaultRendezvousUrl"/>.</summary>
    public string RendezvousUrl => RendezvousUrlOverride.Length > 0 ? RendezvousUrlOverride : DefaultRendezvousUrl;
    /// <summary>Phone Hub privacy toggles, remembered between runs (all default off).</summary>
    public bool HubPreviews { get; private set; }
    public bool HubInAppAlerts { get; private set; }
    public bool HubShellAlerts { get; private set; }
    public bool HubFocus { get; private set; }
    /// <summary>The connection tutorial first-run card was opened or dismissed (default false).</summary>
    public bool TutorialSeen { get; private set; }
    public StreamPreferences Streaming => new(StreamMaxHeight, StreamMaxFps, StreamBitrateKbps);

    private AppSettingsService(AppLanguage language, CloseButtonBehavior closeButtonBehavior)
    {
        Language = language;
        CloseButtonBehavior = closeButtonBehavior;
    }

    public static AppSettingsService LoadOrCreate()
    {
        var path = GetStorePath();
        if (File.Exists(path))
        {
            try
            {
                var record = JsonSerializer.Deserialize<SettingsRecord>(File.ReadAllBytes(path));
                if (record is not null)
                {
                    var settings = new AppSettingsService(
                        Enum.TryParse<AppLanguage>(record.Language, out var language) && Enum.IsDefined(language) ? language : AppLanguage.English,
                        Enum.TryParse<CloseButtonBehavior>(record.CloseButtonBehavior, out var closeBehavior) ? closeBehavior : CloseButtonBehavior.ExitApp);
                    var streaming = StreamPreferences.Normalize(record.StreamMaxHeight, record.StreamMaxFps, record.StreamBitrateKbps);
                    settings.StreamMaxHeight = streaming.MaxHeight;
                    settings.StreamMaxFps = streaming.MaxFps;
                    settings.StreamBitrateKbps = streaming.BitrateKbps;
                    settings.StreamPreset = record.StreamPreset is "Auto" or "Performance" or "Balanced" or "Quality" ? record.StreamPreset : "Custom";
                    settings.ReceiveFolder = record.ReceiveFolder is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) ? folder : "";
                    settings.RemoteAllowed = record.RemoteAllowed;
                    settings.RelayAllowed = record.RelayAllowed;
                    settings.RendezvousUrlOverride = TryNormalizeRendezvousUrl(record.RendezvousUrl, out var rendezvous) ? rendezvous : "";
                    settings.HubPreviews = record.HubPreviews;
                    settings.HubInAppAlerts = record.HubInAppAlerts;
                    settings.HubShellAlerts = record.HubShellAlerts;
                    settings.HubFocus = record.HubFocus;
                    settings.TutorialSeen = record.TutorialSeen;
                    return settings;
                }
            }
            catch (Exception)
            {
                // Corrupt file - fall through to defaults rather than crash on startup.
            }
        }

        return new AppSettingsService(AppLanguage.English, CloseButtonBehavior.ExitApp);
    }

    public void SetReceiveFolder(string folder)
    {
        if (folder.Length > 0 && !Path.IsPathFullyQualified(folder)) throw new ArgumentException("Choose an absolute folder path.");
        ReceiveFolder = folder; Save();
    }

    public void SetTutorialSeen(bool seen) { if (TutorialSeen == seen) return; TutorialSeen = seen; Save(); }

    public void SetRemoteAllowed(bool allowed) { RemoteAllowed = allowed; Save(); }

    public void SetRelayAllowed(bool allowed) { RelayAllowed = allowed; Save(); }

    public void SetPhoneHubToggles(bool previews, bool inApp, bool shell, bool focus)
    {
        if (previews == HubPreviews && inApp == HubInAppAlerts && shell == HubShellAlerts && focus == HubFocus) return;
        HubPreviews = previews; HubInAppAlerts = inApp; HubShellAlerts = shell; HubFocus = focus; Save();
    }

    /// <summary>Empty resets to the built-in default. Otherwise wss:// only (ws:// to loopback is accepted in Debug builds).</summary>
    public void SetRendezvousUrl(string url)
    {
        url = url.Trim();
        if (url.Length > 0 && !TryNormalizeRendezvousUrl(url, out url))
            throw new ArgumentException("Expected a wss:// URL.", nameof(url));
        RendezvousUrlOverride = url == DefaultRendezvousUrl ? "" : url;
        Save();
    }

    internal static bool TryNormalizeRendezvousUrl(string? input, out string url) =>
        ReyDesk.Windows.Rendezvous.RendezvousUrl.TryNormalize(input, ReyDesk.Windows.Rendezvous.RendezvousUrl.DevCleartextDefault, out url);

    public void SetLanguage(AppLanguage language)
    {
        Language = language;
        Save();
    }

    public void SetCloseButtonBehavior(CloseButtonBehavior behavior)
    {
        CloseButtonBehavior = behavior;
        Save();
    }

    public void SetStreaming(int maxHeight, int maxFps, int bitrateKbps)
    {
        var streaming = StreamPreferences.Normalize(maxHeight, maxFps, bitrateKbps);
        StreamMaxHeight = streaming.MaxHeight;
        StreamMaxFps = streaming.MaxFps;
        StreamBitrateKbps = streaming.BitrateKbps;
        StreamPreset = "Custom";
        Save();
    }

    public void SetStreamPreset(string preset)
    {
        if (preset is not ("Auto" or "Performance" or "Balanced" or "Quality" or "Custom"))
            throw new ArgumentException("Unknown stream preset.", nameof(preset));
        if (preset == "Performance") SetStreaming(720, 30, 4000);
        else if (preset == "Balanced") SetStreaming(1080, 30, 8000);
        else if (preset is "Quality" or "Auto") SetStreaming(1080, 60, 12000);
        StreamPreset = preset;
        Save();
    }

    private void Save()
    {
        var record = new SettingsRecord(Language.ToString(), CloseButtonBehavior.ToString(),
            StreamMaxHeight, StreamMaxFps, StreamBitrateKbps, StreamPreset, ReceiveFolder,
            RemoteAllowed, RelayAllowed, RendezvousUrlOverride, HubPreviews, HubInAppAlerts, HubShellAlerts, HubFocus, TutorialSeen);
        var path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(record));
    }

    private static string GetStorePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "ReyDesk", FileName);
    }

    private sealed record SettingsRecord(
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("closeButtonBehavior")] string CloseButtonBehavior,
        [property: JsonPropertyName("streamMaxHeight")] int StreamMaxHeight = 1080,
        [property: JsonPropertyName("streamMaxFps")] int StreamMaxFps = 60,
        [property: JsonPropertyName("streamBitrateKbps")] int StreamBitrateKbps = 12000,
        [property: JsonPropertyName("streamPreset")] string StreamPreset = "Custom",
        [property: JsonPropertyName("receiveFolder")] string ReceiveFolder = "",
        [property: JsonPropertyName("remoteAllowed")] bool RemoteAllowed = false,
        [property: JsonPropertyName("relayAllowed")] bool RelayAllowed = false,
        [property: JsonPropertyName("rendezvousUrl")] string RendezvousUrl = "",
        [property: JsonPropertyName("hubPreviews")] bool HubPreviews = false,
        [property: JsonPropertyName("hubInAppAlerts")] bool HubInAppAlerts = false,
        [property: JsonPropertyName("hubShellAlerts")] bool HubShellAlerts = false,
        [property: JsonPropertyName("hubFocus")] bool HubFocus = false,
        [property: JsonPropertyName("tutorialSeen")] bool TutorialSeen = false);
}
