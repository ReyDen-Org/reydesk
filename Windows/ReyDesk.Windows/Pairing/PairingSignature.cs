using System.Text;
using System.IO;
using ReyDesk.Windows.Identity;

namespace ReyDesk.Windows.Pairing;

/// <summary>
/// The exact byte layouts of pairing signatures and SAS values (docs/identity-and-pairing.md 2.2, 2.3, 3.2).
/// The v1 payload (token || host key || device id) is gone on purpose: a host that still accepted it would be a
/// downgrade path. Both platforms must build these identically; shared vectors live in Tests/Vectors/identity-v2.json
/// and the codec itself is Identity/IdentityCodec.cs (<see cref="IdentityPayloads"/>, <see cref="Sas"/>).
/// </summary>
internal static class PairingSignature
{
    /// <summary>v2 pair signature payload. <paramref name="method"/> is the PairingMethod enum number, token is the QR token
    /// or canonical 6-digit code (empty otherwise), hostDeskId is the pipe target (empty on LAN), nonces only for SAS methods.</summary>
    public static byte[] BuildSignedPayloadV2(int method, string token, byte[] hostSpki, byte[] clientSpki,
        string requesterDeviceId, string hostDeskId, byte[]? hostNonce, byte[]? clientNonce) =>
        IdentityPayloads.PairV2(method, token, hostSpki, clientSpki, requesterDeviceId, hostDeskId, hostNonce, clientNonce);

    /// <summary>M5 attended v3 payload (spec 2.8); the old one-shot pair/v2 method 5 signature is no longer accepted anywhere.</summary>
    public static byte[] BuildAttendedV3(byte[] hostSpki, byte[] clientSpki, string requesterDeviceId, string hostDeskId,
        string requesterDeskId, byte[] hostNonce, byte[] clientNonce) =>
        IdentityPayloads.AttendedV3(hostSpki, clientSpki, requesterDeviceId, hostDeskId, requesterDeskId, hostNonce, clientNonce);

    public static byte[] BuildSasCommitment(byte[] clientNonce) => Sas.Commitment(clientNonce);

    /// <summary>Six ASCII digits; the UI may localize them.</summary>
    public static string ComputeSas(byte[] hostSpki, byte[] clientSpki, byte[] hostNonce, byte[] clientNonce) =>
        Sas.Digits(hostSpki, clientSpki, hostNonce, clientNonce);

    public static byte[] BuildAuthPayload(byte[] serverNonce, string publicWsUrl, byte[] spki, string deskId) =>
        IdentityPayloads.RendezvousAuth(serverNonce, publicWsUrl, spki, deskId);

    // Length-prefixed fields and separate host/client domains prevent ambiguity
    // and reflection. The expiry uses invariant decimal text on both platforms.
    public static byte[] BuildReconnectPayload(bool hostProof, byte[] challenge, byte[] clientNonce,
        string hostId, string clientId, byte[] hostKey, byte[] clientKey, long expiresAt)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in new[] { Encoding.UTF8.GetBytes(hostProof ? "ReyDesk/reconnect/host/v1" : "ReyDesk/reconnect/client/v1"),
            challenge, clientNonce, Encoding.UTF8.GetBytes(hostId), Encoding.UTF8.GetBytes(clientId), hostKey, clientKey,
            Encoding.UTF8.GetBytes(expiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture)) })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, field.Length);
            stream.Write(length); stream.Write(field);
        }
        return stream.ToArray();
    }
}
