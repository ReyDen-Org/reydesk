package ir.reyden.reydesk.ui.devices

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.pairing.LanEndpoint
import ir.reyden.reydesk.pairing.LanHostDiscovery
import ir.reyden.reydesk.protocol.v1.TrustedDevice
import ir.reyden.reydesk.ui.connection.SasDialog
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.PathBadge
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdDialog
import ir.reyden.reydesk.ui.components.RdIconButton
import ir.reyden.reydesk.ui.components.RdIconTile
import ir.reyden.reydesk.ui.components.ScreenHeading
import ir.reyden.reydesk.ui.components.SkeletonBlock
import ir.reyden.reydesk.ui.connection.RemoteUiState
import ir.reyden.reydesk.ui.connection.pathNameRes
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import kotlinx.coroutines.launch

/**
 * Saved identities are paired devices, not an assertion that those PCs are online.
 * [remoteByDevice] (keyed by device id) carries the internet state once identity v2 provides it; empty until then.
 * [onReconnect] shows the Reconnect button when set.
 */
@Composable
fun DevicesScreen(
    onAddComputer: () -> Unit,
    remoteByDevice: Map<String, RemoteUiState>? = null,
    onReconnect: ((TrustedDevice) -> Unit)? = null,
    onSessionStarted: (Boolean) -> Unit = {},
) {
    val context = LocalContext.current
    val session = (context.applicationContext as ReyDeskApplication).session
    val loaded by session.trustedDevices.devices.collectAsState<List<TrustedDevice>?, List<TrustedDevice>?>(initial = null)
    val scope = rememberCoroutineScope()
    var forgetting by remember { mutableStateOf<TrustedDevice?>(null) }
    var removeError by remember { mutableStateOf(false) }
    var reconnectingId by remember { mutableStateOf<String?>(null) }
    var reconnectError by remember { mutableStateOf<String?>(null) }
    val rc = session.remote
    val liveRemote by rc.uiState.collectAsState()

    // Reconnect: prefer the LAN endpoint when mDNS still sees the PC (key must match), otherwise the Desk ID pipe.
    fun reconnect(device: TrustedDevice) {
        reconnectingId = device.device.deviceId; reconnectError = null
        scope.launch {
            try {
                val key = device.publicKey.toByteArray()
                val lan = try {
                    LanHostDiscovery.findHosts(context).firstOrNull { it.id == device.device.deviceId && it.hasKey(key) }?.let { LanEndpoint(it.address, it.port) }
                } catch (e: kotlinx.coroutines.CancellationException) { throw e } catch (_: Exception) { null }
                // Hands-free first: the PC accepts it when "Allow automatic reconnect" is on for this phone. If the PC refuses
                // (or the unattended attempt fails), fall back to the attended request that asks on the PC.
                val auto = try { rc.reconnect(device, lan, unattended = true) } catch (e: kotlinx.coroutines.CancellationException) { throw e } catch (_: Exception) { null }
                val host = if (auto?.outcome?.accepted == true) auto else rc.reconnect(device, lan, unattended = false)
                if (!host.outcome.accepted) {
                    reconnectError = host.outcome.response.reason.ifEmpty { host.outcome.response.result.name }
                } else try {
                    session.startSession(host.outcome.connection, host.displayName, host.deviceId, host.publicKey)
                    onSessionStarted(false)
                } catch (error: Exception) {
                    host.outcome.connection.close()
                    if (error is kotlinx.coroutines.CancellationException) throw error
                    reconnectError = error.message ?: error.javaClass.simpleName
                }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                reconnectError = e.message ?: e.javaClass.simpleName
            } finally { reconnectingId = null }
        }
    }
    val reconnectAction: ((TrustedDevice) -> Unit)? = onReconnect ?: ::reconnect
    liveRemote.sas?.let { sas -> SasDialog(sas, onSame = { rc.answerSas(true) }, onDifferent = { rc.answerSas(false) }) }
    reconnectError?.let { reason ->
        RdDialog(
            onDismiss = { reconnectError = null }, title = stringResource(R.string.companion_reconnect),
            confirmText = stringResource(android.R.string.ok), onConfirm = { reconnectError = null },
        ) { Text(stringResource(R.string.connection_pairing_failed, reason), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted) }
    }
    forgetting?.let { device ->
        RdDialog(
            onDismiss = { forgetting = null }, title = stringResource(R.string.devices_forget_title),
            confirmText = stringResource(R.string.devices_forget), dismissText = stringResource(android.R.string.cancel), destructive = true,
            onConfirm = {
                scope.launch {
                    try { session.forgetTrustedHost(device.device.deviceId); forgetting = null }
                    catch (error: Exception) {
                        if (error is kotlinx.coroutines.CancellationException) throw error
                        android.util.Log.e("DevicesScreen", "Could not forget trusted computer", error)
                        forgetting = null; removeError = true
                    }
                }
            },
        ) { Text(stringResource(R.string.devices_forget_body, device.device.displayName), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted) }
    }
    if (removeError) RdDialog(
        onDismiss = { removeError = false }, title = stringResource(R.string.devices_forget_title),
        confirmText = stringResource(android.R.string.ok), onConfirm = { removeError = false },
    ) { Text(stringResource(R.string.devices_forget_failed), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted) }
    Box(Modifier.fillMaxSize().background(ReyDeskColors.Bg), contentAlignment = Alignment.TopCenter) {
        LazyColumn(
            modifier = Modifier.widthIn(max = 560.dp).fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            item { ScreenHeading(stringResource(R.string.nav_devices), stringResource(R.string.ui_devices_subtitle)) }
            val devices = loaded
            if (devices == null) {
                items(2) { DeviceSkeleton() }
            } else if (devices.isEmpty()) {
                item {
                    ConsolePanel(Modifier.fillMaxWidth()) {
                        RdIconTile("devices")
                        Text(stringResource(R.string.devices_title), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                        Text(stringResource(R.string.ui_devices_empty_body), color = ReyDeskColors.Muted, style = MaterialTheme.typography.bodyMedium)
                        RdButton(stringResource(R.string.ui_add_computer), onAddComputer, Modifier.fillMaxWidth(), glyph = "plus")
                    }
                }
            } else {
                items(devices, key = { it.device.deviceId }) { DeviceRow(it, remoteByDevice?.get(it.device.deviceId) ?: deviceRemote(it, liveRemote), reconnectAction, reconnectingId == null) { forgetting = it } }
                item {
                    Text(stringResource(R.string.ui_devices_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted, modifier = Modifier.padding(top = 4.dp))
                }
                item { RdButton(stringResource(R.string.ui_add_computer), onAddComputer, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "plus") }
            }
        }
    }
}

/** Per-device view of the app state: the device's own Key ID, never the current session path (that belongs to the Connection screen). */
private fun deviceRemote(device: TrustedDevice, live: RemoteUiState): RemoteUiState = RemoteUiState(
    deskId = device.deskId.ifEmpty { null },
    keyId = DeskIdCodec.keyIdDisplay(DeskIdCodec.fingerprint(device.publicKey.toByteArray())),
    serverState = live.serverState, path = null, remoteAllowed = live.remoteAllowed,
)

@Composable
private fun DeviceSkeleton() {
    ConsolePanel(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            SkeletonBlock(Modifier.size(34.dp), ReyDeskShapes.Tile)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                SkeletonBlock(Modifier.fillMaxWidth(.6f).height(15.dp))
                SkeletonBlock(Modifier.fillMaxWidth(.4f).height(12.dp))
            }
        }
    }
}

@Composable
private fun Chip(text: String, tint: androidx.compose.ui.graphics.Color, bg: androidx.compose.ui.graphics.Color, line: androidx.compose.ui.graphics.Color) {
    Text(
        text, style = MaterialTheme.typography.labelSmall, color = tint,
        modifier = Modifier.height(26.dp).clip(ReyDeskShapes.Pill).background(bg, ReyDeskShapes.Pill).border(1.dp, line, ReyDeskShapes.Pill)
            .padding(horizontal = 10.dp, vertical = 5.dp),
    )
}

@Composable
private fun DeviceRow(device: TrustedDevice, remote: RemoteUiState?, onReconnect: ((TrustedDevice) -> Unit)?, canReconnect: Boolean, onForget: () -> Unit) {
    ConsolePanel(Modifier.fillMaxWidth(), highlighted = remote?.path != null) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            RdIconTile("connection")
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(device.device.displayName, style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                Text(stringResource(R.string.devices_paired_status), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                remote?.keyId?.let { Text("KEY $it", style = ir.reyden.reydesk.ui.theme.ReyDeskType.Mono, color = ReyDeskColors.Faint) }
            }
            RdIconButton("trash", onForget, stringResource(R.string.devices_forget), danger = true)
        }
        if (remote != null) Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            if (!remote.remoteAllowed) Chip(stringResource(R.string.devices_internet_off), ReyDeskColors.Muted, ReyDeskColors.Hover, ReyDeskColors.Line)
            remote.path?.let { PathBadge(it, stringResource(pathNameRes(it))) }
        }
        if (onReconnect != null) RdButton(stringResource(R.string.companion_reconnect), { onReconnect(device) }, Modifier.fillMaxWidth(), enabled = canReconnect)
    }
}
