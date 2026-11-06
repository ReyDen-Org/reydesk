using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReyDesk.Windows.Pairing;

/// <summary>One device this PC has completed pairing with. Only public info - no
/// secrets here, so this is plain JSON (like AppSettingsService), not DPAPI-protected
/// like DeviceIdentityService's own keypair.</summary>
internal sealed record TrustedDeviceRecord(
    string DeviceId,
    string DisplayName,
    string PublicKeyBase64,
    long PairedAtUnixMs,
    long LastSeenUnixMs)
{
    public string? Alias { get; init; }
    public bool UnattendedAllowed { get; init; }
    /// <summary>The phone's own ReyDesk ID, learned from the rendezvous pipe (routing hint only, never trust input).</summary>
    public string? DeskId { get; init; }
    /// <summary>Per-device opt-in for any connection that arrives over the internet pipe. Missing in old files = false.</summary>
    public bool RemoteAllowed { get; init; }
    /// <summary>PairingMethod enum number used at enrollment (0 = unknown, legacy).</summary>
    public int PairedMethod { get; init; }
}

/// <summary>What an approved pairing adds to a trust record. RemoteAllowed null leaves an existing value untouched.</summary>
internal sealed record TrustEnrollment(string? DeskId, bool? RemoteAllowed, int PairedMethod);

/// <summary>Locally persisted list of paired devices (spec §8 trust model - never
/// uploaded anywhere in local mode). Backs the Devices panel and is what
/// <see cref="PairingListener"/> checks to short-circuit re-pairing an already-trusted
/// device.</summary>
internal sealed class TrustedDeviceStore
{
    private const string SubFolder = "ReyDesk";
    private const string FileName = "trusted_devices.json";

    private readonly List<TrustedDeviceRecord> _devices;
    private readonly object _sync = new();
    private readonly string _storePath;

    /// <summary>Raised whenever the list changes (paired, re-seen, etc.) so the UI can refresh.</summary>
    public event Action? Changed;

    private TrustedDeviceStore(List<TrustedDeviceRecord> devices, string storePath)
    {
        _devices = devices;
        _storePath = storePath;
    }

    public static TrustedDeviceStore LoadOrCreate(string? storePath = null)
    {
        var path = storePath ?? GetStorePath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var records = JsonSerializer.Deserialize<List<TrustedDeviceRecord>>(json);
                if (records is not null) return new TrustedDeviceStore(records, path);
            }
            catch (Exception)
            {
                // Corrupt file - start fresh rather than crash on launch.
            }
        }
        return new TrustedDeviceStore(new List<TrustedDeviceRecord>(), path);
    }

    public IReadOnlyList<TrustedDeviceRecord> GetAll()
    {
        lock (_sync) return _devices.ToArray();
    }

    public bool IsTrusted(string deviceId)
    {
        lock (_sync) return _devices.Any(d => d.DeviceId == deviceId);
    }

    public bool IsTrusted(string deviceId, byte[] publicKey)
    {
        lock (_sync)
        {
            var record = _devices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (record is null) return false;
            try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(record.PublicKeyBase64), publicKey); }
            catch (FormatException) { return false; }
        }
    }

    public bool IsUnattendedAllowed(string deviceId, byte[] publicKey)
    {
        lock (_sync) return _devices.Any(d => d.DeviceId == deviceId && d.UnattendedAllowed) && IsTrusted(deviceId, publicKey);
    }

    public bool IsRemoteAllowed(string deviceId, byte[] publicKey)
    {
        lock (_sync) return _devices.Any(d => d.DeviceId == deviceId && d.RemoteAllowed) && IsTrusted(deviceId, publicKey);
    }

    /// <summary>True when a trusted, remote-allowed device owns the key with this SHA-256 fingerprint (pipe pre-check).</summary>
    public bool HasRemoteAllowedFingerprint(byte[] fingerprint)
    {
        lock (_sync)
        {
            foreach (var d in _devices)
            {
                if (!d.RemoteAllowed) continue;
                try
                {
                    if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(Convert.FromBase64String(d.PublicKeyBase64)), fingerprint)) return true;
                }
                catch (FormatException) { }
            }
            return false;
        }
    }

    /// <summary>Per-device "Allow over internet", same exact-key pattern as <see cref="SetUnattendedAllowed"/>.</summary>
    public void SetRemoteAllowed(string deviceId, byte[] expectedPublicKey, bool allowed)
    {
        lock (_sync)
        {
            var index = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0 || !IsTrusted(deviceId, expectedPublicKey)) throw new InvalidOperationException("The selected device identity changed.");
            var previous = _devices[index];
            _devices[index] = previous with { RemoteAllowed = allowed };
            try { Save(); } catch { _devices[index] = previous; throw; }
        }
        Changed?.Invoke();
    }

    /// <summary>Explicit local per-key opt-in. Forgetting/re-pairing defaults off.</summary>
    public void SetUnattendedAllowed(string deviceId, byte[] expectedPublicKey, bool allowed)
    {
        lock (_sync)
        {
            var index = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0 || !IsTrusted(deviceId, expectedPublicKey)) throw new InvalidOperationException("The selected device identity changed.");
            var previous = _devices[index];
            _devices[index] = previous with { UnattendedAllowed = allowed };
            try { Save(); } catch { _devices[index] = previous; throw; }
        }
        Changed?.Invoke();
    }

    /// <summary>A local label only; never changes the paired identity or its key.</summary>
    public void Rename(string deviceId, string? alias)
    {
        alias = alias?.Trim();
        if (alias?.Length > 60) throw new ArgumentException("Device name must be at most 60 characters.", nameof(alias));
        lock (_sync)
        {
            var index = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0) return;
            var previous = _devices[index];
            _devices[index] = previous with { Alias = string.IsNullOrWhiteSpace(alias) ? null : alias };
            try { Save(); }
            catch { _devices[index] = previous; throw; }
        }
        Changed?.Invoke();
    }

    /// <summary>Forget one public identity; its next connection must pair again.</summary>
    public void Remove(string deviceId)
    {
        lock (_sync)
        {
            var index = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (index < 0) return;
            var record = _devices[index];
            _devices.RemoveAt(index);
            try { Save(); }
            catch
            {
                _devices.Insert(index, record);
                throw;
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Adds a newly paired device, or refreshes LastSeenUnixMs for one already trusted.</summary>
    public void AddOrUpdate(string deviceId, string displayName, byte[] publicKey) =>
        AddOrUpdate(deviceId, displayName, publicKey, null);

    /// <summary>Same, with the v2 enrollment facts. An existing record keeps its key (exact match required) and its
    /// unattended flag; a new record starts with unattended off.</summary>
    public void AddOrUpdate(string deviceId, string displayName, byte[] publicKey, TrustEnrollment? enrollment)
    {
        lock (_sync)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var existingIndex = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (existingIndex >= 0)
            {
                var existing = _devices[existingIndex];
                if (!IsTrusted(deviceId, publicKey)) throw new InvalidOperationException("Stored device key changed. Forget and pair deliberately again.");
                _devices[existingIndex] = existing with
                {
                    LastSeenUnixMs = nowMs,
                    RemoteAllowed = enrollment?.RemoteAllowed ?? existing.RemoteAllowed,
                    DeskId = string.IsNullOrEmpty(enrollment?.DeskId) ? existing.DeskId : enrollment!.DeskId,
                };
            }
            else
            {
                _devices.Add(new TrustedDeviceRecord(deviceId, displayName, Convert.ToBase64String(publicKey), nowMs, nowMs)
                {
                    DeskId = string.IsNullOrEmpty(enrollment?.DeskId) ? null : enrollment!.DeskId,
                    RemoteAllowed = enrollment?.RemoteAllowed ?? false,
                    PairedMethod = enrollment?.PairedMethod ?? 0,
                });
            }
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        var path = _storePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_devices));
    }

    private static string GetStorePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, SubFolder, FileName);
    }
}
