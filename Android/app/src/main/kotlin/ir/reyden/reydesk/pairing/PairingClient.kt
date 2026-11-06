package ir.reyden.reydesk.pairing

import android.util.Log
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.identity.DeviceIdentityService
import ir.reyden.reydesk.protocol.rendezvous.v1.PipePurpose
import ir.reyden.reydesk.protocol.v1.ConnectionMedium
import ir.reyden.reydesk.protocol.v1.DeviceIdentity
import ir.reyden.reydesk.protocol.v1.PairingMethod
import ir.reyden.reydesk.protocol.v1.PairingOffer
import ir.reyden.reydesk.protocol.v1.PairingResponse
import ir.reyden.reydesk.protocol.v1.PairingResult
import ir.reyden.reydesk.protocol.v1.Platform
import ir.reyden.reydesk.rendezvous.PinnedHostKeyTrustManager
import ir.reyden.reydesk.rendezvous.RelayPipe
import ir.reyden.reydesk.rendezvous.RendezvousClient
import ir.reyden.reydesk.rendezvous.TlsOverPipe
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.NoRouteToHostException
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.security.SecureRandom
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager

fun DeviceIdentityService.asPairingIdentity(): PairingIdentity = object : PairingIdentity {
    override val deviceId: String get() = this@asPairingIdentity.deviceId
    override val displayName: String get() = this@asPairingIdentity.displayName
    override val publicKey: ByteArray get() = this@asPairingIdentity.publicKey
    override fun sign(data: ByteArray): ByteArray = this@asPairingIdentity.sign(data)
}

/** The authenticated stream left over after pairing; [SignalingConnection] runs on it. */
class PairedStream internal constructor(val input: java.io.InputStream, val output: java.io.OutputStream,
    private val closer: () -> Unit, internal val socket: Socket?) {
    fun close() = closer()
}

/**
 * Result of a pairing or reconnect. [response] is the host's terminal reply (check [accepted]); when it is not
 * accepted the stream is already closed. [medium] tells the session which ICE policy to use.
 */
class PairingOutcome internal constructor(val response: PairingResponse, val stream: PairedStream,
    val medium: ConnectionMedium, val method: PairingMethod, val hostDeskId: String?) {
    val accepted: Boolean get() = response.result == PairingResult.PAIRING_RESULT_ACCEPTED ||
        response.result == PairingResult.PAIRING_RESULT_ALREADY_TRUSTED

    /**
     * Signaling over this stream: the pinned-TLS LAN socket or the pinned-TLS relay pipe, same code path.
     */
    val connection: SignalingConnection by lazy { openSignaling(stream, medium) }
}

internal fun openSignaling(stream: PairedStream, medium: ConnectionMedium): SignalingConnection =
    SignalingConnection(stream, medium)

/** A trusted PC to reconnect to. [publicKey] is the exact saved key; [deskId] is its internet routing handle if known. */
class TrustedHost(val deviceId: String, val displayName: String, val publicKey: ByteArray, val deskId: String?)

/** A LAN endpoint found by mDNS whose key equals the trusted key (see [LanHostMetadata.hasKey]). */
class LanEndpoint(val address: String, val port: Int)

enum class ReconnectMode {
    /** M5: the PC shows an Accept prompt. */
    ATTENDED,
    /** M6: per-device opt-in, mutual signatures, no prompt. */
    UNATTENDED,
}

/** The remote path was needed but is not available (no rendezvous client or no Desk ID). Not a security failure. */
class RemoteUnavailableException(message: String) : IOException(message)

/** QR scan: the LAN hint did not answer and no internet fallback was possible (phone or PC internet access off). */
class LanOnlyUnreachableException(cause: Throwable) : IOException("Cannot reach the PC on this network.", cause)

private const val LogTag = "ReyDeskPairing"

/**
 * Facade the UI calls. No PIN path exists any more. Each method returns a [PairingOutcome] whose stream carries
 * SDP/ICE signaling afterwards. Trust is recorded here, only after the PC accepted.
 */
object PairingClient {
    private const val LanConnectMs = 5000
    private const val LanConnectWithFallbackMs = 3000
    private const val HandshakeReadMs = 10_000

    // ---- M1 + M3: QR ---------------------------------------------------------------------------------------------

    /** Scanned QR. Tries the LAN endpoint first (M1); only if TCP cannot connect within 3 s and a Desk ID,
     * [rendezvous] and a valid Desk ID exist does it open a pipe (M3). A TLS pin failure never falls back. */
    suspend fun pairWithQr(offer: PairingOffer, identity: DeviceIdentityService, trust: TrustedDeviceStore,
        rendezvous: RendezvousClient? = null): PairingOutcome {
        val hostKey = offer.hostPublicKey.toByteArray()
        require(hostKey.size in 32..256 && offer.host.deviceId.length in 1..128 &&
            offer.localPortHint in 1..65535 && offer.localIpHint.isNotBlank()) { "Invalid pairing offer." }
        check(offer.expiresAtUnixMs > System.currentTimeMillis()) { "Pairing offer expired. Regenerate it on the PC." }
        require(PairingSession.isQrToken(offer.pairingToken)) { "This QR code is from an older ReyDesk. Update the PC app." }
        val deskId = (DeskIdCodec.parse(offer.host.deskId) as? DeskIdCodec.Result.Ok)?.value
        val canFallback = rendezvous != null && deskId != null
        Log.i(LogTag, "QR scan: LAN first, internet fallback " + when {
            canFallback -> "available"
            rendezvous == null -> "off (internet access is off on this phone)"
            else -> "off (the QR has no Desk ID: internet is off on the PC)"
        })
        val lan = try {
            openLan(offer.localIpHint, offer.localPortHint, hostKey, if (canFallback) LanConnectWithFallbackMs else LanConnectMs)
        } catch (e: LanUnreachableException) {
            Log.i(LogTag, "QR scan: LAN unreachable (${e.cause?.javaClass?.simpleName})")
            if (!canFallback) throw LanOnlyUnreachableException(e)
            null
        }
        val method = if (lan != null) PairingMethod.PAIRING_METHOD_LAN_QR else PairingMethod.PAIRING_METHOD_REMOTE_QR
        Log.i(LogTag, "QR scan: pairing with $method")
        val opened = lan ?: openPipeLink(rendezvous!!, deskId!!, PipePurpose.PIPE_PURPOSE_PAIR_QR, "", hostKey)
        val params = PairParams(method, hostKey, offer.host, if (lan != null) "" else deskId!!,
            offer.pairingToken.toByteArray(Charsets.UTF_8), identity.asPairingIdentity(), trust,
            FirstUseProof.QR, if (lan != null) null else true, deskId)
        return outcome(PairingSession.pairWithQr(opened.link, params), opened.link, opened.socket, opened.medium, method)
    }

    // ---- M2: nearby with SAS --------------------------------------------------------------------------------------

    suspend fun pairNearby(host: LanHostMetadata, identity: DeviceIdentityService, trust: TrustedDeviceStore,
        confirmSas: SasConfirmer): PairingOutcome {
        val lan = openLan(host.address, host.port, host.publicKey, LanConnectMs)
        val hostIdentity = DeviceIdentity.newBuilder().setDeviceId(host.id).setDisplayName(host.name)
            .setPlatform(Platform.PLATFORM_WINDOWS).apply { host.deskId?.let { setDeskId(it) } }.build()
        val params = PairParams(PairingMethod.PAIRING_METHOD_LAN_SAS, host.publicKey, hostIdentity, "", ByteArray(0),
            identity.asPairingIdentity(), trust, FirstUseProof.SAS_CONFIRMED, null, host.deskId)
        return outcome(PairingSession.pairWithSas(lan.link, params, confirmSas), lan.link, lan.socket, lan.medium,
            PairingMethod.PAIRING_METHOD_LAN_SAS)
    }

    // ---- M4: internet by Desk ID + code --------------------------------------------------------------------------

    /** [code] is wiped before this returns (best effort: the rendezvous API takes the code as a String). */
    suspend fun pairByDeskId(deskId: String, code: CharArray, identity: DeviceIdentityService, trust: TrustedDeviceStore,
        rendezvous: RendezvousClient, confirmSas: SasConfirmer): PairingOutcome {
        val canonical = (DeskIdCodec.parse(deskId) as? DeskIdCodec.Result.Ok)?.value
        val normalized = RemoteCode.normalize(String(code))
        code.fill('\u0000')
        require(canonical != null) { "Invalid Desk ID." }
        require(normalized != null) { "The code must be six digits." }
        val codeBytes = normalized.toByteArray(Charsets.UTF_8)
        Log.i(LogTag, "Internet code pairing: opening pipe")
        val link = try {
            // The key the server reports is untrusted: it is pinned only so the SAS can authenticate it.
            openPipeLink(rendezvous, canonical, PipePurpose.PIPE_PURPOSE_PAIR_CODE, normalized, null)
        } catch (e: Exception) { codeBytes.fill(0); throw e }
        val hostKey = link.peerKey
        val hostIdentity = DeviceIdentity.newBuilder().setDeviceId(TrustedDeviceStore.syntheticDeviceId(canonical))
            .setDisplayName("PC " + DeskIdCodec.display(canonical)).setPlatform(Platform.PLATFORM_WINDOWS).setDeskId(canonical).build()
        val params = PairParams(PairingMethod.PAIRING_METHOD_REMOTE_CODE, hostKey, hostIdentity, canonical, codeBytes,
            identity.asPairingIdentity(), trust, FirstUseProof.SAS_CONFIRMED, true, canonical)
        return outcome(PairingSession.pairWithSas(link.link, params, confirmSas), link.link, link.socket, link.medium,
            PairingMethod.PAIRING_METHOD_REMOTE_CODE)
    }

    // ---- M5 / M6: trusted reconnect --------------------------------------------------------------------------------

    /** Call only from an explicit foreground user action (never as silent background reconnect).
     * LAN when [lan] is given (the caller matched the key via mDNS), else a pipe to [TrustedHost.deskId]. */
    suspend fun reconnectTrusted(host: TrustedHost, mode: ReconnectMode, identity: DeviceIdentityService,
        trust: TrustedDeviceStore, lan: LanEndpoint?, rendezvous: RendezvousClient?,
        userConfirmedInForeground: Boolean): PairingOutcome {
        check(userConfirmedInForeground) { "Confirm reconnect in the foreground first." }
        require(host.publicKey.size in 32..256) { "Invalid saved host key." }
        trust.verifyHostKey(host.deviceId, host.publicKey, firstUse = null)
        val deskId = host.deskId?.let { (DeskIdCodec.parse(it) as? DeskIdCodec.Result.Ok)?.value }
        // Over a pipe the phone signs its own Desk ID (M5, spec 2.8): read once from the client that opens the pipe, before opening.
        val ownDeskId = if (lan == null && rendezvous != null && deskId != null && mode == ReconnectMode.ATTENDED) {
            (DeskIdCodec.parse(rendezvous.deskId) as? DeskIdCodec.Result.Ok)?.value
                ?: throw RemoteUnavailableException("Internet access is not set up on this phone.")
        } else ""
        val opened: OpenLink = when {
            lan != null -> openLan(lan.address, lan.port, host.publicKey, LanConnectMs)
            deskId != null && rendezvous != null ->
                openPipeLink(rendezvous, deskId, PipePurpose.PIPE_PURPOSE_RECONNECT, "", host.publicKey)
            else -> throw RemoteUnavailableException("No LAN path and no internet address for this PC.")
        }
        val method = if (mode == ReconnectMode.ATTENDED) PairingMethod.PAIRING_METHOD_TRUSTED_ATTENDED
        else PairingMethod.PAIRING_METHOD_TRUSTED_UNATTENDED
        val overPipe = opened.medium == ConnectionMedium.CONNECTION_MEDIUM_REMOTE
        val hostIdentity = DeviceIdentity.newBuilder().setDeviceId(host.deviceId).setDisplayName(host.displayName)
            .setPlatform(Platform.PLATFORM_WINDOWS).build()
        // A host that accepted over a pipe has proven the remote flag is on for this phone.
        val params = PairParams(method, host.publicKey, hostIdentity, if (overPipe) deskId!! else "", ByteArray(0),
            identity.asPairingIdentity(), trust, null, if (overPipe) true else null, deskId,
            requesterDeskIdForSignature = if (overPipe) ownDeskId else "")
        val result = if (mode == ReconnectMode.ATTENDED) PairingSession.reconnectAttended(opened.link, params)
        else PairingSession.reconnectUnattended(opened.link, params)
        return outcome(result, opened.link, opened.socket, opened.medium, method)
    }

    // ---- compatibility for the pre-v2 UI (T16 replaces these call sites) -------------------------------------------

    /** First pairing from a QR (allowFirstUse = true) or, for an already trusted PC, an attended reconnect. There is no PIN path. */
    suspend fun pair(offer: PairingOffer, identity: DeviceIdentityService, trustedDevices: TrustedDeviceStore,
        allowFirstUse: Boolean = false): PairingOutcome {
        if (allowFirstUse) return pairWithQr(offer, identity, trustedDevices)
        val key = offer.hostPublicKey.toByteArray()
        return reconnectTrusted(TrustedHost(offer.host.deviceId, offer.host.displayName, key, null), ReconnectMode.ATTENDED,
            identity, trustedDevices, LanEndpoint(offer.localIpHint, offer.localPortHint), null, true)
    }

    /** Unattended reconnect over the offer's LAN endpoint (M6), as before. */
    suspend fun reconnect(offer: PairingOffer, identity: DeviceIdentityService, trustedDevices: TrustedDeviceStore,
        userConfirmedInForeground: Boolean): PairingOutcome =
        reconnectTrusted(TrustedHost(offer.host.deviceId, offer.host.displayName, offer.hostPublicKey.toByteArray(), null),
            ReconnectMode.UNATTENDED, identity, trustedDevices, LanEndpoint(offer.localIpHint, offer.localPortHint), null,
            userConfirmedInForeground)

    /** Applies a HostInfo envelope or PairingResponse.host_desk_id: only a record with exactly [hostKey] is touched. */
    suspend fun applyHostInfo(trust: TrustedDeviceStore, hostDeviceId: String, hostKey: ByteArray, deskId: String) =
        trust.updateDeskId(hostDeviceId, hostKey, deskId)

    // ---- transport -----------------------------------------------------------------------------------------------------

    private class LanUnreachableException(cause: Throwable) : IOException("LAN host unreachable", cause)

    private class OpenLink(val link: PairLink, val closer: () -> Unit, val socket: Socket?, val medium: ConnectionMedium,
        val peerKey: ByteArray = ByteArray(0))

    /** TCP connect (a failure here is [LanUnreachableException]) then pinned TLS. Any TLS/pin error propagates unchanged. */
    private suspend fun openLan(address: String, port: Int, hostKey: ByteArray, connectMs: Int): OpenLink {
        val sslContext = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(PinnedHostKeyTrustManager(hostKey)), SecureRandom())
        }
        val socket = sslContext.socketFactory.createSocket() as SSLSocket
        try {
            withContext(Dispatchers.IO) {
                socket.enabledProtocols = socket.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
                try {
                    socket.connect(InetSocketAddress(address, port), connectMs)
                } catch (e: SocketTimeoutException) { throw LanUnreachableException(e) }
                catch (e: ConnectException) { throw LanUnreachableException(e) }
                catch (e: NoRouteToHostException) { throw LanUnreachableException(e) }
                catch (e: UnknownHostException) { throw LanUnreachableException(e) }
                // Other connect-time socket errors (for example "Network is unreachable" on mobile data) also mean "no LAN path".
                catch (e: java.net.SocketException) { throw LanUnreachableException(e) }
                socket.soTimeout = HandshakeReadMs
                socket.tcpNoDelay = true
                socket.startHandshake()
                socket.soTimeout = 0
            }
        } catch (e: Exception) {
            runCatching { socket.close() }
            throw e
        }
        val close = { runCatching { socket.close() }; Unit }
        return OpenLink(PairLink(socket.getInputStream(), socket.getOutputStream(), close), close, socket, ConnectionMedium.CONNECTION_MEDIUM_LOCAL)
    }

    /** Opens a relay pipe, then pinned TLS over it. [expectedKey] null means "pin what the server reports" (M4, SAS authenticates it). */
    private suspend fun openPipeLink(rendezvous: RendezvousClient, deskId: String, purpose: PipePurpose, code: String,
        expectedKey: ByteArray?): OpenLink {
        val pipe: RelayPipe = rendezvous.openPipe(deskId, purpose, code)
        try {
            val reported = pipe.peerSpki
            if (expectedKey != null && !PairingSession.keysEqual(expectedKey, reported))
                throw SecurityException("The server reported a different PC key than the one saved or scanned.")
            val pinned = expectedKey ?: reported
            require(pinned.size in 32..256) { "Invalid PC key from the server." }
            val tls = withContext(Dispatchers.IO) { TlsOverPipe.connect(pipe, pinned) }
            val close = { runCatching { tls.close() }; Unit }
            return OpenLink(PairLink(tls.input, tls.output, close), close, null, ConnectionMedium.CONNECTION_MEDIUM_REMOTE, pinned)
        } catch (e: Exception) {
            runCatching { pipe.close() }
            throw e
        }
    }

    private fun outcome(r: PairingFlowResult, link: PairLink, socket: Socket?, medium: ConnectionMedium, method: PairingMethod): PairingOutcome {
        val stream = PairedStream(link.input, link.output, link.close, socket)
        val result = PairingOutcome(r.response, stream, medium, method, r.hostDeskId)
        // The host reason is one of the PC's fixed strings (no secrets).
        Log.i(LogTag, "$method over $medium -> ${r.response.result}" + r.response.reason.let { if (it.isEmpty()) "" else " ($it)" })
        if (!result.accepted) stream.close()
        return result
    }
}
