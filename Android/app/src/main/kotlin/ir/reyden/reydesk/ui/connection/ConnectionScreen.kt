package ir.reyden.reydesk.ui.connection

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.google.protobuf.ByteString
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.remote.ConnectedHost
import ir.reyden.reydesk.pairing.*
import ir.reyden.reydesk.protocol.v1.*
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.PathBadge
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdDialog
import ir.reyden.reydesk.ui.components.RdIconButton
import ir.reyden.reydesk.ui.components.RdIconTile
import ir.reyden.reydesk.ui.components.RdPath
import ir.reyden.reydesk.ui.components.RdSegmented
import ir.reyden.reydesk.ui.components.RingState
import ir.reyden.reydesk.ui.components.SectionLabel
import ir.reyden.reydesk.ui.components.SkeletonBlock
import ir.reyden.reydesk.ui.components.StatusRing
import ir.reyden.reydesk.ui.tutorial.TutorialFirstRunCard
import ir.reyden.reydesk.ui.tutorial.tutorialTarget
import ir.reyden.reydesk.ui.components.AppGlyph
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.identity.DeskIdCodec
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private sealed interface PairingState {
    data object Idle : PairingState
    data object Connecting : PairingState
    data class Succeeded(val hostName: String) : PairingState
    data class Failed(val reason: String) : PairingState
}

/**
 * Real QR scanning and a working pairing handshake: scanning a Windows offer and
 * tapping Connect exchanges a signed PairingRequest/PairingResponse with
 * Pairing/PairingListener.cs over TCP (Pairing/PairingClient.kt) and persists the
 * result via SessionCoordinator.trustedDevices. The PIN field was removed from this screen by the
 * design contract; the PIN pairing logic stays until identity v2 replaces it.
 */
/** Last non-empty LAN browse result, kept across screen visits and empty re-browses for [NearbyGraceMs]. */
private var lastFoundHosts: List<LanHostMetadata> = emptyList()
private var lastFoundAt = 0L
private const val NearbyGraceMs = 20_000L

@Composable
fun ConnectionScreen(
    onSessionStarted: (Boolean) -> Unit,
    remote: RemoteUiState? = null,
    onInternetConnect: ((deskId: String, code: String) -> Unit)? = null,
    onSasResult: ((same: Boolean) -> Unit)? = null,
    onNearbyConnect: ((LanHostMetadata) -> Unit)? = null,
    onOpenTutorial: () -> Unit = {},
) {

    val context = LocalContext.current
    val session = (context.applicationContext as ReyDeskApplication).session
    val trustedDevices by session.trustedDevices.devices.collectAsState(initial = emptyList())
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    val coroutineScope = rememberCoroutineScope()
    val rc = session.remote
    val liveRemote by rc.uiState.collectAsState()
    val remoteUi = remote ?: liveRemote

    var scannedOffer by remember { mutableStateOf<PairingOffer?>(null) }
    var scanError by remember { mutableStateOf(false) }
    var deskIdInput by rememberSaveable { mutableStateOf("") }
    var codeInput by rememberSaveable { mutableStateOf("") }
    var pairingState by remember { mutableStateOf<PairingState>(PairingState.Idle) }
    var hosts by remember { mutableStateOf(if (System.currentTimeMillis() - lastFoundAt < NearbyGraceMs) lastFoundHosts else emptyList<LanHostMetadata>()) }
    var discovering by remember { mutableStateOf(false) }
    var discoveryError by remember { mutableStateOf(false) }
    var browseAttempt by remember { mutableStateOf(0) }
    var cameraDenied by remember { mutableStateOf(false) }
    var controlOnly by rememberSaveable { mutableStateOf(false) }
    var reconnectHost by remember { mutableStateOf<LanHostMetadata?>(null) }
    var hostsObservedAt by remember { mutableStateOf(if (hosts.isNotEmpty()) lastFoundAt else 0L) }

    LaunchedEffect(browseAttempt) {
        discovering = true
        discoveryError = false
        try {
            val found = LanHostDiscovery.findHosts(context)
            val now = System.currentTimeMillis()
            if (found.isNotEmpty()) { lastFoundHosts = found; lastFoundAt = now }
            // NSD sometimes returns nothing on a network that had a computer seconds ago: keep the last result for a grace period.
            val keepLast = found.isEmpty() && lastFoundHosts.isNotEmpty() && now - lastFoundAt < NearbyGraceMs
            hosts = if (keepLast) lastFoundHosts else found
            hostsObservedAt = if (keepLast) lastFoundAt else now
        } catch (e: CancellationException) { throw e }
        catch (_: Exception) { discoveryError = true; hosts = emptyList() }
        finally { discovering = false }
    }

    fun connect(offer: PairingOffer, allowFirstUse: Boolean = false, reconnect: Boolean = false) {
        pairingState = PairingState.Connecting
        coroutineScope.launch {
            val outcome = try {
                val identity = session.getOrCreateIdentity()
                if (reconnect) {
                    check(lifecycle.currentState.isAtLeast(androidx.lifecycle.Lifecycle.State.RESUMED)) { "Confirm reconnect while ReyDesk is open." }
                    PairingClient.reconnect(offer, identity, session.trustedDevices, userConfirmedInForeground = true)
                } else PairingClient.pair(offer, identity, session.trustedDevices, allowFirstUse)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                pairingState = PairingState.Failed(pairingFailureText(context, e))
                return@launch
            }
            pairingState = when (outcome.response.result) {
                PairingResult.PAIRING_RESULT_ACCEPTED, PairingResult.PAIRING_RESULT_ALREADY_TRUSTED -> {
                    try {
                    session.trustedDevices.addOrUpdate(offer.host, offer.hostPublicKey.toByteArray(), allowFirstUse = allowFirstUse && !reconnect)
                    // The connection stays open and is handed to SessionCoordinator to
                    // carry WebRTC signaling - see its own doc comment - then the UI
                    // moves straight to the full-screen Session view.
                    session.startSession(outcome.connection, offer.host.displayName, offer.host.deviceId, offer.hostPublicKey.toByteArray(), controlOnly)
                    onSessionStarted(controlOnly)
                    PairingState.Succeeded(offer.host.displayName)
                    } catch (error: Exception) {
                        outcome.connection.close()
                        if (error is CancellationException) throw error
                        PairingState.Failed(error.message ?: "Saved computer permission changed. Scan its QR code again.")
                    }
                }
                else -> {
                    outcome.connection.close()
                    PairingState.Failed(pairingFailureText(context, null, outcome.response))
                }
            }
        }
    }

    // Internet and nearby (SAS) connects: RemoteController returns a ConnectedHost; the session starts only if accepted.
    fun runRemoteConnect(wipe: CharArray? = null, block: suspend () -> ConnectedHost) {
        pairingState = PairingState.Connecting
        coroutineScope.launch {
            try {
                val host = block()
                if (!host.outcome.accepted) {
                    pairingState = PairingState.Failed(pairingFailureText(context, null, host.outcome.response))
                } else {
                    try {
                        session.startSession(host.outcome.connection, host.displayName, host.deviceId, host.publicKey, controlOnly)
                        onSessionStarted(controlOnly)
                        pairingState = PairingState.Succeeded(host.displayName)
                    } catch (error: Exception) {
                        host.outcome.connection.close()
                        if (error is CancellationException) throw error
                        pairingState = PairingState.Failed(error.message ?: error.javaClass.simpleName)
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                pairingState = PairingState.Failed(pairingFailureText(context, e))
            } finally {
                wipe?.fill('\u0000')
            }
        }
    }
    val nearbyConnect: (LanHostMetadata) -> Unit = onNearbyConnect ?: { host -> runRemoteConnect { rc.connectNearby(host) } }
    val internetConnect: (String, String) -> Unit = onInternetConnect ?: { id, code ->
        val chars = code.toCharArray()
        codeInput = ""
        runRemoteConnect(chars) { rc.connectByDeskId(id, chars) }
    }
    val answerSas: (Boolean) -> Unit = onSasResult ?: rc::answerSas

    val scanLauncher = rememberLauncherForActivityResult(ScanContract()) { result ->
        val contents = result.contents
        if (contents != null) {
            val offer = PairingOfferParser.parse(contents)
            scannedOffer = offer
            scanError = offer == null
            pairingState = PairingState.Idle
        }
    }

    fun launchScan() {
        scanLauncher.launch(
            ScanOptions()
                .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setBeepEnabled(false)
                .setOrientationLocked(false),
        )
    }

    val cameraPermissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        cameraDenied = !granted
        if (granted) launchScan()
    }


    val busy = pairingState is PairingState.Connecting
    val failed = pairingState as? PairingState.Failed
    reconnectHost?.let { host ->
        RdDialog(
            onDismiss = { reconnectHost = null }, title = stringResource(R.string.companion_reconnect),
            confirmText = stringResource(R.string.companion_reconnect), dismissText = stringResource(android.R.string.cancel),
            onConfirm = {
                reconnectHost = null
                connect(PairingOffer.newBuilder()
                    .setHost(DeviceIdentity.newBuilder().setDeviceId(host.id).setDisplayName(host.name).setPlatform(Platform.PLATFORM_WINDOWS))
                    .setHostPublicKey(ByteString.copyFrom(host.publicKey)).setLocalIpHint(host.address).setLocalPortHint(host.port)
                    .setExpiresAtUnixMs(hostsObservedAt + 300_000).build(), reconnect = true)
            },
        ) { Text(stringResource(R.string.companion_reconnect_confirm, host.name), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted) }
    }
    remoteUi.sas?.let { sas -> SasDialog(sas, onSame = { answerSas(true) }, onDifferent = { answerSas(false) }) }
    Box(Modifier.fillMaxSize().background(ReyDeskColors.Bg), contentAlignment = Alignment.TopCenter) {
        Column(
            Modifier.widthIn(max = 560.dp).fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            TutorialFirstRunCard(onOpenTutorial)
            val modeLabels = mapOf(false to stringResource(R.string.companion_desktop), true to stringResource(R.string.companion_control))
            RdSegmented(
                options = listOf(false, true), selected = controlOnly,
                onSelect = { if (!busy) controlOnly = it },
                optionLabel = { modeLabels.getValue(it) },
                modifier = Modifier.fillMaxWidth().tutorialTarget("mode"),
            )
            if (controlOnly) Text(stringResource(R.string.companion_control_hint), color = ReyDeskColors.Muted, style = MaterialTheme.typography.bodySmall)
            Text(stringResource(R.string.companion_secure_pairing_hint), color = ReyDeskColors.Muted, style = MaterialTheme.typography.bodySmall)
            val ringState = when {
                busy -> RingState.Connecting
                failed != null -> RingState.Error
                else -> RingState.Idle
            }
            val remotePath = remoteUi.path
            Box(Modifier.fillMaxWidth().tutorialTarget("ring"), contentAlignment = Alignment.Center) {
                StatusRing(
                    state = ringState,
                    statusText = when {
                        busy -> stringResource(R.string.connection_pairing_progress)
                        failed != null -> stringResource(R.string.connection_pairing_failed, failed.reason)
                        else -> stringResource(R.string.ring_idle)
                    },
                    compact = true,
                    onClick = if (busy) null else {
                        {
                            val hasPermission = ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED
                            if (hasPermission) launchScan() else cameraPermissionLauncher.launch(Manifest.permission.CAMERA)
                        }
                    },
                    actionLabel = stringResource(R.string.connection_scan_button),
                    badge = if (remotePath != null) { { PathBadge(remotePath, stringResource(pathNameRes(remotePath))) } } else null,
                )
            }
            Text(stringResource(R.string.ui_scan_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted,
                modifier = Modifier.fillMaxWidth(), textAlign = androidx.compose.ui.text.style.TextAlign.Center)
            if (cameraDenied) Notice(stringResource(R.string.camera_permission_rationale))
            if (scanError) Notice(stringResource(R.string.connection_scan_invalid))
            scannedOffer?.let { offer ->
                ConsolePanel(Modifier.fillMaxWidth()) {
                    Text(stringResource(R.string.connection_scanned_device, offer.host.displayName), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                    // M1 with the M3 fallback: the controller passes its rendezvous client so an unreachable LAN hint uses the relay pipe.
                    RdButton(stringResource(R.string.connection_connect_button), { runRemoteConnect { rc.connectByQr(offer) } }, Modifier.fillMaxWidth(), enabled = !busy)
                }
            }
            ConsolePanel(Modifier.fillMaxWidth().tutorialTarget("nearby")) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    SectionLabel(stringResource(R.string.ui_nearby_computers), "connection", Modifier.weight(1f))
                    RdIconButton("refresh", { browseAttempt++ }, stringResource(R.string.pin_find_hosts), enabled = !busy && !discovering)
                }
                if (discovering && hosts.isEmpty()) repeat(2) {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        SkeletonBlock(Modifier.size(28.dp), ReyDeskShapes.TileSm)
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            SkeletonBlock(Modifier.fillMaxWidth(.6f).height(14.dp))
                            SkeletonBlock(Modifier.fillMaxWidth(.4f).height(12.dp))
                        }
                        SkeletonBlock(Modifier.width(88.dp).height(48.dp), ReyDeskShapes.Control)
                    }
                } else for (host in hosts) {
                    val knownHost = trustedDevices.any { !it.revoked && it.device.deviceId == host.id && java.security.MessageDigest.isEqual(it.publicKey.toByteArray(), host.publicKey) }
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        RdIconTile("connection", small = true)
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(host.name, style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                            PathBadge(RdPath.Lan, stringResource(R.string.path_lan))
                        }
                        if (knownHost) RdButton(
                            stringResource(R.string.companion_reconnect), { reconnectHost = host }, kind = RdButtonKind.Tonal,
                            enabled = !busy && !discovering && System.currentTimeMillis() < hostsObservedAt + 300_000,
                        ) else RdButton(
                            stringResource(R.string.connection_connect_button), { nearbyConnect(host) }, kind = RdButtonKind.Tonal,
                            enabled = !busy,
                        )
                    }
                }
                if (!discovering && hosts.isEmpty()) Text(
                    stringResource(if (discoveryError) R.string.pin_discovery_failed else R.string.pin_no_hosts),
                    style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted,
                )
            }
            ConsolePanel(Modifier.fillMaxWidth().tutorialTarget("internet")) {
                SectionLabel(stringResource(R.string.internet_title), "globe")
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Box(Modifier.size(8.dp).background(remoteStateColor(remoteUi.serverState), ReyDeskShapes.Pill))
                    Text(remoteStateText(remoteUi.serverState), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                }
                DeskIdField(deskIdInput, { deskIdInput = it }, enabled = !busy)
                CodeCells(codeInput, { codeInput = it }, enabled = !busy)
                val idOk = DeskIdCodec.parse(deskIdInput) is DeskIdCodec.Result.Ok
                val codeOk = DeskIdCodec.parseRemoteCode(codeInput) is DeskIdCodec.Result.Ok
                RdButton(
                    stringResource(R.string.connection_connect_button), { internetConnect(deskIdInput, codeInput) }, Modifier.fillMaxWidth(),
                    glyph = "globe", enabled = !busy && remoteUi.remoteAllowed && idOk && codeOk,
                )
                if (!remoteUi.remoteAllowed) Text(stringResource(R.string.internet_turn_on), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Faint)
            }
            ConsolePanel(Modifier.fillMaxWidth()) {
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    AppGlyph("help", Modifier.size(20.dp), ReyDeskColors.Muted)
                    Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
                        Text(stringResource(R.string.ui_pc_help_title), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                        Text(stringResource(R.string.ui_connection_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                    }
                }
                RdButton(stringResource(R.string.tut_open), onOpenTutorial, Modifier.fillMaxWidth(), kind = RdButtonKind.Tonal, glyph = "help")
            }
        }
    }
}

@Composable
private fun Notice(text: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.Top) {
        AppGlyph("warning", Modifier.size(16.dp), ReyDeskColors.Danger)
        Text(text, style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Danger)
    }
}

internal fun pathNameRes(path: RdPath): Int = when (path) {
    RdPath.Lan -> R.string.path_lan
    RdPath.Direct -> R.string.path_direct
    RdPath.Relay -> R.string.path_relay
}
