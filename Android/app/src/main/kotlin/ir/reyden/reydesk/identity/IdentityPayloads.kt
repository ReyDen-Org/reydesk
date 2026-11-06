package ir.reyden.reydesk.identity

import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

/** Byte-exact payload builders for ReyDesk v2 (see Tests/Vectors/identity-v2.json "notes"). */
object IdentityPayloads {
    private val EMPTY = ByteArray(0)
    private fun utf8(s: String) = s.toByteArray(Charsets.UTF_8)

    /** LP(x) = 4-byte big-endian length, then x, for each part in order. */
    fun lp(vararg parts: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        for (p in parts) {
            val n = p.size
            out.write(n ushr 24); out.write(n ushr 16); out.write(n ushr 8); out.write(n)
            out.write(p)
        }
        return out.toByteArray()
    }

    private fun sha256(vararg parts: ByteArray): ByteArray {
        val md = MessageDigest.getInstance("SHA-256")
        for (p in parts) md.update(p)
        return md.digest()
    }

    /** [method] is the PairingMethod enum number; [hostDeskId] canonical over a pipe, empty on LAN. */
    fun pairV2(
        method: Int, token: String, hostSpki: ByteArray, clientSpki: ByteArray,
        requesterDeviceId: String, hostDeskId: String, nHost: ByteArray = EMPTY, nClient: ByteArray = EMPTY,
    ): ByteArray = lp(
        utf8("ReyDesk/pair/v2"), utf8(method.toString()), utf8(token), hostSpki, clientSpki,
        utf8(requesterDeviceId), utf8(hostDeskId), nHost, nClient,
    )

    /**
     * M5 attended proof (spec 2.8). Desk IDs are both empty on LAN and both canonical over a pipe; nonces exactly 32 bytes.
     */
    fun attendedV3(
        hostSpki: ByteArray, clientSpki: ByteArray, requesterDeviceId: String,
        hostDeskId: String, requesterDeskId: String, nHost: ByteArray, nClient: ByteArray,
    ): ByteArray {
        require(nHost.size == 32 && nClient.size == 32) { "Attended nonces must be 32 bytes." }
        return lp(
            utf8("ReyDesk/attended/client/v3"), hostSpki, clientSpki, utf8(requesterDeviceId),
            utf8(hostDeskId), utf8(requesterDeskId), nHost, nClient,
        )
    }

    fun rendezvousAuth(serverNonce: ByteArray, publicWsUrl: String, spki: ByteArray, deskId: String): ByteArray =
        lp(utf8("ReyDesk/rendezvous/auth/v1"), serverNonce, utf8(publicWsUrl), spki, utf8(deskId))

    fun revokeSelf(serverNonce: ByteArray): ByteArray = lp(utf8("ReyDesk/rendezvous/revoke/v1"), serverNonce)

    fun pairSlotCodeHash(deskId: String, code: String, salt: ByteArray): ByteArray =
        sha256(lp(utf8(deskId), utf8(code), salt))

    /** SHA256("ReyDesk/sas-commit/v1" || n_c), plain concatenation, no length prefix. */
    fun sasCommitment(nClient: ByteArray): ByteArray = sha256(utf8("ReyDesk/sas-commit/v1"), nClient)

    fun sasCommitmentMatches(commitment: ByteArray, revealedNClient: ByteArray): Boolean =
        MessageDigest.isEqual(commitment, sasCommitment(revealedNClient))

    /** Six-digit SAS value (0..999999). */
    fun sasValue(hostSpki: ByteArray, clientSpki: ByteArray, nHost: ByteArray, nClient: ByteArray): Int {
        val h = sha256(lp(utf8("ReyDesk/sas/v1"), hostSpki, clientSpki, nHost, nClient))
        val u = ((h[0].toLong() and 0xFF) shl 24) or ((h[1].toLong() and 0xFF) shl 16) or
            ((h[2].toLong() and 0xFF) shl 8) or (h[3].toLong() and 0xFF)
        return (u % 1_000_000L).toInt()
    }

    fun sasDigits(value: Int): String = value.toString().padStart(6, '0')

    fun sasDisplay(value: Int): String = sasDigits(value).let { it.substring(0, 3) + " " + it.substring(3) }

    /** 16 random bytes as base64url, no padding (22 chars). */
    fun qrToken(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)

    fun newQrToken(random: SecureRandom = SecureRandom()): String = qrToken(ByteArray(16).also { random.nextBytes(it) })

    fun newNonce(random: SecureRandom = SecureRandom()): ByteArray = ByteArray(32).also { random.nextBytes(it) }
}
