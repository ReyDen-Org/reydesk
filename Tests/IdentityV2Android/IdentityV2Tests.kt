import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.identity.IdentityPayloads
import ir.reyden.reydesk.identity.IdentitySignatures
import java.io.File
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPrivateKeySpec
import java.math.BigInteger

// Minimal JSON reader: objects -> Map, arrays -> List, strings, numbers (Long), booleans, null.
private class Json(val s: String) {
    var i = 0
    fun ws() { while (i < s.length && s[i].isWhitespace()) i++ }
    fun value(): Any? {
        ws()
        return when (val c = s[i]) {
            '{' -> { i++; val m = LinkedHashMap<String, Any?>(); ws()
                if (s[i] == '}') { i++; return m }
                while (true) { ws(); val k = str(); ws(); i++; m[k] = value(); ws()
                    if (s[i++] == '}') return m } ; @Suppress("UNREACHABLE_CODE") m }
            '[' -> { i++; val l = ArrayList<Any?>(); ws()
                if (s[i] == ']') { i++; return l }
                while (true) { l.add(value()); ws(); if (s[i++] == ']') return l } ; @Suppress("UNREACHABLE_CODE") l }
            '"' -> str()
            't' -> { i += 4; true }
            'f' -> { i += 5; false }
            'n' -> { i += 4; null }
            else -> { val st = i; while (i < s.length && (s[i] == '-' || s[i].isDigit())) i++
                if (st == i) error("bad json at $i: $c"); s.substring(st, i).toLong() }
        }
    }
    fun str(): String {
        i++ ; val sb = StringBuilder()
        while (s[i] != '"') {
            if (s[i] == '\\') { i++
                when (s[i]) { 'n' -> sb.append('\n'); 't' -> sb.append('\t'); 'r' -> sb.append('\r')
                    'u' -> { sb.append(s.substring(i + 1, i + 5).toInt(16).toChar()); i += 4 }
                    else -> sb.append(s[i]) }
            } else sb.append(s[i])
            i++
        }
        i++; return sb.toString()
    }
}

@Suppress("UNCHECKED_CAST") private fun Any?.obj() = this as Map<String, Any?>
@Suppress("UNCHECKED_CAST") private fun Any?.arr() = this as List<Any?>
private fun Any?.str() = this as String

private fun hex(h: String): ByteArray = ByteArray(h.length / 2) { h.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
private fun ByteArray.hex() = joinToString("") { "%02x".format(it) }

private var checks = 0
private val failures = ArrayList<String>()
private fun eq(label: String, expected: Any?, actual: Any?) {
    checks++
    val ok = if (expected is ByteArray && actual is ByteArray) expected.contentEquals(actual) else expected == actual
    if (!ok) failures.add("$label: expected <$expected> got <$actual>")
}
private fun eqBytes(label: String, expectedHex: String, actual: ByteArray) = eq(label, expectedHex, actual.hex())

fun main(args: Array<String>) {
    val d = Json(File(args[0]).readText(Charsets.UTF_8)).value().obj()
    val keys = d["keys"].obj()
    fun spki(name: String) = hex(keys[name].obj()["spki_der_hex"].str())
    fun priv(name: String): java.security.PrivateKey {
        val ap = AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }
        val spec = ap.getParameterSpec(ECParameterSpec::class.java)
        return KeyFactory.getInstance("EC").generatePrivate(
            ECPrivateKeySpec(BigInteger(keys[name].obj()["private_scalar_hex"].str(), 16), spec))
    }

    // keys: fingerprint, key id, display
    for ((n, v) in keys) {
        val k = v.obj(); val fp = DeskIdCodec.fingerprint(hex(k["spki_der_hex"].str()))
        eqBytes("fp $n", k["fingerprint_hex"].str(), fp)
        eq("keyId $n", k["key_id"], DeskIdCodec.keyId(fp))
        eq("keyIdDisplay $n", k["key_id_display"], DeskIdCodec.keyIdDisplay(fp))
        eq("fpDisplay $n", k["fingerprint_display"], DeskIdCodec.fingerprintDisplay(fp))
    }
    for (r in d["key_id_from_fingerprint"].arr()) {
        val o = r.obj(); val fp = hex(o["fingerprint_hex"].str())
        eq("kid", o["key_id"], DeskIdCodec.keyId(fp))
        eq("kid disp", o["key_id_display"], DeskIdCodec.keyIdDisplay(fp))
        eq("fp disp", o["fingerprint_display"], DeskIdCodec.fingerprintDisplay(fp))
    }
    for (r in d["desk_id_from_bits"].arr()) {
        val o = r.obj(); val id = DeskIdCodec.fromBits(o["data_bits_hex"].str().toLong(16))
        eq("deskid ${o["data"]}", o["desk_id"], id)
        eq("display", o["display"], DeskIdCodec.display(id))
        eq("check char", o["check_char"].str(), id.last().toString())
    }
    for (r in d["desk_id_parse"].arr()) {
        val o = r.obj(); val l = o["label"].str(); val input = o["input"].str()
        eq("cp $l", o["input_codepoints"].arr().joinToString(","), input.map { "%04X".format(it.code) }.joinToString(","))
        val res = DeskIdCodec.parse(input)
        if (o["ok"] == true) eq("parse $l", DeskIdCodec.Result.Ok(o["canonical"].str()), res)
        else eq("parse $l", DeskIdCodec.Result.Err(DeskIdCodec.ParseError.valueOf(o["error"].str())), res)
    }
    for (r in d["remote_code_parse"].arr()) {
        val o = r.obj(); val l = o["label"].str()
        val res = DeskIdCodec.parseRemoteCode(o["input"].str())
        if (o["ok"] == true) eq("code $l", DeskIdCodec.Result.Ok(o["canonical"].str()), res)
        else eq("code $l", DeskIdCodec.Result.Err(DeskIdCodec.ParseError.valueOf(o["error"].str())), res)
    }
    for (r in d["qr_token"].arr()) { val o = r.obj(); eq("qr", o["token"], IdentityPayloads.qrToken(hex(o["bytes_hex"].str()))) }
    eq("qr len", 22, IdentityPayloads.newQrToken().length)

    for (r in d["sas"].arr()) {
        val o = r.obj(); val l = o["label"].str()
        val nh = hex(o["n_h_hex"].str()); val nc = hex(o["n_c_hex"].str())
        eqBytes("commit $l", o["commitment_hex"].str(), IdentityPayloads.sasCommitment(nc))
        val hs = spki(o["host_key"].str()); val cs = spki(o["client_key"].str())
        eqBytes("transcript $l", o["transcript_hex"].str(), IdentityPayloads.lp("ReyDesk/sas/v1".toByteArray(), hs, cs, nh, nc))
        val v = IdentityPayloads.sasValue(hs, cs, nh, nc)
        eq("sas $l", (o["sas_value"] as Long).toInt(), v)
        eq("sas digits $l", o["sas_digits"], IdentityPayloads.sasDigits(v))
        eq("sas display $l", o["sas_display"], IdentityPayloads.sasDisplay(v))
    }
    for (r in d["sas_commitment_check"].arr()) {
        val o = r.obj()
        eq("commit check ${o["label"]}", o["ok"], IdentityPayloads.sasCommitmentMatches(hex(o["commitment_hex"].str()), hex(o["revealed_n_c_hex"].str())))
    }
    for (r in d["pair_v2_payload"].arr()) {
        val o = r.obj(); val p = IdentityPayloads.pairV2(
            (o["method"] as Long).toInt(), o["token"].str(), spki(o["host_key"].str()), spki(o["client_key"].str()),
            o["requester_device_id"].str(), o["host_desk_id"].str(), hex(o["n_h_hex"].str()), hex(o["n_c_hex"].str()))
        eqBytes("pair ${o["label"]}", o["payload_hex"].str(), p)
        eqBytes("pair sha ${o["label"]}", o["payload_sha256_hex"].str(), MessageDigest.getInstance("SHA-256").digest(p))
    }
    for (r in d["attended_v3_payload"].arr()) {
        val o = r.obj(); val l = o["label"].str()
        val p = IdentityPayloads.attendedV3(spki(o["host_key"].str()), spki(o["client_key"].str()), o["requester_device_id"].str(),
            o["host_desk_id"].str(), o["requester_desk_id"].str(), hex(o["n_h_hex"].str()), hex(o["n_c_hex"].str()))
        eqBytes("attended $l", o["payload_hex"].str(), p)
        eqBytes("attended sha $l", o["payload_sha256_hex"].str(), MessageDigest.getInstance("SHA-256").digest(p))
    }
    var badNonce = 0
    for ((h, c) in listOf(31 to 32, 33 to 32, 32 to 31, 32 to 0, 0 to 32)) {
        try { IdentityPayloads.attendedV3(ByteArray(91), ByteArray(91), "d", "", "", ByteArray(h), ByteArray(c)) } catch (_: IllegalArgumentException) { badNonce++ }
    }
    eq("attended rejects non-32-byte nonces", 5, badNonce)
    for (r in d["rendezvous_auth_payload"].arr()) {
        val o = r.obj()
        val p = IdentityPayloads.rendezvousAuth(hex(o["nonce_hex"].str()), o["public_ws_url"].str(), spki(o["key"].str()), o["desk_id"].str())
        eqBytes("auth ${o["label"]}", o["payload_hex"].str(), p)
    }
    for (r in d["revoke_self_payload"].arr()) {
        val o = r.obj(); eqBytes("revoke", o["payload_hex"].str(), IdentityPayloads.revokeSelf(hex(o["nonce_hex"].str())))
    }
    for (r in d["pair_slot_code_hash"].arr()) {
        val o = r.obj()
        eqBytes("slot hash", o["code_hash_hex"].str(), IdentityPayloads.pairSlotCodeHash(o["desk_id"].str(), o["code"].str(), hex(o["salt_hex"].str())))
    }
    // signatures, including negatives
    for (r in d["signature_checks"].arr()) {
        val o = r.obj()
        eq("sig ${o["label"]}", o["expect_valid"], IdentitySignatures.verify(spki(o["key"].str()), hex(o["payload_hex"].str()), hex(o["signature_der_hex"].str())))
    }
    // sign/verify roundtrip with random k, and wrong-key rejection
    val payload = IdentityPayloads.revokeSelf(ByteArray(32) { it.toByte() })
    val sig = IdentitySignatures.sign(priv("client"), payload)
    eq("roundtrip", true, IdentitySignatures.verify(spki("client"), payload, sig))
    eq("roundtrip wrong key", false, IdentitySignatures.verify(spki("host"), payload, sig))
    eq("garbage spki", false, IdentitySignatures.verify(byteArrayOf(1, 2, 3), payload, sig))

    if (failures.isNotEmpty()) { failures.forEach { System.err.println("FAIL $it") }; System.exit(1) }
    println("PASS IdentityV2Android ($checks checks)")
}
