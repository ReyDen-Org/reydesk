package ir.reyden.reydesk.pairing

import ir.reyden.reydesk.identity.DeskIdCodec
import java.util.Base64

/** Typed pairing code for internet pairing (M4): exactly six digits, Persian/Arabic digits accepted. */
object RemoteCode {
    fun normalize(input: String): String? =
        (DeskIdCodec.parseRemoteCode(input) as? DeskIdCodec.Result.Ok)?.value
}

/**
 * Public mDNS metadata. Everything here is unauthenticated: [publicKey] is pinned for TLS but only
 * trusted after the SAS comparison. [deskId] and [keyId] are display/routing hints, never trust inputs.
 */
data class LanHostMetadata(val id: String, val name: String, val publicKey: ByteArray, val address: String, val port: Int,
    val deskId: String? = null, val keyId: String? = null) {
    fun hasKey(key: ByteArray): Boolean = java.security.MessageDigest.isEqual(publicKey, key)

    companion object {
        fun parse(properties: Map<String, String>, address: String, port: Int): LanHostMetadata? {
            if (properties["v"] != "1" || address.isBlank() || port !in 1..65535) return null
            val id = properties["id"]?.takeIf { it.isNotBlank() && it.length <= 128 } ?: return null
            val name = properties["name"]?.takeIf { it.isNotBlank() } ?: id
            val publicKey = try { Base64.getDecoder().decode(properties["key"] ?: return null) }
            catch (_: IllegalArgumentException) { return null }
            if (publicKey.size !in 32..256) return null
            // kid is derived from the key: a present but inconsistent value means a malformed or spoofed record.
            val derivedKeyId = DeskIdCodec.keyId(DeskIdCodec.fingerprint(publicKey))
            val kid = properties["kid"]
            val keyId = if (kid == null) null else {
                val normalized = kid.filterNot { it == '-' || it == ' ' }.uppercase()
                if (normalized != derivedKeyId) return null
                derivedKeyId
            }
            // A bad did is dropped (additive, hint only), it never rejects the host.
            val deskId = properties["did"]?.let { (DeskIdCodec.parse(it) as? DeskIdCodec.Result.Ok)?.value }
            return LanHostMetadata(id, name, publicKey, address, port, deskId, keyId)
        }
    }
}
