package ir.reyden.reydesk

import android.content.Context
import android.util.Log
import ir.reyden.reydesk.identity.DeviceIdentityService
import ir.reyden.reydesk.pairing.SignalingConnection
import ir.reyden.reydesk.remote.RemoteController
import ir.reyden.reydesk.protocol.v1.PathKind
import ir.reyden.reydesk.pairing.TrustedDeviceStore
import ir.reyden.reydesk.protocol.v1.ConnectionMedium
import ir.reyden.reydesk.protocol.v1.ConnectionState
import ir.reyden.reydesk.protocol.v1.ConnectionStatus
import ir.reyden.reydesk.protocol.v1.Envelope
import ir.reyden.reydesk.webrtc.WebRtcSession
import ir.reyden.reydesk.webrtc.ConnectionMetrics
import ir.reyden.reydesk.settings.AppSettingsRepository
import ir.reyden.reydesk.protocol.v1.NegotiatedConfig
import ir.reyden.reydesk.protocol.v1.CompanionRequest
import ir.reyden.reydesk.protocol.v1.CompanionResult
import ir.reyden.reydesk.companion.PhoneHubBridge
import java.util.UUID
import android.net.Uri
import ir.reyden.reydesk.companion.AndroidFileTransfer
import ir.reyden.reydesk.companion.FileTransferUi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import org.webrtc.DefaultVideoDecoderFactory
import org.webrtc.DefaultVideoEncoderFactory
import org.webrtc.EglBase
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.VideoTrack
import java.time.Instant

private const val Tag = "SessionCoordinator"

/**
 * Owns the app's long-lived, stateful services (identity, pairing, WebRTC signaling) -
 * a simple composition root rather than a DI framework, mirroring
 * Windows/ReyDesk.Windows/SessionCoordinator.cs. Held by ReyDeskApplication and
 * reached from Composables via
 * `LocalContext.current.applicationContext as ReyDeskApplication`.
 */
class SessionCoordinator(private val appContext: Context) {
    val trustedDevices = TrustedDeviceStore(appContext)
    val phoneHub = PhoneHubBridge(appContext)
    private val _controlReady = MutableStateFlow(false)
    val controlReady = _controlReady.asStateFlow()
    private val _companionResult = MutableStateFlow<CompanionResult?>(null)
    val companionResult = _companionResult.asStateFlow()
    private val pendingRequests = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
    private var activeTransfer: AndroidFileTransfer? = null
    private val _transferUi = MutableStateFlow(FileTransferUi())
    val transferUi = _transferUi.asStateFlow()
    fun sendFile(uri: Uri, expectedSessionId: String) {
        if (expectedSessionId.isNotEmpty() && activeSession?.sessionIdentity() == expectedSessionId) activeTransfer?.sendFile(uri)
    }
    fun cancelFileTransfer() { activeTransfer?.cancel() }
    fun chooseFileDestination(transferId: String, uri: Uri?, expectedSessionId: String) {
        val transfer = activeTransfer.takeIf { expectedSessionId.isNotEmpty() && activeSession?.sessionIdentity() == expectedSessionId }
        if (transfer != null) transfer.chooseDestination(transferId, uri)
        else if (uri != null) scope.launch(Dispatchers.IO) {
            val deleted = runCatching { android.provider.DocumentsContract.deleteDocument(appContext.contentResolver, uri) }.getOrDefault(false)
            if (!deleted && activeTransfer == null) _transferUi.value = FileTransferUi("cleanup_failed", detail = "Remove the empty file created by your canceled save picker.")
        }
    }

    private var identity: DeviceIdentityService? = null

    // Lives for the app's process, not any one screen's composition - a signaling
    // connection/session must survive navigating away from the Connection tab.
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    /** Internet access: rendezvous lifecycle, ICE config, observable RemoteUiState. Starts only if the user turned it on. */
    val remote = RemoteController(appContext, scope, ::getOrCreateIdentity, trustedDevices)

    init {
        scope.launch { runCatching { remote.start() }.onFailure { Log.w(Tag, "remote start failed: ${it.message}") } }
    }

    // Owned here (not per-session) since it backs every SurfaceViewRenderer the UI
    // creates across the app's lifetime, not just one session.
    val eglBase: EglBase by lazy { EglBase.create() }

    private val peerConnectionFactory: PeerConnectionFactory by lazy {
        PeerConnectionFactory.initialize(
            PeerConnectionFactory.InitializationOptions.builder(appContext)
                .setEnableInternalTracer(false)
                .createInitializationOptions(),
        )
        // Decoder factory is real work (Android only ever receives Windows's H.264
        // video, never sends its own - see WebRtcSession.kt); the encoder factory is
        // registered for API completeness only and never exercised.
        PeerConnectionFactory.builder()
            .setVideoDecoderFactory(DefaultVideoDecoderFactory(eglBase.eglBaseContext))
            .setVideoEncoderFactory(DefaultVideoEncoderFactory(eglBase.eglBaseContext, true, true))
            .createPeerConnectionFactory()
    }

    private val _remoteVideoTrack = MutableStateFlow<VideoTrack?>(null)
    val remoteVideoTrack: StateFlow<VideoTrack?> = _remoteVideoTrack.asStateFlow()

    private val _connectionState = MutableStateFlow<PeerConnection.PeerConnectionState?>(null)
    val connectionState: StateFlow<PeerConnection.PeerConnectionState?> = _connectionState.asStateFlow()
    /** True when an internet session has not connected (failed, or still waiting after 15 s) while relay is off: the UI suggests turning relay on. */
    private val _relayHint = MutableStateFlow(false)
    val relayHint: StateFlow<Boolean> = _relayHint.asStateFlow()
    private val _streamStats = MutableStateFlow<ConnectionMetrics?>(null)
    val streamStats: StateFlow<ConnectionMetrics?> = _streamStats.asStateFlow()
    private val _connectedHostName = MutableStateFlow<String?>(null)
    val connectedHostName: StateFlow<String?> = _connectedHostName.asStateFlow()
    private val _sessionStartedAtMs = MutableStateFlow<Long?>(null)
    val sessionStartedAtMs: StateFlow<Long?> = _sessionStartedAtMs.asStateFlow()
    private val _negotiatedConfig = MutableStateFlow<NegotiatedConfig?>(null)
    val negotiatedConfig: StateFlow<NegotiatedConfig?> = _negotiatedConfig.asStateFlow()

    private var activeSession: WebRtcSession? = null
    private var activeConnection: SignalingConnection? = null
    private var activeHostId: String? = null

    /** Loads (or generates, on first run) this device's identity. Cheap to call
     * repeatedly - only does real work once per process. */
    suspend fun getOrCreateIdentity(): DeviceIdentityService =
        identity ?: DeviceIdentityService.loadOrCreate(appContext).also { identity = it }

    /** The session started by [startSession], if any is currently active - the
     * Session screen sends input through this. */
    fun activeSessionOrNull(): WebRtcSession? = activeSession

    /** Ends the active session (if any) by closing its signaling connection - the
     * same teardown that happens if Windows disconnects, see [startSession]'s
     * `connection.onClosed`. Closing the connection stops Windows's read loop too
     * (its blocking socket read fails), which is what actually makes Windows tear
     * down capture/encode on its side - there's no separate "goodbye" message. */
    fun disconnectActiveSession() {
        activeConnection?.close()
    }

    suspend fun forgetTrustedHost(deviceId: String) {
        trustedDevices.remove(deviceId)
        if (activeHostId == deviceId) activeConnection?.close()
    }

    /** Only the established authenticated WebRTC control lane accepts companion commands. */
    fun requestCompanion(operation: String, text: String = "", actionId: String = ""): String? {
        val current = activeSession
        if (current == null || !_controlReady.value || current.sessionIdentity().isEmpty()) {
            _companionResult.value = CompanionResult.newBuilder().setSuccess(false).setDetail("Connect to a trusted computer first.").build()
            return null
        }
        if (pendingRequests.size >= 32) return null
        val requestId = UUID.randomUUID().toString()
        pendingRequests.add(requestId)
        _companionResult.value = null
        current.sendControlMessage(Envelope.newBuilder().setProtocolVersion(1).setSessionId(current.sessionIdentity())
            .setCompanionRequest(CompanionRequest.newBuilder().setRequestId(requestId).setOperation(operation).setText(text.take(65536)).setActionId(actionId.take(128))).build().toByteArray())
        scope.launch {
            kotlinx.coroutines.delay(if (operation in setOf("approved_action", "clipboard_get", "clipboard_set", "developer_start", "developer_stop")) 75000 else 10000)
            if (activeSession === current && pendingRequests.remove(requestId))
                _companionResult.value = CompanionResult.newBuilder().setRequestId(requestId).setSuccess(false).setDetail("No acknowledgement received from the computer.").build()
        }
        return requestId
    }

    fun startSession(connection: SignalingConnection, hostDisplayName: String, hostDeviceId: String, hostPublicKey: ByteArray, controlOnly: Boolean = false) {
        scope.launch {
        val preferences = AppSettingsRepository(appContext).streaming.first()
        activeConnection?.close()
        // LAN keeps an empty ICE list; the remote medium (pipe-backed connection) gets STUN, and TURN only if relay is allowed.
        val medium = connection.medium
        val isRemote = medium == ConnectionMedium.CONNECTION_MEDIUM_REMOTE
        val session = WebRtcSession(peerConnectionFactory, connection, preferences, controlOnly, remote.iceServers(medium), isRemote)
        var trustWatcher: kotlinx.coroutines.Job? = null
        val companionOwner = UUID.randomUUID().toString()
        activeSession = session
        activeConnection = connection
        activeHostId = hostDeviceId
        val transfer = AndroidFileTransfer(appContext, { activeSession === session && _controlReady.value }, { envelope ->
            activeSession === session && _controlReady.value && session.sendControlMessage(envelope.toByteArray())
        }) { ui -> if (activeSession === session) _transferUi.value = ui }
        activeTransfer = transfer
        _transferUi.value = FileTransferUi()
        _controlReady.value = false
        _companionResult.value = null
        pendingRequests.clear()
        _streamStats.value = null
        _negotiatedConfig.value = null
        _connectedHostName.value = hostDisplayName
        _sessionStartedAtMs.value = null
        _relayHint.value = false
        if (isRemote && !remote.isRelayAllowed) scope.launch {
            kotlinx.coroutines.delay(15_000)
            if (activeSession === session && _connectionState.value != PeerConnection.PeerConnectionState.CONNECTED) _relayHint.value = true
        }

        session.onConnectionStateChanged = stateChanged@ { state ->
            Log.d(Tag, "$hostDisplayName: $state")
            if (activeSession !== session) return@stateChanged
            _connectionState.value = state
            if (state == PeerConnection.PeerConnectionState.FAILED && isRemote && !remote.isRelayAllowed && _sessionStartedAtMs.value == null) _relayHint.value = true
            if (state == PeerConnection.PeerConnectionState.DISCONNECTED || state == PeerConnection.PeerConnectionState.FAILED || state == PeerConnection.PeerConnectionState.CLOSED) connection.close()
            if (state == PeerConnection.PeerConnectionState.CONNECTED && _sessionStartedAtMs.value == null)
                _sessionStartedAtMs.value = System.currentTimeMillis()
        }
        session.onPathChanged = pathChanged@ { kind ->
            if (activeSession !== session) return@pathChanged
            remote.reportPath(kind)
            if (_controlReady.value) session.sendControlMessage(Envelope.newBuilder().setProtocolVersion(1)
                .setSentAtUnixMs(Instant.now().toEpochMilli())
                .setConnectionStatus(ConnectionStatus.newBuilder().setState(ConnectionState.CONNECTION_STATE_CONNECTED)
                    .setMedium(medium).setPath(kind).build()).build().toByteArray())
        }
        session.onRemoteVideoTrack = { track -> if (activeSession === session) _remoteVideoTrack.value = track }
        session.onMetrics = { metrics -> if (activeSession === session) _streamStats.value = metrics }
        session.onControlChannelOpen = channelOpen@ {
            if (activeSession !== session) return@channelOpen
            Log.d(Tag, "control channel open with $hostDisplayName")
            _controlReady.value = true
            phoneHub.beginSession(companionOwner) { event ->
                if (activeSession === session && _controlReady.value) session.sendControlMessage(Envelope.newBuilder()
                    .setProtocolVersion(1).setSessionId(session.sessionIdentity()).setPhoneHubEvent(event).build().toByteArray())
            }
            val hello = Envelope.newBuilder()
                .setProtocolVersion(1)
                .setSentAtUnixMs(Instant.now().toEpochMilli())
                .setConnectionStatus(
                    ConnectionStatus.newBuilder()
                        .setState(ConnectionState.CONNECTION_STATE_CONNECTED)
                        .setMedium(medium)
                        .setDiagnosticDetail("Hello from Android.")
                        .build(),
                )
                .build()
            session.sendControlMessage(hello.toByteArray())
        }
        session.onControlMessage = controlMessage@ { data ->
            try {
                val envelope = Envelope.parseFrom(data)
                if (activeSession !== session || envelope.protocolVersion != 1) return@controlMessage
                if (envelope.hasFileTransferOffer() || envelope.hasFileTransferResponse() || envelope.hasFileTransferProgress() || envelope.hasFileTransferControl() || envelope.hasFileTransferChunk()) {
                    if (envelope.sessionId == session.sessionIdentity() && _controlReady.value) transfer.handle(envelope)
                    return@controlMessage
                }
                if (envelope.hasCompanionRequest() || envelope.hasCompanionResult()) {
                    if (envelope.sessionId != session.sessionIdentity() || !_controlReady.value) return@controlMessage
                    if (envelope.hasCompanionRequest()) phoneHub.request(companionOwner, envelope.companionRequest) { result ->
                        if (activeSession === session && _controlReady.value) session.sendControlMessage(Envelope.newBuilder()
                            .setProtocolVersion(1).setSessionId(session.sessionIdentity()).setCompanionResult(result).build().toByteArray())
                    }
                    else if (pendingRequests.remove(envelope.companionResult.requestId)) _companionResult.value = envelope.companionResult
                    return@controlMessage
                }
                if (envelope.hasNegotiatedConfig() && activeSession === session)
                    _negotiatedConfig.value = envelope.negotiatedConfig
                Log.d(Tag, "control message from $hostDisplayName: ${envelope.payloadCase}")
            } catch (_: Exception) {
                Log.d(Tag, "malformed control message from $hostDisplayName")
            }
        }
        connection.onClosed = {
            Log.d(Tag, "connection closed with $hostDisplayName")
            phoneHub.endSession(companionOwner)
            transfer.close()
            trustWatcher?.cancel()
            session.close()
            if (activeSession === session) {
                _controlReady.value = false
                _companionResult.value = null
                pendingRequests.clear()
                activeSession = null
                activeConnection = null
                activeHostId = null
                activeTransfer = null
                _transferUi.value = FileTransferUi(detail = "Disconnected; pending transfers canceled.")
                _remoteVideoTrack.value = null
                _connectionState.value = null
                _streamStats.value = null
                _connectedHostName.value = null
                _sessionStartedAtMs.value = null
                _negotiatedConfig.value = null
                remote.reportPath(null)
            }
        }

        // Wired above via WebRtcSession's constructor (signaling.onSessionOffer/
        // onIceCandidate) - start dispatching only now that a listener is in place.
        trustWatcher = scope.launch {
            trustedDevices.devices.first { devices ->
                activeSession !== session || devices.none { !it.revoked && it.device.deviceId == hostDeviceId && it.publicKey.toByteArray().contentEquals(hostPublicKey) }
            }
            if (activeSession === session) connection.close()
        }
        connection.startReadLoop(scope)
        }
    }
}
