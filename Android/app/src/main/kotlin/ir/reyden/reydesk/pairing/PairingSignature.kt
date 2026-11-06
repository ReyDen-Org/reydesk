package ir.reyden.reydesk.pairing

import ir.reyden.reydesk.identity.IdentityPayloads
import java.nio.charset.StandardCharsets
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream

/**
 * Byte layouts a pairing signature is computed over. Must match
 * Windows/ReyDesk.Windows/Pairing/PairingSignature.cs byte-for-byte; v2 layouts are pinned by
 * Tests/Vectors/identity-v2.json. The v1 payload (token || host key || device id, no domain
 * separation) is gone: v1 requests are never produced and a host that answers one is a downgrade.
 */
object PairingSignature {
    private val Domain = "ReyDesk/pair/v2".toByteArray(StandardCharsets.UTF_8)

    fun buildReconnectPayload(hostProof: Boolean, challenge: ByteArray, clientNonce: ByteArray,
        hostId: String, clientId: String, hostKey: ByteArray, clientKey: ByteArray, expiresAt: Long): ByteArray {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { output ->
            listOf((if (hostProof) "ReyDesk/reconnect/host/v1" else "ReyDesk/reconnect/client/v1").toByteArray(StandardCharsets.UTF_8),
                challenge, clientNonce, hostId.toByteArray(StandardCharsets.UTF_8), clientId.toByteArray(StandardCharsets.UTF_8),
                hostKey, clientKey, expiresAt.toString().toByteArray(StandardCharsets.UTF_8)).forEach {
                output.writeInt(it.size); output.write(it)
            }
        }
        return bytes.toByteArray()
    }

    /**
     * v2 pair signature payload. [tokenBytes] is the UTF-8 token (QR token or canonical 6-digit code, empty otherwise);
     * taking bytes lets the caller wipe it. [hostDeskId] is canonical over a pipe, empty on the LAN medium.
     */
    fun buildSignedPayloadV2(method: Int, tokenBytes: ByteArray, hostSpki: ByteArray, clientSpki: ByteArray,
        requesterDeviceId: String, hostDeskId: String, nHost: ByteArray = ByteArray(0), nClient: ByteArray = ByteArray(0)): ByteArray =
        IdentityPayloads.lp(Domain, method.toString().toByteArray(StandardCharsets.UTF_8), tokenBytes, hostSpki, clientSpki,
            requesterDeviceId.toByteArray(StandardCharsets.UTF_8), hostDeskId.toByteArray(StandardCharsets.UTF_8), nHost, nClient)

    /** M5 attended v3 payload (docs/identity-and-pairing.md 2.8); method 5 never uses [buildSignedPayloadV2]. */
    fun buildAttendedV3(hostSpki: ByteArray, clientSpki: ByteArray, requesterDeviceId: String, hostDeskId: String,
        requesterDeskId: String, nHost: ByteArray, nClient: ByteArray): ByteArray =
        IdentityPayloads.attendedV3(hostSpki, clientSpki, requesterDeviceId, hostDeskId, requesterDeskId, nHost, nClient)

    fun buildSasCommitment(nClient: ByteArray): ByteArray = IdentityPayloads.sasCommitment(nClient)

    fun computeSas(hostSpki: ByteArray, clientSpki: ByteArray, nHost: ByteArray, nClient: ByteArray): Int =
        IdentityPayloads.sasValue(hostSpki, clientSpki, nHost, nClient)
}
