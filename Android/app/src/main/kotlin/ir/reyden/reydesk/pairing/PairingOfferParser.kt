package ir.reyden.reydesk.pairing

import ir.reyden.reydesk.protocol.v1.PairingOffer
import java.util.Base64

/**
 * Decodes the `reydesk://pair/<base64url>` (legacy `reyscreen://` still accepted) QR payload produced by
 * Windows/ReyDesk.Windows/Pairing/PairingOfferFactory.cs back into a real
 * PairingOffer message - same schema, same source of truth (Protocol/reydesk).
 */
object PairingOfferParser {
    private val Schemes = listOf("reydesk://pair/", "reyscreen://pair/")

    fun parse(qrText: String): PairingOffer? {
        val scheme = Schemes.firstOrNull { qrText.startsWith(it) } ?: return null
        if (qrText.length > 4096) return null
        val encoded = qrText.removePrefix(scheme)
        return try {
            val bytes = Base64.getUrlDecoder().decode(padBase64Url(encoded))
            PairingOffer.parseFrom(bytes)
        } catch (_: Exception) {
            null
        }
    }

    // PairingOfferFactory.cs strips the '=' padding for URL-friendliness; restore
    // it since java.util.Base64's URL decoder still expects a padded length.
    private fun padBase64Url(value: String): String {
        val remainder = value.length % 4
        return if (remainder == 0) value else value + "=".repeat(4 - remainder)
    }
}
