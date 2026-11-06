using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Google.Protobuf;
using ReyDesk.Windows.Identity;
using ReyDesk.Protocol.V1;

namespace ReyDesk.Windows.Pairing;

/// <summary>Everything needed to render one LAN/QR pairing attempt: the QR payload (single-use 128-bit token, never shown as
/// text) and the host's ReyDesk ID for display. <see cref="Offer"/> is the raw message a <see cref="PairingListener"/>
/// validates incoming QR requests against.</summary>
internal sealed record PairingPresentation(PairingOffer Offer, string QrPayload, string DeskIdDisplay, DateTimeOffset ExpiresAt)
{
    /// <summary>Compatibility shim for MainPage (T11 removes it with the PIN card). The PIN no longer exists; always empty.</summary>
    [Obsolete("PIN pairing was replaced by SAS and the remote code. Remove the PIN UI.")]
    public string Pin => "";
}

/// <summary>One owner-opened typed pairing code for "Add phone over internet" (M4). Show <see cref="Display"/>; pass
/// <see cref="Code"/> to <c>RendezvousClient.CreatePairSlotAsync</c> and <c>PairingListener.ArmRemoteCode</c>.</summary>
internal sealed record RemoteCodePresentation(string Code, string Display, DateTimeOffset ExpiresAt);

/// <summary>
/// Builds a <see cref="PairingOffer"/> for display as a QR code, and for
/// <see cref="PairingListener"/> to validate incoming connections against.
/// </summary>
internal static class PairingOfferFactory
{
    public const int LocalPairingPort = 47821; // ReyDesk's reserved local pairing port
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <param name="deskId">Canonical host ReyDesk ID when the PC is registered with the rendezvous service, else null.</param>
    /// <param name="rendezvousUrl">Service URL the phone uses for remote pairing (M3), else null.</param>
    public static PairingPresentation Create(DeviceIdentityService identity, string? deskId = null, string? rendezvousUrl = null)
    {
        var token = GenerateQrToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(Ttl);

        var offer = new PairingOffer
        {
            Host = new DeviceIdentity
            {
                DeviceId = identity.DeviceId,
                DisplayName = identity.DisplayName,
                Platform = ReyDesk.Protocol.V1.Platform.Windows,
                AppVersion = ReyDesk.Windows.Platform.AppInfo.Version,
                DeskId = deskId ?? "",
            },
            HostPublicKey = ByteString.CopyFrom(identity.PublicKey),
            LocalIpHint = GetLocalIPv4() ?? "",
            LocalPortHint = LocalPairingPort,
            PairingToken = token,
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
            RendezvousUrl = rendezvousUrl ?? "",
        };

        var bytes = offer.ToByteArray();
        var qrPayload = "reydesk://pair/" + Base64UrlEncode(bytes);
        var deskIdDisplay = string.IsNullOrEmpty(deskId) ? "" : DeskId.Display(deskId);
        return new PairingPresentation(offer, qrPayload, deskIdDisplay, expiresAt);
    }

    /// <summary>22 base64url chars of 16 random bytes. QR only; it is never typed or displayed.</summary>
    public static string GenerateQrToken() => QrToken.Generate();

    /// <summary>Six uniform random digits. RandomNumberGenerator.GetInt32 rejection-samples, so there is no modulo bias.</summary>
    public static string GenerateRemoteCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static RemoteCodePresentation CreateRemoteCode()
    {
        var code = GenerateRemoteCode();
        return new RemoteCodePresentation(code, code[..3] + " " + code[3..], DateTimeOffset.UtcNow.Add(Ttl));
    }

    private static string? GetLocalIPv4()
    {
        var candidates = new List<LocalAddressCandidate>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = ni.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(gateway => LocalAddressSelector.IsUsableIPv4(gateway.Address));
            foreach (var unicast in properties.UnicastAddresses)
            {
                candidates.Add(new LocalAddressCandidate(ni.Name, ni.Description, ni.NetworkInterfaceType,
                    ni.OperationalStatus == OperationalStatus.Up, hasGateway, unicast.Address));
            }
        }
        return LocalAddressSelector.Select(candidates)?.ToString();
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
