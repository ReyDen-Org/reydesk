package ir.reyden.reydesk.pairing

import com.google.protobuf.ByteString
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.identity.IdentityPayloads
import ir.reyden.reydesk.protocol.v1.DeviceIdentity
import ir.reyden.reydesk.protocol.v1.Envelope
import ir.reyden.reydesk.protocol.v1.PairingMethod
import ir.reyden.reydesk.protocol.v1.PairingRequest
import ir.reyden.reydesk.protocol.v1.PairingResponse
import ir.reyden.reydesk.protocol.v1.PairingResult
import ir.reyden.reydesk.protocol.v1.PairingStage
import ir.reyden.reydesk.protocol.v1.Platform
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.time.Instant
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.coroutineContext

/** The phone's own identity as the pairing protocol sees it (a Keystore-backed [DeviceIdentityService] in the app). */
interface PairingIdentity {
    val deviceId: String
    val displayName: String
    /** SPKI DER of the P-256 key. */
    val publicKey: ByteArray
    fun sign(data: ByteArray): ByteArray
}

/** Six-digit comparison code the user checks against the PC. [display] is "482 913" with ASCII digits (UI may localize). */
class SasPrompt(val value: Int) {
    val digits: String get() = IdentityPayloads.sasDigits(value)
    val display: String get() = IdentityPayloads.sasDisplay(value)
}

/** Return true only when the user states the code on the PC matches. Any other outcome cancels the pairing. */
fun interface SasConfirmer {
    suspend fun confirm(sas: SasPrompt): Boolean
}

class PairingCancelledException : IOException("Pairing cancelled: codes were not confirmed.")
class PairingRejectedException(message: String) : IOException(message)

/** A raw duplex byte stream with TLS already established and pinned (SSLSocket or TlsPipeStream). */
class PairLink(val input: InputStream, val output: OutputStream, val close: () -> Unit)

/**
 * Everything one pairing or reconnect attempt needs. [token] (QR token or code bytes) is wiped by [PairingSession] when done.
 * [hostDeskIdForSignature] is the canonical Desk ID of the pipe target, empty on the LAN medium.
 */
/** BuildConfig.APP_VERSION (from ReyDesk/VERSION), read reflectively so the standalone Kotlin test runners compile without the Android build. */
private fun defaultAppVersion(): String =
    runCatching { Class.forName("ir.reyden.reydesk.BuildConfig").getField("APP_VERSION").get(null) as String }.getOrDefault("0.0.0")

class PairParams(
    val method: PairingMethod,
    val hostKey: ByteArray,
    val hostIdentity: DeviceIdentity,
    val hostDeskIdForSignature: String,
    val token: ByteArray,
    val identity: PairingIdentity,
    val trust: HostTrust,
    /** Trust proof recorded on success for a new host; null for trusted reconnects. */
    val firstUse: FirstUseProof?,
    /** Value stored for the per-host remote flag on success; null leaves an existing value unchanged. */
    val remoteAllowedOnSuccess: Boolean?,
    val deskIdHint: String?,
    val appVersion: String = defaultAppVersion(),
    /** M5 only: the phone's own canonical Desk ID over a pipe, empty on the LAN medium (both Desk IDs empty or both set). */
    val requesterDeskIdForSignature: String = "",
)

class PairingFlowResult(val response: PairingResponse, val hostDeskId: String?)

/**
 * The phone side of every v2 pairing flow over an already pinned TLS stream (docs/identity-and-pairing.md 2.2 to 2.4).
 * Never logs or returns codes, keys, nonces or the SAS. Fails closed: any unexpected reply, stage, size or
 * downgrade (a host answering a v2 request like a v1 host) throws before trust is recorded.
 */
object PairingSession {
    private const val StageReplyMs = 10_000L
    private const val ApprovalMs = 75_000L
    private const val AttendedMs = 45_000L
    private const val MaxInterimReplies = 3
    private val random = SecureRandom()

    // ---- M1 / M3: QR token flow ----------------------------------------------------------------------------------

    suspend fun pairWithQr(link: PairLink, p: PairParams): PairingFlowResult = run(link, p) {
        require(p.method == PairingMethod.PAIRING_METHOD_LAN_QR || p.method == PairingMethod.PAIRING_METHOD_REMOTE_QR)
        require(p.token.size == QrTokenLength && p.firstUse == FirstUseProof.QR) { "Invalid QR pairing parameters." }
        p.trust.verifyHostKey(p.hostIdentity.deviceId, p.hostKey, FirstUseProof.QR)
        send(link, signedRequest(p, p.token, ByteArray(0), ByteArray(0), PairingStage.PAIRING_STAGE_UNSPECIFIED))
        finish(link, p, readFinal(link, ApprovalMs))
    }

    // ---- M2 / M4: SAS commit-then-reveal ------------------------------------------------------------------------

    suspend fun pairWithSas(link: PairLink, p: PairParams, confirm: SasConfirmer): PairingFlowResult = run(link, p) {
        require(p.method == PairingMethod.PAIRING_METHOD_LAN_SAS || p.method == PairingMethod.PAIRING_METHOD_REMOTE_CODE)
        require(p.firstUse == FirstUseProof.SAS_CONFIRMED) { "Invalid SAS pairing parameters." }
        if (p.method == PairingMethod.PAIRING_METHOD_REMOTE_CODE) require(p.token.size == 6 && p.hostDeskIdForSignature.isNotEmpty())
        else require(p.token.isEmpty())
        // The unauthenticated key may not replace a saved one; it is not trusted until the codes match and the PC accepts.
        p.trust.checkKeyUnchanged(p.hostIdentity.deviceId, p.hostKey)

        val nClient = IdentityPayloads.newNonce(random)
        var nHost = ByteArray(0)
        try {
            send(link, baseRequest(p).setSasCommitment(ByteString.copyFrom(IdentityPayloads.sasCommitment(nClient))).build())
            val nonceReply = readResponse(link, StageReplyMs)
            // A host that ignores v2 (or skips SAS) must not be able to finish pairing: only the nonce stage is valid here.
            if (nonceReply.stage != PairingStage.PAIRING_STAGE_SAS_HOST_NONCE ||
                nonceReply.result != PairingResult.PAIRING_RESULT_UNSPECIFIED || nonceReply.sasHostNonce.size() != 32) {
                if (nonceReply.result == PairingResult.PAIRING_RESULT_REJECTED || nonceReply.result == PairingResult.PAIRING_RESULT_EXPIRED_TOKEN)
                    throw PairingRejectedException(nonceReply.reason.ifEmpty { nonceReply.result.name })
                throw PairingRejectedException("The PC did not start the code comparison. Update ReyDesk on both devices.")
            }
            nHost = nonceReply.sasHostNonce.toByteArray()

            send(link, baseRequest(p).setStage(PairingStage.PAIRING_STAGE_SAS_REVEAL).setSasClientNonce(ByteString.copyFrom(nClient)).build())
            val sas = SasPrompt(IdentityPayloads.sasValue(p.hostKey, p.identity.publicKey, nHost, nClient))
            if (!confirm.confirm(sas)) throw PairingCancelledException()
            coroutineContext.ensureActive()

            // Signature and (for M4) the code only leave the phone after the user confirmed the SAS.
            send(link, signedRequest(p, p.token, nHost, nClient, PairingStage.PAIRING_STAGE_SAS_CONFIRM))
            finish(link, p, readFinal(link, ApprovalMs))
        } finally {
            nClient.fill(0); nHost.fill(0)
        }
    }

    // ---- M5: trusted attended reconnect -------------------------------------------------------------------------

    suspend fun reconnectAttended(link: PairLink, p: PairParams): PairingFlowResult = run(link, p) {
        require(p.method == PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED && p.firstUse == null && p.token.isEmpty())
        require((p.hostDeskIdForSignature.isEmpty()) == (p.requesterDeskIdForSignature.isEmpty())) { "Invalid Desk IDs for reconnect." }
        p.trust.verifyHostKey(p.hostIdentity.deviceId, p.hostKey, null)
        val nClient = IdentityPayloads.newNonce(random)
        var nHost = ByteArray(0)
        try {
            // Step 1: unsigned request carrying only n_c. No signature leaves the phone before the host issues a fresh n_h.
            send(link, baseRequest(p).setAttendedClientNonce(ByteString.copyFrom(nClient)).build())
            val challenge = readResponse(link, StageReplyMs)
            if (challenge.stage != PairingStage.PAIRING_STAGE_ATTENDED_CHALLENGE ||
                challenge.result != PairingResult.PAIRING_RESULT_UNSPECIFIED || challenge.attendedHostNonce.size() != 32) {
                if (challenge.result == PairingResult.PAIRING_RESULT_REJECTED || challenge.result == PairingResult.PAIRING_RESULT_EXPIRED_TOKEN)
                    throw PairingRejectedException(challenge.reason.ifEmpty { challenge.result.name })
                throw PairingRejectedException("Update ReyDesk on the PC.")
            }
            nHost = challenge.attendedHostNonce.toByteArray()
            coroutineContext.ensureActive()
            val payload = PairingSignature.buildAttendedV3(p.hostKey, p.identity.publicKey, p.identity.deviceId,
                p.hostDeskIdForSignature, p.requesterDeskIdForSignature, nHost, nClient)
            val signature = p.identity.sign(payload)
            send(link, baseRequest(p).setStage(PairingStage.PAIRING_STAGE_ATTENDED_PROOF)
                .setSignature(ByteString.copyFrom(signature)).build())
            val response = readFinal(link, AttendedMs)
            p.trust.verifyHostKey(p.hostIdentity.deviceId, p.hostKey, null)
            finish(link, p, response)
        } finally {
            nClient.fill(0); nHost.fill(0)
        }
    }

    // ---- M6: trusted unattended reconnect (mutual signatures, unchanged from v1 apart from the method field) ---------

    suspend fun reconnectUnattended(link: PairLink, p: PairParams): PairingFlowResult = run(link, p) {
        require(p.method == PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED && p.firstUse == null && p.token.isEmpty())
        val hostKey = p.hostKey
        val hostId = p.hostIdentity.deviceId
        p.trust.verifyHostKey(hostId, hostKey, null)
        // No proof is signed until the pinned TLS handshake (done by the caller) has matched the saved key.
        val clientNonce = ByteArray(32).also { random.nextBytes(it) }
        val first = baseRequest(p).setUnattendedReconnect(true)
            .setReconnectClientNonce(ByteString.copyFrom(clientNonce)).build()
        send(link, first)
        val reply = readResponse(link, ApprovalMs)
        val challenge = reply.reconnectChallenge
        if (challenge.isEmpty) {
            // No challenge means the PC did not run the reconnect protocol; a success here would be a downgrade.
            if (reply.result == PairingResult.PAIRING_RESULT_ACCEPTED || reply.result == PairingResult.PAIRING_RESULT_ALREADY_TRUSTED)
                throw PairingRejectedException("PC did not perform secure reconnect challenge.")
            return@run PairingFlowResult(reply, null)
        }
        val expires = reply.reconnectExpiresAtUnixMs
        val now = System.currentTimeMillis()
        check(challenge.size() == 32 && reply.reconnectHostSignature.size() in 8..144 &&
            expires > now && expires <= now + 305_000) { "Invalid reconnect challenge." }
        val challengeBytes = challenge.toByteArray()
        val hostPayload = PairingSignature.buildReconnectPayload(true, challengeBytes, clientNonce, hostId,
            p.identity.deviceId, hostKey, p.identity.publicKey, expires)
        val hostVerified = try {
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(hostKey)))
                update(hostPayload); verify(reply.reconnectHostSignature.toByteArray())
            }
        } catch (e: java.security.GeneralSecurityException) { false }
        check(hostVerified) { "PC reconnect proof failed." }
        coroutineContext.ensureActive()
        val proof = p.identity.sign(PairingSignature.buildReconnectPayload(false, challengeBytes, clientNonce, hostId,
            p.identity.deviceId, hostKey, p.identity.publicKey, expires))
        send(link, first.toBuilder().setSignature(ByteString.copyFrom(proof)).build())
        val last = readResponse(link, ApprovalMs)
        check(last.reconnectChallenge.isEmpty) { "No final reconnect response." }
        p.trust.verifyHostKey(hostId, hostKey, null)
        clientNonce.fill(0)
        finish(link, p, last)
    }

    // ---- internals -------------------------------------------------------------------------------------------------

    private suspend fun run(link: PairLink, p: PairParams, block: suspend () -> PairingFlowResult): PairingFlowResult {
        require(p.hostKey.size in 32..256 && p.hostIdentity.deviceId.length in 1..128) { "Invalid host identity." }
        try {
            return block()
        } catch (e: Exception) {
            runCatching { link.close() }
            if (e is CancellationException) throw e
            if (e is IOException || e is IllegalStateException || e is IllegalArgumentException || e is SecurityException) throw e
            throw IOException("Pairing failed: ${e.javaClass.simpleName}")
        } finally {
            p.token.fill(0)
        }
    }

    private fun baseRequest(p: PairParams): PairingRequest.Builder = PairingRequest.newBuilder()
        .setRequester(DeviceIdentity.newBuilder().setDeviceId(p.identity.deviceId)
            .setDisplayName(p.identity.displayName).setPlatform(Platform.PLATFORM_ANDROID).setAppVersion(p.appVersion).build())
        .setRequesterPublicKey(ByteString.copyFrom(p.identity.publicKey))
        .setMethod(p.method)

    /** The v2 signed request. The token is the QR token or the canonical code; empty for SAS-only and trusted methods. */
    private fun signedRequest(p: PairParams, token: ByteArray, nHost: ByteArray, nClient: ByteArray, stage: PairingStage): PairingRequest {
        val payload = PairingSignature.buildSignedPayloadV2(p.method.number, token, p.hostKey, p.identity.publicKey,
            p.identity.deviceId, p.hostDeskIdForSignature, nHost, nClient)
        val signature = p.identity.sign(payload)
        return baseRequest(p).setStage(stage).setPairingTokenBytes(ByteString.copyFrom(token))
            .setSignature(ByteString.copyFrom(signature)).build()
    }

    private suspend fun send(link: PairLink, request: PairingRequest) {
        val envelope = Envelope.newBuilder().setProtocolVersion(1).setSentAtUnixMs(Instant.now().toEpochMilli())
            .setPairingRequest(request).build()
        withContext(Dispatchers.IO) { EnvelopeFraming.write(link.output, envelope) }
    }

    private suspend fun readResponse(link: PairLink, timeoutMs: Long): PairingResponse {
        val timedOut = AtomicBoolean(false)
        val reply = coroutineScope {
            val watchdog = launch { delay(timeoutMs); timedOut.set(true); runCatching { link.close() } }
            try {
                withContext(Dispatchers.IO) { EnvelopeFraming.read(link.input) }
            } catch (e: IOException) {
                if (timedOut.get()) throw IOException("The PC did not respond in time.")
                throw e
            } finally {
                watchdog.cancel()
            }
        }
        if (reply == null || !reply.hasPairingResponse())
            throw PairingRejectedException("No valid pairing response. Update both ReyDesk apps and retry.")
        return reply.pairingResponse
    }

    /** Reads until a terminal response, skipping a bounded number of "waiting for approval" notices. */
    private suspend fun readFinal(link: PairLink, timeoutMs: Long): PairingResponse {
        repeat(MaxInterimReplies + 1) {
            val r = readResponse(link, timeoutMs)
            if (r.stage == PairingStage.PAIRING_STAGE_AWAITING_APPROVAL && r.result == PairingResult.PAIRING_RESULT_UNSPECIFIED) return@repeat
            return r
        }
        throw PairingRejectedException("The PC never answered the pairing request.")
    }

    /** Terminal reply: on success re-verify the key, record trust, and learn a valid Desk ID; otherwise surface the reason. */
    private suspend fun finish(link: PairLink, p: PairParams, r: PairingResponse): PairingFlowResult {
        val accepted = r.result == PairingResult.PAIRING_RESULT_ACCEPTED || r.result == PairingResult.PAIRING_RESULT_ALREADY_TRUSTED
        // A success that still carries an unfinished challenge, or an interim stage, is not a real success.
        if (accepted && (!r.reconnectChallenge.isEmpty || !r.attendedHostNonce.isEmpty ||
                r.stage == PairingStage.PAIRING_STAGE_SAS_HOST_NONCE || r.stage == PairingStage.PAIRING_STAGE_ATTENDED_CHALLENGE))
            throw PairingRejectedException("Unexpected pairing response.")
        if (!accepted) return PairingFlowResult(r, null)
        coroutineContext.ensureActive()
        val hostDeskId = (DeskIdCodec.parse(r.hostDeskId) as? DeskIdCodec.Result.Ok)?.value
            ?: p.deskIdHint?.let { (DeskIdCodec.parse(it) as? DeskIdCodec.Result.Ok)?.value }
        p.trust.recordPaired(p.hostIdentity, p.hostKey, p.firstUse, p.method, p.remoteAllowedOnSuccess, hostDeskId)
        return PairingFlowResult(r, hostDeskId)
    }

    const val QrTokenLength = 22

    fun isQrToken(token: String): Boolean = token.length == QrTokenLength &&
        token.all { it in 'A'..'Z' || it in 'a'..'z' || it in '0'..'9' || it == '-' || it == '_' }

    /** Constant-time equality for key/SPKI comparisons outside TLS (for example the server-reported pipe peer key). */
    fun keysEqual(a: ByteArray, b: ByteArray): Boolean = MessageDigest.isEqual(a, b)
}
