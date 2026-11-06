using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReyDesk.Windows.Identity;

/// <summary>
/// This device's long-term identity: a stable device ID and an ECDSA P-256 keypair
/// (used per docs/protocol.md's pairing/trust model - the original design doc named
/// Ed25519, but .NET has no fully-confirmed built-in Ed25519 support as of .NET 8,
/// while ECDsa/P-256 is a long-standing, natively supported, equally well-established
/// primitive for this use case). Generated once on first run,
/// persisted locally, and never uploaded anywhere in local mode.
/// </summary>
internal sealed class DeviceIdentityService
{
    private const string SubFolder = "ReyDesk";
    private const string FileName = "identity.json";

    public string DeviceId { get; }
    public string DisplayName { get; }
    public byte[] PublicKey { get; }

    private readonly byte[] _privateKeyPkcs8;
    private readonly object _deskIdLock = new();
    private string? _deskId;

    /// <summary>Canonical (12 chars, no separators) rendezvous Desk ID, or null. Persisted in the same DPAPI-protected file as the
    /// key. Satisfies the DeskId/SetDeskId half of Rendezvous.IRendezvousIdentity (adapted in Remote/RendezvousIdentityAdapter.cs).</summary>
    public string? DeskId { get { lock (_deskIdLock) return _deskId; } }

    private DeviceIdentityService(string deviceId, string displayName, byte[] publicKey, byte[] privateKeyPkcs8)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        PublicKey = publicKey;
        _privateKeyPkcs8 = privateKeyPkcs8;
    }

    public static DeviceIdentityService LoadOrCreate()
    {
        var path = GetStorePath();

        if (File.Exists(path))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(path);
                var json = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                var record = JsonSerializer.Deserialize<IdentityRecord>(json)
                    ?? throw new InvalidDataException("Empty identity record.");
                var loaded = new DeviceIdentityService(
                    record.DeviceId,
                    record.DisplayName,
                    Convert.FromBase64String(record.PublicKeyBase64),
                    Convert.FromBase64String(record.PrivateKeyPkcs8Base64));
                loaded._deskId = string.IsNullOrEmpty(record.DeskId) ? null : record.DeskId;
                return loaded;
            }
            catch (Exception)
            {
                // Corrupt or unreadable (e.g. moved to a different user profile, since
                // DPAPI keys are user-scoped) - fall through and generate a fresh identity
                // rather than crash the app on startup.
            }
        }

        return CreateAndPersist(path);
    }

    private static DeviceIdentityService CreateAndPersist(string path)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = ecdsa.ExportSubjectPublicKeyInfo();
        var privateKey = ecdsa.ExportPkcs8PrivateKey();

        var deviceId = Guid.NewGuid().ToString();
        var displayName = Environment.MachineName;

        var record = new IdentityRecord(deviceId, displayName, Convert.ToBase64String(publicKey), Convert.ToBase64String(privateKey));
        var json = JsonSerializer.SerializeToUtf8Bytes(record);
        var protectedBytes = ProtectedData.Protect(json, optionalEntropy: null, DataProtectionScope.CurrentUser);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, protectedBytes);

        return new DeviceIdentityService(deviceId, displayName, publicKey, privateKey);
    }

    /// <summary>Stores (or clears, with null) the server-allocated Desk ID by rewriting the protected identity file.</summary>
    public void SetDeskId(string? deskId)
    {
        lock (_deskIdLock)
        {
            if (_deskId == deskId) return;
            var record = new IdentityRecord(DeviceId, DisplayName, Convert.ToBase64String(PublicKey),
                Convert.ToBase64String(_privateKeyPkcs8), deskId);
            var protectedBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(record), optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            var path = GetStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, path, overwrite: true);
            _deskId = deskId;
        }
    }

    /// <summary>Signs data with this device's private key - used to prove identity during pairing.</summary>
    public byte[] Sign(byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(_privateKeyPkcs8, out _);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>TLS identity uses the same persisted key pinned by the pairing QR.
    /// The certificate is local and self-signed: trust comes from its exact SPKI,
    /// never a permissive certificate-validation callback or a public CA.</summary>
    public X509Certificate2 CreateTlsCertificate()
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_privateKeyPkcs8, out _);
        var request = new CertificateRequest("CN=ReyDesk", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null,
            // Schannel rejects EphemeralKeySet (SEC_E_NO_CREDENTIALS). UserKeySet
            // imports a temporary provider key, cleaned up with the certificate;
            // PersistKeySet is intentionally absent.
            X509KeyStorageFlags.UserKeySet);
    }

    /// <summary>Verifies a signature made by another device's key (e.g. an Android
    /// PairingRequest). The signature format is DER (RFC 3279 SEQUENCE{r,s}) - that's
    /// what Java/Android's default <c>Signature.getInstance("SHA256withECDSA")</c>
    /// produces, so Windows verifies in that format rather than .NET's own default
    /// IEEE P1363 concatenation. See docs/protocol.md "Pairing & trust model" for the
    /// exact signed-payload byte layout this is checked against.</summary>
    public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            // Malformed key or signature - treat as "does not verify", not a crash.
            return false;
        }
    }

    private static string GetStorePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, SubFolder, FileName);
    }

    private sealed record IdentityRecord(
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("publicKey")] string PublicKeyBase64,
        [property: JsonPropertyName("privateKey")] string PrivateKeyPkcs8Base64,
        [property: JsonPropertyName("deskId")] string? DeskId = null);
}
