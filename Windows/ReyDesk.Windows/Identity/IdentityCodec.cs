using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReyDesk.Windows.Identity;

/// <summary>Length-prefixed field writer shared by every v2 signed payload (docs/identity-and-pairing.md).
/// LP(x) = 4-byte big-endian length, then x. Same framing as PairingSignature.BuildReconnectPayload.</summary>
internal static class Lp
{
    public static byte[] Join(params byte[][] fields)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)field.Length);
            stream.Write(length);
            stream.Write(field);
        }
        return stream.ToArray();
    }

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}

/// <summary>Crockford base32 (0-9 A-Z without I L O U), bits read most significant first.</summary>
internal static class Crockford
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static int ValueOf(char c) => c < 128 ? Alphabet.IndexOf(c) : -1;

    /// <summary>Encodes the low chars*5 bits of value, char 0 = top 5 bits.</summary>
    public static string EncodeBits(ulong value, int chars)
    {
        var buffer = new char[chars];
        for (var i = 0; i < chars; i++) buffer[i] = Alphabet[(int)((value >> (5 * (chars - 1 - i))) & 31)];
        return new string(buffer);
    }

    /// <summary>Encodes bytes MSB first, zero padded on the right to a whole number of 5-bit chars.</summary>
    public static string EncodeBytes(ReadOnlySpan<byte> data)
    {
        var chars = (data.Length * 8 + 4) / 5;
        var buffer = new char[chars];
        for (var i = 0; i < chars; i++)
        {
            var v = 0;
            for (var b = 0; b < 5; b++)
            {
                var bit = i * 5 + b;
                var set = bit < data.Length * 8 && (data[bit / 8] & (0x80 >> (bit % 8))) != 0;
                v = (v << 1) | (set ? 1 : 0);
            }
            buffer[i] = Alphabet[v];
        }
        return new string(buffer);
    }

    public static string Group(string text, int size, char separator)
    {
        var sb = new StringBuilder(text.Length + text.Length / size);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && i % size == 0) sb.Append(separator);
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>Drops separators and bidi controls (dropped = true) and maps Persian/Arabic-Indic digits to ASCII.
    /// Shared by Desk ID and remote code parsing. Never uses char.GetNumericValue.</summary>
    public static char Normalize(char c, out bool dropped)
    {
        dropped = c is '-' or ' ' or '‎' or '‏' or '⁦' or '⁧' or '⁨' or '⁩';
        if (dropped) return c;
        if (c >= '۰' && c <= '۹') return (char)('0' + (c - '۰'));
        if (c >= '٠' && c <= '٩') return (char)('0' + (c - '٠'));
        return c;
    }
}

internal enum IdentityParseError { None, BadChar, BadLength, BadCheck }

internal readonly record struct IdentityParseResult(bool Ok, string? Canonical, IdentityParseError Error);

/// <summary>Desk ID: 11 Crockford data chars (55 bits) + 1 check char, shown as XXXX-XXXX-XXXX.</summary>
internal static class DeskId
{
    public const int Length = 12;

    public static char CheckChar(string data11)
    {
        var sum = 0;
        for (var i = 0; i < 11; i++) sum += (i + 1) * Crockford.ValueOf(data11[i]);
        return Crockford.Alphabet[sum % 31];
    }

    /// <summary>Builds a canonical Desk ID from a 55-bit value.</summary>
    public static string FromBits(ulong data55)
    {
        if (data55 >> 55 != 0) throw new ArgumentOutOfRangeException(nameof(data55));
        var data = Crockford.EncodeBits(data55, 11);
        return data + CheckChar(data);
    }

    public static string Generate() =>
        FromBits(BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8)) >> 9);

    public static string Display(string canonical) => Crockford.Group(canonical, 4, '-');

    /// <summary>Error precedence: BadChar, BadLength, BadCheck. Canonical = 12 uppercase chars, no separators.</summary>
    public static IdentityParseResult Parse(string? input)
    {
        var sb = new StringBuilder(Length);
        foreach (var raw in input ?? string.Empty)
        {
            var ch = Crockford.Normalize(raw, out var dropped);
            if (dropped) continue;
            if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 32);
            if (ch is 'I' or 'L') ch = '1';
            else if (ch == 'O') ch = '0';
            else if (ch == 'U') continue;
            if (Crockford.ValueOf(ch) < 0) return new(false, null, IdentityParseError.BadChar);
            sb.Append(ch);
        }
        if (sb.Length != Length) return new(false, null, IdentityParseError.BadLength);
        var canonical = sb.ToString();
        if (CheckChar(canonical[..11]) != canonical[11]) return new(false, null, IdentityParseError.BadCheck);
        return new(true, canonical, IdentityParseError.None);
    }
}

/// <summary>Key ID (top 60 bits of SHA-256(SPKI), no check char) and the 52-char fingerprint display.</summary>
internal static class KeyId
{
    public static byte[] Fingerprint(byte[] spki) => SHA256.HashData(spki);

    public static string FromFingerprint(byte[] fingerprint) =>
        Crockford.EncodeBits(BinaryPrimitives.ReadUInt64BigEndian(fingerprint) >> 4, 12);

    public static string FromSpki(byte[] spki) => FromFingerprint(Fingerprint(spki));

    public static string Display(string keyId) => Crockford.Group(keyId, 4, '-');

    /// <summary>256 fingerprint bits + 4 zero bits = 52 chars, 13 groups of 4 joined by ASCII space.</summary>
    public static string FingerprintDisplay(byte[] fingerprint) =>
        Crockford.Group(Crockford.EncodeBytes(fingerprint), 4, ' ');

    /// <summary>First 16 lowercase hex chars of the fingerprint (TURN username suffix).</summary>
    public static string FingerprintHex16(byte[] fingerprint) => Convert.ToHexString(fingerprint, 0, 8).ToLowerInvariant();
}

/// <summary>Remote 6-digit code: same separator/bidi/digit-set handling as Desk ID, exactly 6 ASCII digits.</summary>
internal static class RemoteCode
{
    public static IdentityParseResult Normalize(string? input)
    {
        var sb = new StringBuilder(6);
        foreach (var raw in input ?? string.Empty)
        {
            var c = Crockford.Normalize(raw, out var dropped);
            if (dropped) continue;
            if (c < '0' || c > '9') return new(false, null, IdentityParseError.BadChar);
            sb.Append(c);
        }
        if (sb.Length != 6) return new(false, null, IdentityParseError.BadLength);
        return new(true, sb.ToString(), IdentityParseError.None);
    }

    /// <summary>Pair slot code_hash = SHA-256(LP(desk_id) LP(code) LP(salt 16 bytes)).</summary>
    public static byte[] CodeHash(string canonicalDeskId, string code6, byte[] salt) =>
        SHA256.HashData(Lp.Join(Lp.Utf8(canonicalDeskId), Lp.Utf8(code6), salt));
}

internal static class QrToken
{
    /// <summary>base64url without padding of 16 bytes (22 chars).</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Generate() => Encode(RandomNumberGenerator.GetBytes(16));
}

/// <summary>Short Authentication String: commitment, reveal check and the 6-digit value.</summary>
internal static class Sas
{
    public static byte[] RandomNonce() => RandomNumberGenerator.GetBytes(32);

    /// <summary>SHA-256(UTF8("ReyDesk/sas-commit/v1") || n_c), plain concatenation, no length prefix.</summary>
    public static byte[] Commitment(byte[] nc) => SHA256.HashData(Lp.Utf8("ReyDesk/sas-commit/v1").Concat(nc).ToArray());

    public static bool VerifyCommitment(byte[] commitment, byte[] revealedNc) =>
        CryptographicOperations.FixedTimeEquals(commitment, Commitment(revealedNc));

    public static byte[] Transcript(byte[] hostSpki, byte[] clientSpki, byte[] nh, byte[] nc) =>
        Lp.Join(Lp.Utf8("ReyDesk/sas/v1"), hostSpki, clientSpki, nh, nc);

    public static string Digits(byte[] hostSpki, byte[] clientSpki, byte[] nh, byte[] nc)
    {
        var h = SHA256.HashData(Transcript(hostSpki, clientSpki, nh, nc));
        var value = BinaryPrimitives.ReadUInt32BigEndian(h) % 1000000u;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Display(string digits) => digits[..3] + " " + digits[3..];
}

/// <summary>Byte layouts of every v2 signed payload. method = decimal text of the PairingMethod enum number.</summary>
internal static class IdentityPayloads
{
    public static byte[] PairV2(int method, string token, byte[] hostSpki, byte[] clientSpki,
        string requesterDeviceId, string hostDeskId, byte[]? nh, byte[]? nc) =>
        Lp.Join(Lp.Utf8("ReyDesk/pair/v2"),
            Lp.Utf8(method.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Lp.Utf8(token), hostSpki, clientSpki, Lp.Utf8(requesterDeviceId), Lp.Utf8(hostDeskId),
            nh ?? Array.Empty<byte>(), nc ?? Array.Empty<byte>());

    /// <summary>M5 attended reconnect proof (spec 2.8). Desk IDs are both empty on the LAN and both canonical over a pipe.
    /// n_h and n_c are exactly 32 bytes. Domain tag and version differ from pair/v2 on purpose: an old M5 signature never verifies.</summary>
    public static byte[] AttendedV3(byte[] hostSpki, byte[] clientSpki, string requesterDeviceId, string hostDeskId,
        string requesterDeskId, byte[] nh, byte[] nc)
    {
        if (nh.Length != 32 || nc.Length != 32) throw new ArgumentException("Attended nonces must be exactly 32 bytes.");
        return Lp.Join(Lp.Utf8("ReyDesk/attended/client/v3"), hostSpki, clientSpki, Lp.Utf8(requesterDeviceId),
            Lp.Utf8(hostDeskId), Lp.Utf8(requesterDeskId), nh, nc);
    }

    public static byte[] RendezvousAuth(byte[] serverNonce, string publicWsUrl, byte[] spki, string deskId) =>
        Lp.Join(Lp.Utf8("ReyDesk/rendezvous/auth/v1"), serverNonce, Lp.Utf8(publicWsUrl), spki, Lp.Utf8(deskId));

    public static byte[] RevokeSelf(byte[] serverNonce) =>
        Lp.Join(Lp.Utf8("ReyDesk/rendezvous/revoke/v1"), serverNonce);
}

/// <summary>ECDSA P-256 / SHA-256 signatures in DER (X9.62), not IEEE P1363.</summary>
internal static class IdentitySignature
{
    public static byte[] Sign(ECDsa key, byte[] payload) =>
        key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public static bool Verify(byte[] spki, byte[] payload, byte[] signatureDer)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa.VerifyData(payload, signatureDer, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>TURN REST credential (server-side shape, kept for tests and a future local relay).</summary>
internal static class TurnCredential
{
    public static string Username(long expiryUnix, string fingerprintHex16) =>
        expiryUnix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + fingerprintHex16;

    public static string Credential(string turnSecret, string username) =>
        Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(turnSecret), Encoding.UTF8.GetBytes(username)));
}
