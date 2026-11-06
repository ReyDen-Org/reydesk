package ir.reyden.reydesk.pairing

import ir.reyden.reydesk.protocol.v1.DeviceIdentity
import ir.reyden.reydesk.protocol.v1.PairingMethod
import java.security.MessageDigest

/** Why a host key may be trusted for the first time. There is no other way in: no PIN, no TOFU. */
enum class FirstUseProof {
    /** Key came from a scanned QR code (authenticated out of band). */
    QR,
    /** Key came from mDNS or the rendezvous server (unauthenticated) and the user confirmed the SAS. */
    SAS_CONFIRMED,
}

class StoredHostKey(val key: ByteArray, val revoked: Boolean)

/** Pure exact-key rule shared by the store and the tests. Fails closed with IllegalStateException. */
object HostKeyRules {
    fun verify(existing: StoredHostKey?, presented: ByteArray, firstUse: FirstUseProof?) {
        if (existing == null) {
            check(firstUse != null) { "Scan this PC's QR code or compare the codes to pair with a new PC." }
            return
        }
        check(!existing.revoked && MessageDigest.isEqual(existing.key, presented)) {
            "This PC's identity key changed. Verify the PC, forget it, then pair again."
        }
    }
}

/** What the pairing protocol needs from the trust store (kept an interface so the protocol runs without Android). */
interface HostTrust {
    /** [firstUse] == null means the host must already be trusted with exactly [publicKey]. */
    suspend fun verifyHostKey(deviceId: String, publicKey: ByteArray, firstUse: FirstUseProof?)

    /** Only fails when a record exists whose key differs or that is revoked. Does not authorize a new host. */
    suspend fun checkKeyUnchanged(deviceId: String, publicKey: ByteArray)

    /**
     * Adds or refreshes a host after a successful, host-accepted pairing or reconnect.
     * [remoteAllowed] null leaves an existing value unchanged (new records start false).
     */
    suspend fun recordPaired(host: DeviceIdentity, publicKey: ByteArray, firstUse: FirstUseProof?,
        method: PairingMethod, remoteAllowed: Boolean?, deskId: String?)
}
