package ir.reyden.reydesk.identity

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID

private val Context.identityDataStore by preferencesDataStore(name = "reydesk_identity")

/**
 * This device's long-term identity: a stable device ID and an EC P-256 keypair held
 * in the Android Keystore (hardware-backed where the device supports it) - the
 * private key never leaves the keystore, unlike Windows's DPAPI-protected exported
 * PKCS8 blob (Windows/ReyDesk.Windows/Identity/DeviceIdentityService.cs). Signatures
 * are DER-encoded (RFC 3279 SEQUENCE{r,s}), the default output of Java's
 * "SHA256withECDSA" - Windows verifies in that same format, see
 * docs/protocol.md "Pairing & trust model" for the exact signed-payload byte layout.
 */
class DeviceIdentityService private constructor(
    val deviceId: String,
    val displayName: String,
    /** X.509 SubjectPublicKeyInfo DER bytes - same encoding .NET's ExportSubjectPublicKeyInfo() produces. */
    val publicKey: ByteArray,
) {
    /** Signs data with this device's Keystore-held private key. */
    fun sign(data: ByteArray): ByteArray {
        val keyStore = KeyStore.getInstance(KeystoreProvider).apply { load(null) }
        val privateKey = keyStore.getKey(KeyAlias, null) as PrivateKey
        return Signature.getInstance("SHA256withECDSA").apply {
            initSign(privateKey)
            update(data)
        }.sign()
    }

    companion object {
        private const val KeystoreProvider = "AndroidKeyStore"
        private const val KeyAlias = "reydesk_device_identity"
        private val DeviceIdKey = stringPreferencesKey("device_id")

        suspend fun loadOrCreate(context: Context): DeviceIdentityService = withContext(Dispatchers.IO) {
            val keyStore = KeyStore.getInstance(KeystoreProvider).apply { load(null) }
            if (!keyStore.containsAlias(KeyAlias)) {
                val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, KeystoreProvider)
                generator.initialize(
                    KeyGenParameterSpec.Builder(KeyAlias, KeyProperties.PURPOSE_SIGN)
                        .setDigests(KeyProperties.DIGEST_SHA256)
                        .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                        .build(),
                )
                generator.generateKeyPair()
            }
            val publicKey = keyStore.getCertificate(KeyAlias).publicKey.encoded

            val prefs = context.identityDataStore.data.first()
            val existingId = prefs[DeviceIdKey]
            val deviceId = if (existingId != null) {
                existingId
            } else {
                val newId = UUID.randomUUID().toString()
                context.identityDataStore.edit { it[DeviceIdKey] = newId }
                newId
            }

            DeviceIdentityService(deviceId, Build.MODEL ?: "Android Device", publicKey)
        }
    }
}
