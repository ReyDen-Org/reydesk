package ir.reyden.reydesk.remote

import android.content.Context
import ir.reyden.reydesk.BuildConfig
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.identity.DeviceIdentityService
import ir.reyden.reydesk.pairing.LanEndpoint
import ir.reyden.reydesk.pairing.LanHostMetadata
import ir.reyden.reydesk.pairing.PairingClient
import ir.reyden.reydesk.pairing.PairingOutcome
import ir.reyden.reydesk.pairing.ReconnectMode
import ir.reyden.reydesk.pairing.RemoteUnavailableException
import ir.reyden.reydesk.pairing.SasConfirmer
import ir.reyden.reydesk.pairing.TrustedDeviceStore
import ir.reyden.reydesk.pairing.TrustedHost
import ir.reyden.reydesk.protocol.v1.ConnectionMedium
import ir.reyden.reydesk.protocol.v1.PairingOffer
import ir.reyden.reydesk.protocol.v1.PathKind
import ir.reyden.reydesk.protocol.v1.TrustedDevice
import ir.reyden.reydesk.rendezvous.IceServerProvider
import ir.reyden.reydesk.rendezvous.RendezvousUrl
import ir.reyden.reydesk.rendezvous.RendezvousClient
import ir.reyden.reydesk.rendezvous.RendezvousState
import ir.reyden.reydesk.rendezvous.asRendezvousIdentity
import ir.reyden.reydesk.settings.RemoteSettingsRepository
import ir.reyden.reydesk.ui.components.RdPath
import ir.reyden.reydesk.ui.connection.RemoteServerState
import ir.reyden.reydesk.ui.connection.RemoteUiState
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.webrtc.PeerConnection

/** A connected host: pass `outcome.connection` and these fields to `SessionCoordinator.startSession` once `outcome.accepted`. */
class ConnectedHost(val outcome: PairingOutcome, val deviceId: String, val displayName: String, val publicKey: ByteArray)

/**
 * App-level facade for internet access: rendezvous lifecycle, persisted switches and Desk ID, ICE servers per
 * medium, the SAS prompt hand-off, and the observable [RemoteUiState]. Owned by SessionCoordinator (`remote`).
 * LAN behaviour does not depend on anything here: nothing starts until [start] finds remote allowed and a URL.
 */
class RemoteController(
    appContext: Context,
    private val scope: CoroutineScope,
    private val identityProvider: suspend () -> DeviceIdentityService,
    private val trust: TrustedDeviceStore,
) {
    private val repo = RemoteSettingsRepository(appContext)
    private val _ui = MutableStateFlow(RemoteUiState(null, null, RemoteServerState.Off, null, remoteAllowed = false))

    /** Observe this for the Connection, Devices and Settings screens. */
    val uiState: StateFlow<RemoteUiState> = _ui.asStateFlow()

    @Volatile var client: RendezvousClient? = null
        private set
    private var stateJob: Job? = null
    private var pendingSas: CompletableDeferred<Boolean>? = null
    @Volatile private var relayAllowed = false
    @Volatile private var url = ""

    /** Loads persisted settings and Key ID, then starts the rendezvous client if internet access is on. Call once at app start. */
    suspend fun start() {
        val s = repo.load()
        relayAllowed = s.relayAllowed
        url = s.effectiveRendezvousUrl
        val identity = identityProvider()
        val keyId = DeskIdCodec.keyIdDisplay(DeskIdCodec.fingerprint(identity.publicKey))
        _ui.update { it.copy(keyId = keyId, deskId = s.deskId.ifEmpty { null }, remoteAllowed = s.remoteAllowed, relayAllowed = s.relayAllowed) }
        if (s.remoteAllowed) startClient(s.deskId)
    }

    /** Internet access switch. Turning it off stops the client; the Desk ID stays saved. */
    fun setRemoteAllowed(allowed: Boolean) {
        scope.launch {
            repo.setRemoteAllowed(allowed)
            _ui.update { it.copy(remoteAllowed = allowed) }
            if (allowed) startClient(repo.load().deskId) else stopClient()
        }
    }

    val isRelayAllowed: Boolean get() = relayAllowed

    /** "Allow relay through the ReyDesk server" switch (default off). Applies to the next connection. */
    fun setRelayAllowed(allowed: Boolean) {
        relayAllowed = allowed
        _ui.update { it.copy(relayAllowed = allowed) }
        scope.launch { repo.setRelayAllowed(allowed) }
    }

    /** Advanced setting; restarts the client when internet access is on. Must be wss:// (otherwise treated as not set up). */
    fun setRendezvousUrl(newUrl: String) {
        // One canonical spelling (the client signs it); anything that is not acceptable is ignored.
        val canonical = if (newUrl.isBlank()) "" else RendezvousUrl.normalize(newUrl, BuildConfig.DEBUG) ?: return
        scope.launch {
            repo.setRendezvousUrl(canonical)
            url = canonical.ifEmpty { ir.reyden.reydesk.settings.DEFAULT_RENDEZVOUS_URL }
            if (_ui.value.remoteAllowed) startClient(repo.load().deskId)
        }
    }

    private suspend fun startClient(savedDeskId: String) {
        stopClient()
        if (url.isEmpty()) return // service not configured: state stays Off
        val identity = identityProvider()
        val initial = if (savedDeskId.isEmpty() || DeskIdCodec.check(savedDeskId)) savedDeskId else ""
        val c = try {
            RendezvousClient(url, identity.asRendezvousIdentity(), initial,
                onDeskIdChanged = { id ->
                    _ui.update { it.copy(deskId = id.ifEmpty { null }) }
                    scope.launch { repo.setDeskId(id) }
                }, scope, BuildConfig.VERSION_NAME, allowCleartextForDev = BuildConfig.DEBUG, relayAllowed = { relayAllowed })
        } catch (_: IllegalArgumentException) {
            return // non-wss URL: treated as not configured
        }
        client = c
        stateJob = scope.launch {
            c.state.collect { st ->
                _ui.update {
                    it.copy(serverState = when (st) {
                        RendezvousState.Stopped -> RemoteServerState.Off
                        RendezvousState.Connecting -> RemoteServerState.Connecting
                        is RendezvousState.Online -> RemoteServerState.Online
                        is RendezvousState.Backoff, is RendezvousState.Failed -> RemoteServerState.Offline
                    })
                }
            }
        }
        c.start()
    }

    private fun stopClient() {
        stateJob?.cancel(); stateJob = null
        client?.stop(); client = null
        _ui.update { it.copy(serverState = RemoteServerState.Off) }
    }

    /** ICE servers for a session: LAN empty; remote STUN from the server config, TURN only if relay is allowed. */
    suspend fun iceServers(medium: ConnectionMedium): List<PeerConnection.IceServer> =
        if (medium == ConnectionMedium.CONNECTION_MEDIUM_REMOTE) IceServerProvider.forRemote(client?.iceConfigForSession(), relayAllowed)
        else IceServerProvider.forLan()

    /** Called by SessionCoordinator with the measured path (null when no session). */
    internal fun reportPath(kind: PathKind?) {
        val p = when (kind) {
            PathKind.PATH_LAN_DIRECT -> RdPath.Lan
            PathKind.PATH_REMOTE_DIRECT -> RdPath.Direct
            PathKind.PATH_RELAYED -> RdPath.Relay
            else -> null
        }
        _ui.update { it.copy(path = p) }
    }

    // ---- SAS hand-off: pass [sasConfirmer] to pairing; the UI shows uiState.sas and calls [answerSas] ----

    val sasConfirmer = SasConfirmer { prompt ->
        val d = CompletableDeferred<Boolean>()
        pendingSas?.complete(false)
        pendingSas = d
        _ui.update { it.copy(sas = prompt.digits) }
        android.util.Log.i("ReyDeskPairing", "Code comparison shown (the PC shows the same code now)")
        try { d.await().also { android.util.Log.i("ReyDeskPairing", "Code comparison answered: " + if (it) "same" else "different") } } finally {
            _ui.update { it.copy(sas = null) }
            if (pendingSas === d) pendingSas = null
        }
    }

    /** UI callback `onSasResult(same)`. */
    fun answerSas(same: Boolean) { pendingSas?.complete(same) }

    // ---- connect entry points; check outcome.accepted before starting a session ----

    /** UI callback `onInternetConnect(deskId, code)` (M4). Throws [RemoteUnavailableException] if internet access is off. */
    suspend fun connectByDeskId(deskId: String, code: CharArray): ConnectedHost {
        val c = client
        if (c == null) {
            code.fill('\u0000')
            throw RemoteUnavailableException("Turn on internet access first.")
        }
        val canonical = (DeskIdCodec.parse(deskId) as? DeskIdCodec.Result.Ok)?.value
        val outcome = PairingClient.pairByDeskId(deskId, code, identityProvider(), trust, c, sasConfirmer)
        val saved = canonical?.let { id -> trust.devices.first().firstOrNull { !it.revoked && it.deskId == id } }
        return ConnectedHost(outcome, saved?.device?.deviceId.orEmpty(), saved?.device?.displayName.orEmpty(),
            saved?.publicKey?.toByteArray() ?: ByteArray(0))
    }

    /** Scanned QR (M1, falling back to M3). Passes the rendezvous client so an unreachable LAN hint (3 s) falls back to a relay
     * pipe to the QR's Desk ID. Before 2026-11 the scan screen called PairingClient.pair without a client, so M3 never ran. */
    suspend fun connectByQr(offer: PairingOffer): ConnectedHost =
        ConnectedHost(PairingClient.pairWithQr(offer, identityProvider(), trust, client), offer.host.deviceId,
            offer.host.displayName, offer.hostPublicKey.toByteArray())

    /** UI callback `onNearbyConnect(host)` (M2, LAN + SAS). */
    suspend fun connectNearby(host: LanHostMetadata): ConnectedHost =
        ConnectedHost(PairingClient.pairNearby(host, identityProvider(), trust, sasConfirmer), host.id, host.name, host.publicKey)

    /** UI callback `onReconnect(device)` (M5/M6). [lan] is the mDNS match, if any; otherwise the Desk ID pipe is used. */
    suspend fun reconnect(device: TrustedDevice, lan: LanEndpoint?, unattended: Boolean): ConnectedHost {
        val key = device.publicKey.toByteArray()
        val deskId = device.deskId.ifEmpty { device.device.deskId }.ifEmpty { null }
        val host = TrustedHost(device.device.deviceId, device.device.displayName, key, deskId)
        val outcome = PairingClient.reconnectTrusted(host, if (unattended) ReconnectMode.UNATTENDED else ReconnectMode.ATTENDED,
            identityProvider(), trust, lan, client, userConfirmedInForeground = true)
        return ConnectedHost(outcome, host.deviceId, host.displayName, key)
    }
}
