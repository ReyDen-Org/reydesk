package ir.reyden.reydesk.pairing

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringSetPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.google.protobuf.ByteString
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.protocol.v1.DeviceIdentity
import ir.reyden.reydesk.protocol.v1.PairingMethod
import ir.reyden.reydesk.protocol.v1.TrustedDevice
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import java.util.Base64
import java.security.MessageDigest

private val Context.trustedDevicesDataStore by preferencesDataStore(name = "reydesk_trusted_devices")

/**
 * Locally persisted list of devices this phone has paired with (spec section 8 trust model,
 * never uploaded anywhere in local mode). Stores each TrustedDevice as a Base64-encoded
 * protobuf blob in a string set, reusing the wire schema directly instead of a
 * hand-rolled DTO/JSON layer. Mirrors Windows/ReyDesk.Windows/Pairing/TrustedDeviceStore.cs,
 * adapted to DataStore.
 *
 * v2: records also carry the host's Desk ID, the per-host remote-allowed flag and the method it was
 * paired with. Identity is the exact public key: a record is found by device ID or, for hosts paired
 * without a known device ID (internet pairing), by key. Existing v1 records read as remote_allowed = false.
 */
class TrustedDeviceStore(private val context: Context) : HostTrust {
    private object Keys {
        val Devices = stringSetPreferencesKey("devices")
    }

    val devices: Flow<List<TrustedDevice>> = context.trustedDevicesDataStore.data.map { prefs -> decode(prefs[Keys.Devices]) }

    suspend fun isTrusted(deviceId: String): Boolean = devices.first().any { it.device.deviceId == deviceId }

    /** Finds a trusted host by exact public key (used to pick the LAN endpoint or Desk ID for a reconnect). */
    suspend fun findByKey(publicKey: ByteArray): TrustedDevice? =
        devices.first().firstOrNull { !it.revoked && MessageDigest.isEqual(it.publicKey.toByteArray(), publicKey) }

    override suspend fun verifyHostKey(deviceId: String, publicKey: ByteArray, firstUse: FirstUseProof?) {
        HostKeyRules.verify(lookup(devices.first(), deviceId, publicKey), publicKey, firstUse)
    }

    /** Compatibility for the pre-v2 UI call: true can only mean a scanned QR. */
    suspend fun verifyHostKey(deviceId: String, publicKey: ByteArray, allowFirstUse: Boolean) =
        verifyHostKey(deviceId, publicKey, if (allowFirstUse) FirstUseProof.QR else null)

    override suspend fun checkKeyUnchanged(deviceId: String, publicKey: ByteArray) {
        val existing = lookup(devices.first(), deviceId, publicKey) ?: return
        HostKeyRules.verify(existing, publicKey, null)
    }

    /** Forget only the selected identity; a new pairing is required later. */
    suspend fun remove(deviceId: String) {
        context.trustedDevicesDataStore.edit { prefs ->
            prefs[Keys.Devices] = decode(prefs[Keys.Devices])
                .filterNot { it.device.deviceId == deviceId }
                .map { it.toByteArray().toBase64() }.toSet()
        }
    }

    override suspend fun recordPaired(host: DeviceIdentity, publicKey: ByteArray, firstUse: FirstUseProof?,
        method: PairingMethod, remoteAllowed: Boolean?, deskId: String?) {
        val validDeskId = deskId?.let { (DeskIdCodec.parse(it) as? DeskIdCodec.Result.Ok)?.value }
        context.trustedDevicesDataStore.edit { prefs ->
            val current = decode(prefs[Keys.Devices])
            val now = System.currentTimeMillis()
            val existing = lookupDevice(current, host.deviceId, publicKey)
            HostKeyRules.verify(existing?.toStored(), publicKey, firstUse)
            val updated = if (existing != null) {
                val b = existing.toBuilder().setLastSeenUnixMs(now)
                // A synthetic desk:<ID> record learns the real device ID once a LAN pairing reveals it.
                if (existing.device.deviceId.startsWith(SyntheticPrefix) && host.deviceId.isNotEmpty() &&
                    !host.deviceId.startsWith(SyntheticPrefix)) {
                    b.setDevice(host)
                }
                if (validDeskId != null) b.setDeskId(validDeskId)
                if (remoteAllowed != null) b.setRemoteAllowed(remoteAllowed)
                if (method != PairingMethod.PAIRING_METHOD_UNSPECIFIED && firstUse != null) b.setPairedMethod(method)
                b.build()
            } else {
                TrustedDevice.newBuilder()
                    .setDevice(host)
                    .setPublicKey(ByteString.copyFrom(publicKey))
                    .setPairedAtUnixMs(now)
                    .setLastSeenUnixMs(now)
                    .setDeskId(validDeskId ?: "")
                    .setRemoteAllowed(remoteAllowed ?: false)
                    .setPairedMethod(method)
                    .build()
            }
            val remaining = current.filterNot { it.device.deviceId == existing?.device?.deviceId }
            prefs[Keys.Devices] = (remaining + updated).map { it.toByteArray().toBase64() }.toSet()
        }
    }

    /** Compatibility for the pre-v2 UI call; the pairing client already recorded the host, so this refreshes it. */
    suspend fun addOrUpdate(host: DeviceIdentity, hostPublicKey: ByteArray, allowFirstUse: Boolean = false) =
        recordPaired(host, hostPublicKey, if (allowFirstUse) FirstUseProof.QR else null,
            if (allowFirstUse) PairingMethod.PAIRING_METHOD_LAN_QR else PairingMethod.PAIRING_METHOD_UNSPECIFIED, null, null)

    /** Sets the per-host internet flag only on the record whose key is exactly [expectedKey]. */
    suspend fun setRemoteAllowed(deviceId: String, expectedKey: ByteArray, allowed: Boolean) {
        mutateExact(deviceId, expectedKey) { it.setRemoteAllowed(allowed) }
    }

    /** HostInfo / PairingResponse.host_desk_id: updates only a record with exactly [expectedKey]; invalid IDs are ignored. */
    suspend fun updateDeskId(deviceId: String, expectedKey: ByteArray, deskId: String) {
        val valid = (DeskIdCodec.parse(deskId) as? DeskIdCodec.Result.Ok)?.value ?: return
        mutateExact(deviceId, expectedKey) { it.setDeskId(valid) }
    }

    private suspend fun mutateExact(deviceId: String, expectedKey: ByteArray, change: (TrustedDevice.Builder) -> Unit) {
        context.trustedDevicesDataStore.edit { prefs ->
            val current = decode(prefs[Keys.Devices])
            val existing = current.find { it.device.deviceId == deviceId } ?: return@edit
            if (existing.revoked || !MessageDigest.isEqual(existing.publicKey.toByteArray(), expectedKey)) return@edit
            val updated = existing.toBuilder().also(change).build()
            prefs[Keys.Devices] = (current.filterNot { it.device.deviceId == deviceId } + updated).map { it.toByteArray().toBase64() }.toSet()
        }
    }

    /** By device ID first (a different key then fails the exact-key rule), else by exact key. */
    private fun lookupDevice(list: List<TrustedDevice>, deviceId: String, key: ByteArray): TrustedDevice? =
        list.find { it.device.deviceId == deviceId }
            ?: list.firstOrNull { MessageDigest.isEqual(it.publicKey.toByteArray(), key) }

    private fun lookup(list: List<TrustedDevice>, deviceId: String, key: ByteArray): StoredHostKey? =
        lookupDevice(list, deviceId, key)?.toStored()

    private fun TrustedDevice.toStored() = StoredHostKey(publicKey.toByteArray(), revoked)

    private fun decode(encoded: Set<String>?): List<TrustedDevice> =
        (encoded ?: emptySet()).mapNotNull { runCatching { TrustedDevice.parseFrom(Base64.getDecoder().decode(it)) }.getOrNull() }

    private fun ByteArray.toBase64(): String = Base64.getEncoder().encodeToString(this)

    companion object {
        /** Device ID given to a host paired over the internet before its real device ID is known. */
        const val SyntheticPrefix = "desk:"
        fun syntheticDeviceId(deskId: String) = SyntheticPrefix + deskId
    }
}
