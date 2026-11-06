package ir.reyden.reydesk.identity

import java.security.GeneralSecurityException
import java.security.KeyFactory
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.X509EncodedKeySpec

/** ECDSA P-256 / SHA-256, DER (X9.62) signatures, same format the existing trust code uses. */
object IdentitySignatures {
    fun sign(privateKey: PrivateKey, payload: ByteArray): ByteArray =
        Signature.getInstance("SHA256withECDSA").run {
            initSign(privateKey); update(payload); sign()
        }

    /** True only for a valid signature by the key in [spki]; malformed input returns false. */
    fun verify(spki: ByteArray, payload: ByteArray, signatureDer: ByteArray): Boolean = try {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki))
        Signature.getInstance("SHA256withECDSA").run {
            initVerify(key); update(payload); verify(signatureDer)
        }
    } catch (_: GeneralSecurityException) {
        false
    }
}
