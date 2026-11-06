using System.Security.Cryptography;
using System.Text.Json;
using ReyDesk.Windows.Identity;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception("FAIL " + label);
    checks++;
}
byte[] Hex(string s) => Convert.FromHexString(s);
string Lower(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

var dir = AppContext.BaseDirectory;
while (dir != null && !File.Exists(Path.Combine(dir, "Tests", "Vectors", "identity-v2.json"))) dir = Path.GetDirectoryName(dir);
if (dir == null) throw new Exception("identity-v2.json not found");
using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "Tests", "Vectors", "identity-v2.json")));
var root = doc.RootElement;
string S(JsonElement e, string n) => e.GetProperty(n).GetString()!;

var keys = root.GetProperty("keys");
byte[] Spki(string k) => Hex(S(keys.GetProperty(k), "spki_der_hex"));
ECDsa Private(string k)
{
    using var pubOnly = ECDsa.Create();
    pubOnly.ImportSubjectPublicKeyInfo(Spki(k), out _);
    var p = pubOnly.ExportParameters(false);
    p.D = Hex(S(keys.GetProperty(k), "private_scalar_hex"));
    return ECDsa.Create(p);
}
foreach (var k in new[] { "host", "client", "attacker" })
{
    var e = keys.GetProperty(k);
    var fp = KeyId.Fingerprint(Spki(k));
    Check(Lower(fp) == S(e, "fingerprint_hex"), k + " fingerprint");
    Check(KeyId.FromFingerprint(fp) == S(e, "key_id"), k + " key id");
    Check(KeyId.Display(KeyId.FromFingerprint(fp)) == S(e, "key_id_display"), k + " key id display");
    Check(KeyId.FingerprintDisplay(fp) == S(e, "fingerprint_display"), k + " fingerprint display");
    using var priv = Private(k);
    Check(Lower(priv.ExportSubjectPublicKeyInfo()) == S(e, "spki_der_hex"), k + " private scalar matches spki");
    var msg = new byte[] { 1, 2, 3 };
    var sig = IdentitySignature.Sign(priv, msg);
    Check(IdentitySignature.Verify(Spki(k), msg, sig), k + " own sign/verify roundtrip");
    Check(!IdentitySignature.Verify(Spki(k), new byte[] { 1, 2, 4 }, sig), k + " tampered payload rejected");
}

foreach (var r in root.GetProperty("desk_id_from_bits").EnumerateArray())
{
    ulong v = 0;
    foreach (var b in Hex(S(r, "data_bits_hex"))) v = (v << 8) | b;
    var id = DeskId.FromBits(v);
    var name = S(r, "desk_id");
    Check(id == name, "desk id from bits " + name);
    Check(id[..11] == S(r, "data") && id[11].ToString() == S(r, "check_char"), "desk id data/check " + name);
    Check(DeskId.Display(id) == S(r, "display"), "desk id display " + name);
}
Check(DeskId.Parse(DeskId.Generate()).Ok, "generated desk id parses");

IdentityParseError Err(string? s) => s switch
{
    null => IdentityParseError.None,
    "BAD_CHAR" => IdentityParseError.BadChar,
    "BAD_LENGTH" => IdentityParseError.BadLength,
    "BAD_CHECK" => IdentityParseError.BadCheck,
    _ => throw new Exception("unknown error " + s)
};
foreach (var r in root.GetProperty("desk_id_parse").EnumerateArray())
{
    var label = S(r, "label");
    var res = DeskId.Parse(S(r, "input"));
    var ok = r.GetProperty("ok").GetBoolean();
    Check(res.Ok == ok, "desk parse ok: " + label);
    Check(res.Error == Err(r.GetProperty("error").GetString()), "desk parse error: " + label);
    if (ok) Check(res.Canonical == S(r, "canonical"), "desk parse canonical: " + label);
}
foreach (var r in root.GetProperty("remote_code_parse").EnumerateArray())
{
    var label = S(r, "label");
    var res = RemoteCode.Normalize(S(r, "input"));
    Check(res.Ok == r.GetProperty("ok").GetBoolean() && res.Error == Err(r.GetProperty("error").GetString()), "remote code: " + label);
    if (res.Ok) Check(res.Canonical == S(r, "canonical"), "remote code canonical: " + label);
}
foreach (var r in root.GetProperty("key_id_from_fingerprint").EnumerateArray())
{
    var fp = Hex(S(r, "fingerprint_hex"));
    var id = S(r, "key_id");
    Check(KeyId.FromFingerprint(fp) == id, "key id " + id);
    Check(KeyId.Display(id) == S(r, "key_id_display"), "key id display " + id);
    Check(KeyId.FingerprintDisplay(fp) == S(r, "fingerprint_display"), "fp display " + id);
}
foreach (var r in root.GetProperty("sas").EnumerateArray())
{
    var host = Spki(S(r, "host_key")); var client = Spki(S(r, "client_key"));
    var nh = Hex(S(r, "n_h_hex")); var nc = Hex(S(r, "n_c_hex"));
    var label = S(r, "label");
    Check(Lower(Sas.Commitment(nc)) == S(r, "commitment_hex"), "sas commitment " + label);
    Check(Lower(Sas.Transcript(host, client, nh, nc)) == S(r, "transcript_hex"), "sas transcript " + label);
    var digits = Sas.Digits(host, client, nh, nc);
    Check(digits == S(r, "sas_digits") && Sas.Display(digits) == S(r, "sas_display"), "sas digits " + label);
}
foreach (var r in root.GetProperty("sas_commitment_check").EnumerateArray())
    Check(Sas.VerifyCommitment(Hex(S(r, "commitment_hex")), Hex(S(r, "revealed_n_c_hex"))) == r.GetProperty("ok").GetBoolean(), "sas commitment check: " + S(r, "label"));
foreach (var r in root.GetProperty("pair_v2_payload").EnumerateArray())
{
    var method = r.GetProperty("method").GetInt32();
    var p = IdentityPayloads.PairV2(method, S(r, "token"), Spki(S(r, "host_key")), Spki(S(r, "client_key")),
        S(r, "requester_device_id"), S(r, "host_desk_id"), Hex(S(r, "n_h_hex")), Hex(S(r, "n_c_hex")));
    Check(S(r, "method_field_utf8") == method.ToString(), "method text " + S(r, "label"));
    Check(Lower(p) == S(r, "payload_hex"), "pair v2 payload " + S(r, "label"));
    Check(Lower(SHA256.HashData(p)) == S(r, "payload_sha256_hex"), "pair v2 sha " + S(r, "label"));
}
foreach (var r in root.GetProperty("attended_v3_payload").EnumerateArray())
{
    var p = IdentityPayloads.AttendedV3(Spki(S(r, "host_key")), Spki(S(r, "client_key")), S(r, "requester_device_id"),
        S(r, "host_desk_id"), S(r, "requester_desk_id"), Hex(S(r, "n_h_hex")), Hex(S(r, "n_c_hex")));
    Check(Lower(p) == S(r, "payload_hex"), "attended v3 payload " + S(r, "label"));
    Check(Lower(SHA256.HashData(p)) == S(r, "payload_sha256_hex"), "attended v3 sha " + S(r, "label"));
}
{
    var hk = Spki("host"); var ck = Spki("client");
    var n32 = new byte[32];
    foreach (var bad in new[] { 0, 16, 31, 33 })
    {
        var threw = false;
        try { IdentityPayloads.AttendedV3(hk, ck, "d", "", "", new byte[bad], n32); } catch (ArgumentException) { threw = true; }
        Check(threw, "attended v3 rejects n_h of " + bad + " bytes");
        threw = false;
        try { IdentityPayloads.AttendedV3(hk, ck, "d", "", "", n32, new byte[bad]); } catch (ArgumentException) { threw = true; }
        Check(threw, "attended v3 rejects n_c of " + bad + " bytes");
    }
}
foreach (var r in root.GetProperty("rendezvous_auth_payload").EnumerateArray())
{
    var p = IdentityPayloads.RendezvousAuth(Hex(S(r, "nonce_hex")), S(r, "public_ws_url"), Spki(S(r, "key")), S(r, "desk_id"));
    Check(Lower(p) == S(r, "payload_hex") && Lower(SHA256.HashData(p)) == S(r, "payload_sha256_hex"), "rendezvous auth " + S(r, "label"));
}
foreach (var r in root.GetProperty("revoke_self_payload").EnumerateArray())
{
    var p = IdentityPayloads.RevokeSelf(Hex(S(r, "nonce_hex")));
    Check(Lower(p) == S(r, "payload_hex") && Lower(SHA256.HashData(p)) == S(r, "payload_sha256_hex"), "revoke self payload");
}
foreach (var r in root.GetProperty("pair_slot_code_hash").EnumerateArray())
{
    var salt = Hex(S(r, "salt_hex"));
    Check(Lower(Lp.Join(Lp.Utf8(S(r, "desk_id")), Lp.Utf8(S(r, "code")), salt)) == S(r, "preimage_hex"), "code hash preimage");
    Check(Lower(RemoteCode.CodeHash(S(r, "desk_id"), S(r, "code"), salt)) == S(r, "code_hash_hex"), "code hash");
}
foreach (var r in root.GetProperty("qr_token").EnumerateArray())
    Check(QrToken.Encode(Hex(S(r, "bytes_hex"))) == S(r, "token"), "qr token " + S(r, "token"));
Check(QrToken.Generate().Length == 22, "generated qr token length");
foreach (var r in root.GetProperty("turn_credential").EnumerateArray())
{
    var fp16 = KeyId.FingerprintHex16(KeyId.Fingerprint(Spki(S(r, "key"))));
    Check(fp16 == S(r, "key_fp_hex16"), "turn fp16");
    var user = TurnCredential.Username(r.GetProperty("expiry_unix").GetInt64(), fp16);
    Check(user == S(r, "username"), "turn username");
    Check(TurnCredential.Credential(S(r, "secret"), user) == S(r, "credential"), "turn credential");
}
foreach (var r in root.GetProperty("signature_checks").EnumerateArray())
{
    var got = IdentitySignature.Verify(Spki(S(r, "key")), Hex(S(r, "payload_hex")), Hex(S(r, "signature_der_hex")));
    Check(got == r.GetProperty("expect_valid").GetBoolean(), "signature " + S(r, "group") + " / " + S(r, "label"));
}
Console.WriteLine($"PASS IdentityV2 ({checks} checks)");
