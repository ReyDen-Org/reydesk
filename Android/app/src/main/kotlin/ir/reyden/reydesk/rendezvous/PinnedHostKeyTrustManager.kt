package ir.reyden.reydesk.rendezvous

import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.X509TrustManager

/**
 * Exact-key host pinning, identical in behavior to the trust manager in
 * pairing/PairingClient.kt (LAN path): no CA, no system trust, no fallback. The leaf
 * certificate must be currently valid and its SubjectPublicKeyInfo must equal [expectedSpki]
 * byte for byte.
 */
class PinnedHostKeyTrustManager(expectedSpki: ByteArray) : X509TrustManager {
    private val expected = expectedSpki.copyOf()

    init {
        require(expected.size in 32..256) { "Invalid pinned host key." }
    }

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        throw CertificateException("Client certificate validation is not used here")
    }

    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("Missing host certificate")
        leaf.checkValidity()
        if (!MessageDigest.isEqual(expected, leaf.publicKey.encoded))
            throw CertificateException("PC identity does not match the QR code or saved device")
    }
}
