import com.google.protobuf.ByteString
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.identity.IdentityPayloads
import ir.reyden.reydesk.identity.IdentitySignatures
import ir.reyden.reydesk.pairing.EnvelopeFraming
import ir.reyden.reydesk.pairing.FirstUseProof
import ir.reyden.reydesk.pairing.HostKeyRules
import ir.reyden.reydesk.pairing.HostTrust
import ir.reyden.reydesk.pairing.LanHostMetadata
import ir.reyden.reydesk.pairing.PairLink
import ir.reyden.reydesk.pairing.PairParams
import ir.reyden.reydesk.pairing.PairingCancelledException
import ir.reyden.reydesk.pairing.PairingIdentity
import ir.reyden.reydesk.pairing.PairingRejectedException
import ir.reyden.reydesk.pairing.PairingSession
import ir.reyden.reydesk.pairing.PairingSignature
import ir.reyden.reydesk.pairing.RemoteCode
import ir.reyden.reydesk.pairing.SasConfirmer
import ir.reyden.reydesk.pairing.StoredHostKey
import ir.reyden.reydesk.protocol.v1.DeviceIdentity
import ir.reyden.reydesk.protocol.v1.Envelope
import ir.reyden.reydesk.protocol.v1.PairingMethod
import ir.reyden.reydesk.protocol.v1.PairingRequest
import ir.reyden.reydesk.protocol.v1.PairingResponse
import ir.reyden.reydesk.protocol.v1.PairingResult
import ir.reyden.reydesk.protocol.v1.PairingStage
import kotlinx.coroutines.runBlocking
import java.io.File
import java.net.ServerSocket
import java.net.Socket
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread

private var passed = 0
private fun check(value: Boolean, label: String) { if (!value) { println("FAIL: $label"); throw AssertionError(label) }; passed++; println("PASS: $label") }
private inline fun <reified T : Throwable> throwsA(label: String, block: () -> Unit) {
    val e = try { block(); null } catch (t: Throwable) { t }
    check(e is T, "$label (got ${e?.javaClass?.simpleName})")
}
private fun hex(s: String) = ByteArray(s.length / 2) { s.substring(2 * it, 2 * it + 2).toInt(16).toByte() }

// Minimal JSON reader for the shared vectors file.
private class Json(val s: String) {
    var i = 0
    fun ws() { while (i < s.length && s[i].isWhitespace()) i++ }
    fun value(): Any? {
        ws()
        return when (val c = s[i]) {
            '{' -> { i++; val m = LinkedHashMap<String, Any?>(); ws()
                if (s[i] == '}') { i++; return m }
                while (true) { ws(); val k = str(); ws(); i++; m[k] = value(); ws(); if (s[i++] == '}') return m } }
            '[' -> { i++; val l = ArrayList<Any?>(); ws()
                if (s[i] == ']') { i++; return l }
                while (true) { l.add(value()); ws(); if (s[i++] == ']') return l } }
            '"' -> str()
            't' -> { i += 4; true }
            'f' -> { i += 5; false }
            'n' -> { i += 4; null }
            else -> { val st = i; while (i < s.length && (s[i] == '-' || s[i].isDigit())) i++
                if (st == i) error("bad json at $i: $c"); s.substring(st, i).toLong() }
        }
    }
    fun str(): String {
        i++; val sb = StringBuilder()
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

private class TestKey(val pair: KeyPair) {
    val spki: ByteArray = pair.public.encoded
    fun sign(data: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run { initSign(pair.private); update(data); sign() }
}
private fun newKey() = TestKey(KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair())

private class TestIdentity(val key: TestKey, override val deviceId: String = "phone-1") : PairingIdentity {
    override val displayName = "Test phone"
    override val publicKey get() = key.spki
    override fun sign(data: ByteArray) = key.sign(data)
}

private class RecordingTrust(val existingKey: ByteArray? = null, val failCheck: Boolean = false) : HostTrust {
    val calls = ArrayList<String>()
    var recordedProof: FirstUseProof? = null
    var recorded = false
    var recordedRemote: Boolean? = null
    var recordedDeskId: String? = null
    var recordedMethod: PairingMethod? = null
    override suspend fun verifyHostKey(deviceId: String, publicKey: ByteArray, firstUse: FirstUseProof?) {
        calls += "verify"
        if (failCheck) error("key changed")
        HostKeyRules.verify(existingKey?.let { StoredHostKey(it, false) }, publicKey, firstUse)
    }
    override suspend fun checkKeyUnchanged(deviceId: String, publicKey: ByteArray) {
        calls += "check"
        if (failCheck) error("key changed")
    }
    override suspend fun recordPaired(host: DeviceIdentity, publicKey: ByteArray, firstUse: FirstUseProof?,
        method: PairingMethod, remoteAllowed: Boolean?, deskId: String?) {
        recorded = true; recordedProof = firstUse; recordedRemote = remoteAllowed; recordedDeskId = deskId; recordedMethod = method
    }
}

private class Harness(val script: (Socket) -> Unit) {
    val server = ServerSocket(0, 1, java.net.InetAddress.getLoopbackAddress())
    val error = AtomicReference<Throwable?>()
    val hostThread = thread(isDaemon = true) {
        try { server.accept().use { script(it) } } catch (t: Throwable) { error.set(t) }
    }
    val client = Socket(java.net.InetAddress.getLoopbackAddress(), server.localPort).apply { soTimeout = 15000 }
    val link = PairLink(client.getInputStream(), client.getOutputStream()) { runCatching { client.close() } }
    fun finish() { runCatching { client.close() }; hostThread.join(3000); server.close() }
}

private fun readReq(s: Socket): PairingRequest = EnvelopeFraming.read(s.getInputStream())!!.pairingRequest
private fun writeResp(s: Socket, r: PairingResponse.Builder) =
    EnvelopeFraming.write(s.getOutputStream(), Envelope.newBuilder().setProtocolVersion(1).setPairingResponse(r).build())
private fun ok(desk: String = "") = PairingResponse.newBuilder().setResult(PairingResult.PAIRING_RESULT_ACCEPTED).setHostDeskId(desk)
private fun noReadable(s: Socket, ms: Long = 250): Boolean { Thread.sleep(ms); return s.getInputStream().available() == 0 }

private val hostId = DeviceIdentity.newBuilder().setDeviceId("host-1").setDisplayName("PC").build()
private const val DeskA = "JGCEXMGVAQ1C"
private const val DeskB = "6XQM3JA8GZF4"

private fun params(method: PairingMethod, host: TestKey, phone: TestIdentity, trust: HostTrust, token: ByteArray = ByteArray(0),
    firstUse: FirstUseProof? = null, remote: Boolean? = null, deskSig: String = "", requesterDesk: String = "") =
    PairParams(method, host.spki, hostId, deskSig, token, phone, trust, firstUse, remote, null, requesterDeskIdForSignature = requesterDesk)

private fun m5Verify(r: PairingRequest, hostKey: TestKey, phone: TestIdentity, hostDesk: String, reqDesk: String, nH: ByteArray, nC: ByteArray): Boolean {
    val payload = IdentityPayloads.attendedV3(hostKey.spki, phone.publicKey, phone.deviceId, hostDesk, reqDesk, nH, nC)
    return IdentitySignatures.verify(phone.publicKey, payload, r.signature.toByteArray())
}

private fun verifySig(r: PairingRequest, hostKey: TestKey, phone: TestIdentity, method: Int, token: String, deskId: String,
    nH: ByteArray = ByteArray(0), nC: ByteArray = ByteArray(0)): Boolean {
    val payload = IdentityPayloads.pairV2(method, token, hostKey.spki, phone.publicKey, phone.deviceId, deskId, nH, nC)
    return IdentitySignatures.verify(phone.publicKey, payload, r.signature.toByteArray())
}

private fun vectorChecks(path: String) {
    val d = Json(File(path).readText(Charsets.UTF_8)).value().obj()
    val keys = d["keys"].obj()
    fun spki(name: String) = hex(keys[name].obj()["spki_der_hex"] as String)
    var n = 0
    for (row in d["pair_v2_payload"].arr()) {
        val r = row.obj()
        val payload = PairingSignature.buildSignedPayloadV2((r["method"] as Long).toInt(), (r["token"] as String).toByteArray(),
            spki(r["host_key"] as String), spki(r["client_key"] as String), r["requester_device_id"] as String,
            r["host_desk_id"] as String, hex(r["n_h_hex"] as String), hex(r["n_c_hex"] as String))
        check(payload.contentEquals(hex(r["payload_hex"] as String)), "v2 pair payload matches vector: ${r["label"]}")
        n++
    }
    var attended = 0
    for (row in d["attended_v3_payload"].arr()) {
        val r = row.obj()
        val payload = PairingSignature.buildAttendedV3(spki(r["host_key"] as String), spki(r["client_key"] as String),
            r["requester_device_id"] as String, r["host_desk_id"] as String, r["requester_desk_id"] as String,
            hex(r["n_h_hex"] as String), hex(r["n_c_hex"] as String))
        check(payload.contentEquals(hex(r["payload_hex"] as String)), "attended v3 payload matches vector: ${r["label"]}")
        attended++
    }
    check(attended >= 3, "attended v3 vectors present")
    var m5Negatives = 0
    for (row in d["signature_checks"].arr()) {
        val r = row.obj()
        val label = r["label"] as String
        if (r["group"] != "pair_v2_payload" && r["group"] != "attended_v3_payload" && !label.contains("M5")) continue
        if (label.startsWith("NEG") && label.contains("M5")) m5Negatives++
        val actual = IdentitySignatures.verify(spki(r["key"] as String), hex(r["payload_hex"] as String), hex(r["signature_der_hex"] as String))
        check(actual == (r["expect_valid"] as Boolean), "v2 pair signature vector verifies as expected: ${r["label"]}")
    }
    for (row in d["sas"].arr()) {
        val r = row.obj()
        val v = PairingSignature.computeSas(spki(r["host_key"] as String), spki(r["client_key"] as String), hex(r["n_h_hex"] as String), hex(r["n_c_hex"] as String))
        check(IdentityPayloads.sasDigits(v) == r["sas_digits"], "SAS matches vector: ${r["label"]}")
        check(PairingSignature.buildSasCommitment(hex(r["n_c_hex"] as String)).contentEquals(hex(r["commitment_hex"] as String)), "SAS commitment matches vector: ${r["label"]}")
    }
    for (row in d["remote_code_parse"].arr()) {
        val r = row.obj()
        check(RemoteCode.normalize(r["input"] as String) == r["canonical"], "RemoteCode.normalize vector: ${r["label"]}")
    }
    check(n > 0, "pair v2 vectors present")
    check(m5Negatives >= 6, "six M5 negative signature vectors present (replay, legacy, cross-domain, swaps, attacker key)")
}

private fun metadataChecks() {
    val key = ByteArray(91) { 3 }
    val kid = DeskIdCodec.keyId(DeskIdCodec.fingerprint(key))
    val base = mapOf("v" to "1", "id" to "desktop", "name" to "My PC", "key" to Base64.getEncoder().encodeToString(key))
    val m = LanHostMetadata.parse(base + mapOf("did" to "jgce-xmgv-aq1c", "kid" to DeskIdCodec.display(kid)), "192.168.1.42", 47821)
    check(m != null && m.deskId == DeskA && m.keyId == kid, "TXT did and kid are parsed and normalized")
    check(LanHostMetadata.parse(base, "192.168.1.42", 47821)?.deskId == null, "old host without did/kid still parses")
    check(LanHostMetadata.parse(base + ("kid" to "0000-0000-0000"), "192.168.1.42", 47821) == null, "kid that does not match the key is rejected")
    check(LanHostMetadata.parse(base + ("did" to "JGCEXMGVAQ1D"), "192.168.1.42", 47821)?.deskId == null, "bad Desk ID check char is dropped, host kept")
    check(LanHostMetadata.parse(base + ("did" to "JGCEXMGVAQ1D"), "192.168.1.42", 47821) != null, "bad did does not reject the host")
    check(LanHostMetadata.parse(base + ("v" to "2"), "192.168.1.42", 47821) == null, "unsupported discovery version rejected")
    check(RemoteCode.normalize("۱۲۳ ۴۵۶") == "123456", "RemoteCode accepts Persian digits")
    check(RemoteCode.normalize("١٢٣-٤٥٦") == "123456", "RemoteCode accepts Arabic-Indic digits")
    check(RemoteCode.normalize("12345") == null && RemoteCode.normalize("1234567") == null && RemoteCode.normalize("12345a") == null, "RemoteCode rejects bad length and letters")
    check(m!!.hasKey(key) && !m.hasKey(ByteArray(91) { 4 }), "hasKey compares exactly")
}

private fun trustRuleChecks() {
    val k = ByteArray(91) { 1 }
    fun fails(existing: StoredHostKey?, key: ByteArray, p: FirstUseProof?) = try { HostKeyRules.verify(existing, key, p); false } catch (_: IllegalStateException) { true }
    check(fails(null, k, null), "unknown host without proof is refused")
    check(!fails(null, k, FirstUseProof.QR) && !fails(null, k, FirstUseProof.SAS_CONFIRMED), "unknown host with QR or confirmed SAS is allowed")
    check(!fails(StoredHostKey(k, false), k, null), "same saved key is allowed")
    check(fails(StoredHostKey(k, false), ByteArray(91) { 2 }, FirstUseProof.QR), "changed key is refused even with a first-use proof")
    check(fails(StoredHostKey(k, true), k, null), "revoked host is refused")
}

private fun m1Checks() = runBlocking {
    val host = newKey(); val phone = TestIdentity(newKey())
    val token = IdentityPayloads.newQrToken()
    val seen = AtomicReference<PairingRequest>()
    val h = Harness { s -> seen.set(readReq(s)); writeResp(s, ok(DeskA)) }
    val trust = RecordingTrust()
    val tokenBytes = token.toByteArray()
    val r = PairingSession.pairWithQr(h.link, params(PairingMethod.PAIRING_METHOD_LAN_QR, host, phone, trust, tokenBytes, FirstUseProof.QR))
    h.finish()
    val req = seen.get()
    check(r.response.result == PairingResult.PAIRING_RESULT_ACCEPTED && trust.recorded, "M1 accepted and trust recorded")
    check(trust.recordedProof == FirstUseProof.QR && trust.recordedMethod == PairingMethod.PAIRING_METHOD_LAN_QR, "M1 records QR proof and method")
    check(req.method == PairingMethod.PAIRING_METHOD_LAN_QR && req.pairingToken == token, "M1 request carries method and 22-char token")
    check(verifySig(req, host, phone, 1, token, ""), "M1 v2 signature verifies (LAN: empty host desk id)")
    check(!req.unattendedReconnect && req.sasCommitment.isEmpty, "M1 request has no reconnect or SAS fields")
    check(tokenBytes.all { it == 0.toByte() }, "token bytes wiped after use")
    check(r.hostDeskId == DeskA && trust.recordedDeskId == DeskA, "valid host_desk_id from the PC is recorded")

    val h2 = Harness { s -> readReq(s); writeResp(s, PairingResponse.newBuilder().setResult(PairingResult.PAIRING_RESULT_REJECTED).setReason("Update ReyDesk on the phone")) }
    val t2 = RecordingTrust()
    val r2 = PairingSession.pairWithQr(h2.link, params(PairingMethod.PAIRING_METHOD_LAN_QR, host, phone, t2, IdentityPayloads.newQrToken().toByteArray(), FirstUseProof.QR))
    h2.finish()
    check(r2.response.result == PairingResult.PAIRING_RESULT_REJECTED && !t2.recorded, "M1 rejection records no trust")

    val h3 = Harness { s -> readReq(s); writeResp(s, ok("NOT-A-DESK-ID")) }
    val t3 = RecordingTrust()
    val r3 = PairingSession.pairWithQr(h3.link, params(PairingMethod.PAIRING_METHOD_LAN_QR, host, phone, t3, IdentityPayloads.newQrToken().toByteArray(), FirstUseProof.QR))
    h3.finish()
    check(r3.hostDeskId == null && t3.recordedDeskId == null, "invalid host_desk_id from the PC is ignored")

    val h4 = Harness { s -> readReq(s) }
    throwsA<IllegalArgumentException>("M1 rejects a 6-digit token (code is not a QR token)") {
        runBlocking { PairingSession.pairWithQr(h4.link, params(PairingMethod.PAIRING_METHOD_LAN_QR, host, phone, RecordingTrust(), "123456".toByteArray(), FirstUseProof.QR)) }
    }
    h4.finish()
    // M3 uses the pipe target Desk ID inside the signature.
    val seen5 = AtomicReference<PairingRequest>()
    val h5 = Harness { s -> seen5.set(readReq(s)); writeResp(s, ok()) }
    val tk = IdentityPayloads.newQrToken()
    PairingSession.pairWithQr(h5.link, params(PairingMethod.PAIRING_METHOD_REMOTE_QR, host, phone, RecordingTrust(), tk.toByteArray(), FirstUseProof.QR, true, DeskA))
    h5.finish()
    check(verifySig(seen5.get(), host, phone, 3, tk, DeskA), "M3 signature binds the host Desk ID")
    check(!verifySig(seen5.get(), host, phone, 3, tk, ""), "M3 signature does not verify without the Desk ID (no replay as LAN)")
}

private class SasHost(val hostKey: TestKey, val phone: TestIdentity, val method: Int, val desk: String,
    val flavor: String = "good") {
    val nHost = ByteArray(32).also { SecureRandom().nextBytes(it) }
    val msgs = ArrayList<PairingRequest>()
    @Volatile var hostSas: Int = -1
    val sawSignature = AtomicBoolean(false)
    val commitmentOk = AtomicBoolean(false)
    fun script(s: Socket) {
        val first = readReq(s); msgs += first
        when (flavor) {
            "accept-early" -> { writeResp(s, ok()); return }
            "v1-reject" -> { writeResp(s, PairingResponse.newBuilder().setResult(PairingResult.PAIRING_RESULT_REJECTED).setReason("Update ReyDesk on the phone")); return }
            "short-nonce" -> { writeResp(s, PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_SAS_HOST_NONCE).setSasHostNonce(ByteString.copyFrom(ByteArray(16)))); return }
        }
        writeResp(s, PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_SAS_HOST_NONCE).setSasHostNonce(ByteString.copyFrom(nHost)))
        val reveal = readReq(s); msgs += reveal
        val nC = reveal.sasClientNonce.toByteArray()
        commitmentOk.set(IdentityPayloads.sasCommitmentMatches(first.sasCommitment.toByteArray(), nC))
        hostSas = IdentityPayloads.sasValue(hostKey.spki, phone.publicKey, nHost, nC)
        val confirm = try { readReq(s) } catch (_: Exception) { return }
        sawSignature.set(!confirm.signature.isEmpty); msgs += confirm
        writeResp(s, PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_AWAITING_APPROVAL))
        writeResp(s, ok(desk))
    }
}

private fun sasChecks() = runBlocking {
    val host = newKey(); val phone = TestIdentity(newKey())
    for (m in listOf(PairingMethod.PAIRING_METHOD_LAN_SAS, PairingMethod.PAIRING_METHOD_REMOTE_CODE)) {
        val remote = m == PairingMethod.PAIRING_METHOD_REMOTE_CODE
        val tag = if (remote) "M4" else "M2"
        val sh = SasHost(host, phone, m.number, if (remote) DeskA else "")
        val h = Harness(sh::script)
        val trust = RecordingTrust()
        val code = "048213".toByteArray()
        val shownSas = AtomicReference<Int>()
        val sigSeenAtConfirm = AtomicReference<Boolean>()
        val confirmer = SasConfirmer { sas ->
            shownSas.set(sas.value)
            Thread.sleep(300) // give a misbehaving phone time to send early
            sigSeenAtConfirm.set(sh.msgs.size >= 3)
            true
        }
        val r = PairingSession.pairWithSas(h.link, params(m, host, phone, trust, if (remote) code else ByteArray(0),
            FirstUseProof.SAS_CONFIRMED, if (remote) true else null, if (remote) DeskA else ""), confirmer)
        h.finish()
        check(r.response.result == PairingResult.PAIRING_RESULT_ACCEPTED && trust.recorded, "$tag accepted after SAS and recorded")
        check(trust.recordedProof == FirstUseProof.SAS_CONFIRMED, "$tag records SAS_CONFIRMED, not QR")
        check(trust.calls.first() == "check", "$tag only checks for a changed key before connecting (unauthenticated key not trusted yet)")
        check(sh.commitmentOk.get(), "$tag host verifies commitment against the revealed nonce")
        check(shownSas.get() == sh.hostSas, "$tag phone SAS equals host SAS")
        check(sh.msgs[0].sasCommitment.size() == 32 && sh.msgs[0].signature.isEmpty && sh.msgs[0].pairingToken.isEmpty(), "$tag first message has only the commitment (no signature, no code)")
        check(sh.msgs[1].stage == PairingStage.PAIRING_STAGE_SAS_REVEAL && sh.msgs[1].signature.isEmpty && sh.msgs[1].pairingToken.isEmpty(), "$tag reveal carries no signature and no code")
        check(sigSeenAtConfirm.get() == false, "$tag signature is not sent before the user confirms")
        val c = sh.msgs[2]
        check(c.stage == PairingStage.PAIRING_STAGE_SAS_CONFIRM && c.method == m, "$tag confirm stage and method")
        check(verifySig(c, host, phone, m.number, if (remote) "048213" else "", if (remote) DeskA else "", sh.nHost, c.let { sh.msgs[1].sasClientNonce.toByteArray() }), "$tag v2 signature over transcript with nonces verifies")
        check(if (remote) c.pairingToken == "048213" else c.pairingToken.isEmpty(), "$tag code only in the final confirm request (M4) and never for M2")
        if (remote) check(code.all { it == 0.toByte() }, "$tag code bytes wiped")
        check(trust.recordedRemote == (if (remote) true else null), "$tag remote flag: internet pairing sets true, LAN leaves default")
    }

    // User says the codes do not match.
    run {
        val sh = SasHost(host, phone, 2, "")
        val h = Harness(sh::script); val trust = RecordingTrust()
        throwsA<PairingCancelledException>("SAS mismatch by the user cancels pairing") {
            runBlocking { PairingSession.pairWithSas(h.link, params(PairingMethod.PAIRING_METHOD_LAN_SAS, host, phone, trust, ByteArray(0), FirstUseProof.SAS_CONFIRMED), SasConfirmer { false }) }
        }
        h.finish()
        check(!trust.recorded && sh.msgs.size == 2 && !sh.sawSignature.get(), "cancelled SAS: no trust, no signature ever sent")
    }
    // Downgrades and malformed host behavior.
    for ((flavor, label) in listOf("accept-early" to "host skipping SAS and accepting", "v1-reject" to "v1 host answering REJECTED", "short-nonce" to "short host nonce")) {
        val sh = SasHost(host, phone, 2, "", flavor)
        val h = Harness(sh::script); val trust = RecordingTrust()
        throwsA<PairingRejectedException>("SAS flow fails closed on $label") {
            runBlocking { PairingSession.pairWithSas(h.link, params(PairingMethod.PAIRING_METHOD_LAN_SAS, host, phone, trust, ByteArray(0), FirstUseProof.SAS_CONFIRMED), SasConfirmer { true }) }
        }
        h.finish()
        check(!trust.recorded, "no trust recorded after $label")
    }
    // Saved key changed: no bytes leave the phone.
    run {
        val sh = SasHost(host, phone, 2, "")
        val h = Harness(sh::script); val trust = RecordingTrust(failCheck = true)
        throwsA<IllegalStateException>("changed saved key stops SAS pairing before any message") {
            runBlocking { PairingSession.pairWithSas(h.link, params(PairingMethod.PAIRING_METHOD_LAN_SAS, host, phone, trust, ByteArray(0), FirstUseProof.SAS_CONFIRMED), SasConfirmer { true }) }
        }
        h.finish()
        check(sh.msgs.isEmpty(), "nothing was sent to the host with a changed key")
    }
    // Wrong parameter shapes.
    run {
        val h = Harness { }
        throwsA<IllegalArgumentException>("M4 refuses a non-6-digit code") {
            runBlocking { PairingSession.pairWithSas(h.link, params(PairingMethod.PAIRING_METHOD_REMOTE_CODE, host, phone, RecordingTrust(), "12345".toByteArray(), FirstUseProof.SAS_CONFIRMED, true, DeskA), SasConfirmer { true }) }
        }
        throwsA<IllegalArgumentException>("SAS pairing refuses a QR first-use proof") {
            runBlocking { PairingSession.pairWithSas(h.link, params(PairingMethod.PAIRING_METHOD_LAN_SAS, host, phone, RecordingTrust(), ByteArray(0), FirstUseProof.QR), SasConfirmer { true }) }
        }
        h.finish()
    }
}

private fun reconnectChecks() = runBlocking {
    val host = newKey(); val phone = TestIdentity(newKey())
    // M5 over a pipe: challenge then proof (spec 2.8), with an interim "waiting" notice before the accept.
    val nHostFixed = ByteArray(32) { (it + 1).toByte() }
    val reqs = java.util.concurrent.CopyOnWriteArrayList<PairingRequest>()
    fun m5Host(nonce: ByteArray, finalResp: (Socket) -> Unit) = Harness { s ->
        reqs.add(readReq(s))
        writeResp(s, PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_ATTENDED_CHALLENGE)
            .setAttendedHostNonce(ByteString.copyFrom(nonce)))
        reqs.add(readReq(s))
        finalResp(s)
    }
    val h = m5Host(nHostFixed) { s -> writeResp(s, PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_AWAITING_APPROVAL)); writeResp(s, ok()) }
    val trust = RecordingTrust(existingKey = host.spki)
    val r = PairingSession.reconnectAttended(h.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, trust, remote = true, deskSig = DeskA, requesterDesk = DeskB))
    h.finish()
    check(r.response.result == PairingResult.PAIRING_RESULT_ACCEPTED && trust.recorded && trust.recordedProof == null, "M5 accepted, refreshes a trusted record without a first-use proof")
    val first = reqs[0]; val proof = reqs[1]
    check(first.attendedClientNonce.size() == 32 && first.signature.isEmpty && first.stage == PairingStage.PAIRING_STAGE_UNSPECIFIED, "M5 first request is unsigned and carries a 32-byte n_c")
    check(first.pairingTokenBytes.isEmpty && first.sasCommitment.isEmpty && !first.unattendedReconnect, "M5 first request has no token, SAS or reconnect fields")
    check(proof.stage == PairingStage.PAIRING_STAGE_ATTENDED_PROOF && proof.attendedClientNonce.isEmpty && !proof.signature.isEmpty, "M5 proof has stage ATTENDED_PROOF, signature, and no nonce")
    check(m5Verify(proof, host, phone, DeskA, DeskB, nHostFixed, first.attendedClientNonce.toByteArray()), "M5 proof verifies against attended v3 over a pipe (both Desk IDs)")
    check(!m5Verify(proof, host, phone, "", "", nHostFixed, first.attendedClientNonce.toByteArray()), "M5 pipe proof does not verify with empty Desk IDs")
    check(!m5Verify(proof, host, phone, DeskA, DeskB, ByteArray(32) { 9 }, first.attendedClientNonce.toByteArray()), "M5 proof replayed against a fresh n_h does not verify")
    check(trust.recordedRemote == true, "M5 over a pipe marks remote allowed")

    // M5 over LAN: both Desk IDs empty; n_c differs per attempt.
    val reqsLan = java.util.concurrent.CopyOnWriteArrayList<PairingRequest>()
    val hLan = Harness { s ->
        reqsLan.add(readReq(s))
        writeResp(s, PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_ATTENDED_CHALLENGE).setAttendedHostNonce(ByteString.copyFrom(nHostFixed)))
        reqsLan.add(readReq(s)); writeResp(s, ok())
    }
    PairingSession.reconnectAttended(hLan.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, RecordingTrust(existingKey = host.spki)))
    hLan.finish()
    check(m5Verify(reqsLan[1], host, phone, "", "", nHostFixed, reqsLan[0].attendedClientNonce.toByteArray()), "M5 proof verifies against attended v3 over LAN (empty Desk IDs)")
    check(reqsLan[0].attendedClientNonce != first.attendedClientNonce, "n_c differs per attempt")

    // Phone behaviour against hostile or buggy hosts: it must throw and never send a proof.
    fun m5Refuses(label: String, reply: PairingResponse.Builder, expectReason: String? = null) {
        val sent = java.util.concurrent.CopyOnWriteArrayList<PairingRequest>()
        val hh = Harness { s -> sent.add(readReq(s)); writeResp(s, reply); noReadable(s) }
        val t = RecordingTrust(existingKey = host.spki)
        val e = try { runBlocking { PairingSession.reconnectAttended(hh.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, t)) }; null } catch (x: Throwable) { x }
        hh.finish()
        check(e is PairingRejectedException, "$label: phone throws (got ${e?.javaClass?.simpleName})")
        if (expectReason != null) check(e?.message == expectReason, "$label: reason surfaced (got ${e?.message})")
        check(sent.size == 1 && !t.recorded, "$label: nothing signed, no trust recorded")
    }
    fun challenge(n: Int) = PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_ATTENDED_CHALLENGE).setAttendedHostNonce(ByteString.copyFrom(ByteArray(n) { 7 }))
    m5Refuses("M5 31-byte host nonce", challenge(31))
    m5Refuses("M5 33-byte host nonce", challenge(33))
    m5Refuses("M5 empty host nonce", challenge(0))
    m5Refuses("M5 AWAITING_APPROVAL instead of a challenge", PairingResponse.newBuilder().setStage(PairingStage.PAIRING_STAGE_AWAITING_APPROVAL))
    m5Refuses("M5 ALREADY_TRUSTED instead of a challenge", ok().setResult(PairingResult.PAIRING_RESULT_ALREADY_TRUSTED))
    m5Refuses("M5 wrong stage (SAS host nonce)", challenge(32).setStage(PairingStage.PAIRING_STAGE_SAS_HOST_NONCE))
    m5Refuses("M5 challenge with a result set", challenge(32).setResult(PairingResult.PAIRING_RESULT_ACCEPTED))
    m5Refuses("M5 host rejection reason", PairingResponse.newBuilder().setResult(PairingResult.PAIRING_RESULT_REJECTED).setReason("Enable Allow over internet for this phone on the PC."),
        "Enable Allow over internet for this phone on the PC.")
    m5Refuses("M5 old host without the feature", PairingResponse.newBuilder(), "Update ReyDesk on the PC.")

    // A success that still carries the host nonce or the challenge stage is not a success.
    for ((label, extra) in listOf<Pair<String, (PairingResponse.Builder) -> PairingResponse.Builder>>(
        "carries attended_host_nonce" to { b -> b.setAttendedHostNonce(ByteString.copyFrom(nHostFixed)) },
        "has stage ATTENDED_CHALLENGE" to { b -> b.setStage(PairingStage.PAIRING_STAGE_ATTENDED_CHALLENGE) })) {
        val hs = m5Host(nHostFixed) { s -> writeResp(s, extra(ok())) }
        val t = RecordingTrust(existingKey = host.spki)
        throwsA<PairingRejectedException>("M5 success that $label is rejected") {
            runBlocking { PairingSession.reconnectAttended(hs.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, t)) }
        }
        hs.finish()
        check(!t.recorded, "M5 success that $label records no trust")
    }
    // Host declines after the proof: reason shown, no trust change.
    val hd = m5Host(nHostFixed) { s -> writeResp(s, PairingResponse.newBuilder().setResult(PairingResult.PAIRING_RESULT_REJECTED).setReason("Declined")) }
    val td = RecordingTrust(existingKey = host.spki)
    val rd = PairingSession.reconnectAttended(hd.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, td))
    hd.finish()
    check(rd.response.result == PairingResult.PAIRING_RESULT_REJECTED && !td.recorded, "M5 decline surfaces the rejection and records nothing")
    // Mismatched Desk ID inputs (one empty, one set) are refused before anything is sent.
    val hm = Harness { s -> readReq(s) }
    throwsA<IllegalArgumentException>("M5 requires both Desk IDs or neither") {
        runBlocking { PairingSession.reconnectAttended(hm.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, RecordingTrust(existingKey = host.spki), deskSig = DeskA)) }
    }
    hm.finish()
    // M5 for an unknown host is refused locally.
    val h2 = Harness { s -> readReq(s) }
    throwsA<IllegalStateException>("M5 refuses a host that is not already trusted") {
        runBlocking { PairingSession.reconnectAttended(h2.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, RecordingTrust(existingKey = null))) }
    }
    h2.finish()
    // M5 with a different stored key.
    val h3 = Harness { s -> readReq(s) }
    throwsA<IllegalStateException>("M5 refuses when the saved key differs") {
        runBlocking { PairingSession.reconnectAttended(h3.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED, host, phone, RecordingTrust(existingKey = newKey().spki))) }
    }
    h3.finish()

    // M6 full mutual challenge.
    fun unattendedHost(signWith: TestKey, expiresDelta: Long, flavor: String, got: AtomicReference<List<PairingRequest>>) = Harness { s ->
        val first = readReq(s)
        if (flavor == "accept-no-challenge") { writeResp(s, ok()); got.set(listOf(first)); return@Harness }
        val challenge = ByteArray(32).also { SecureRandom().nextBytes(it) }
        val expires = System.currentTimeMillis() + expiresDelta
        val hostSig = signWith.sign(PairingSignature.buildReconnectPayload(true, challenge, first.reconnectClientNonce.toByteArray(), "host-1",
            phone.deviceId, host.spki, phone.publicKey, expires))
        writeResp(s, PairingResponse.newBuilder().setReconnectChallenge(ByteString.copyFrom(challenge))
            .setReconnectHostSignature(ByteString.copyFrom(hostSig)).setReconnectExpiresAtUnixMs(expires))
        val second = try { readReq(s) } catch (_: Exception) { got.set(listOf(first)); return@Harness }
        got.set(listOf(first, second))
        val ok = IdentitySignatures.verify(phone.publicKey, PairingSignature.buildReconnectPayload(false, challenge, first.reconnectClientNonce.toByteArray(),
            "host-1", phone.deviceId, host.spki, phone.publicKey, expires), second.signature.toByteArray())
        writeResp(s, if (ok) ok() else PairingResponse.newBuilder().setResult(PairingResult.PAIRING_RESULT_REJECTED))
    }
    run {
        val got = AtomicReference<List<PairingRequest>>()
        val h6 = unattendedHost(host, 60_000, "good", got); val t6 = RecordingTrust(existingKey = host.spki)
        val r6 = PairingSession.reconnectUnattended(h6.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED, host, phone, t6))
        h6.finish()
        check(r6.response.result == PairingResult.PAIRING_RESULT_ACCEPTED && t6.recorded, "M6 mutual challenge accepted")
        check(got.get()[0].method == PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED && got.get()[0].unattendedReconnect && got.get()[0].signature.isEmpty, "M6 first request sets method, flag, and signs nothing yet")
        check(got.get()[0].reconnectClientNonce.size() == 32, "M6 uses a fresh 32-byte client nonce")
    }
    run {
        val got = AtomicReference<List<PairingRequest>>()
        val h6 = unattendedHost(newKey(), 60_000, "good", got)   // challenge signed by another key
        throwsA<IllegalStateException>("M6 refuses a host proof signed by a different key") {
            runBlocking { PairingSession.reconnectUnattended(h6.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED, host, phone, RecordingTrust(existingKey = host.spki))) }
        }
        h6.finish()
        check(got.get().size == 1, "M6 signs no client proof when the host proof is bad")
    }
    run {
        val got = AtomicReference<List<PairingRequest>>()
        val h6 = unattendedHost(host, -5_000, "good", got)
        throwsA<IllegalStateException>("M6 refuses an expired challenge") {
            runBlocking { PairingSession.reconnectUnattended(h6.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED, host, phone, RecordingTrust(existingKey = host.spki))) }
        }
        h6.finish()
    }
    run {
        val got = AtomicReference<List<PairingRequest>>()
        val h6 = unattendedHost(host, 60_000, "accept-no-challenge", got); val t6 = RecordingTrust(existingKey = host.spki)
        throwsA<PairingRejectedException>("M6 treats success without a challenge as a downgrade") {
            runBlocking { PairingSession.reconnectUnattended(h6.link, params(PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED, host, phone, t6)) }
        }
        h6.finish()
        check(!t6.recorded, "no trust change after the M6 downgrade attempt")
    }
}

fun main(args: Array<String>) {
    vectorChecks(args[0])
    metadataChecks()
    trustRuleChecks()
    m1Checks()
    sasChecks()
    reconnectChecks()
    println("$passed pairing security checks passed")
}
