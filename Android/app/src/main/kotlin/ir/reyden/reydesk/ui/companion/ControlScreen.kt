package ir.reyden.reydesk.ui.companion

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.background
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdDialog
import ir.reyden.reydesk.ui.components.ScreenHeading
import ir.reyden.reydesk.ui.components.SectionLabel
import ir.reyden.reydesk.ui.settings.SwitchRow
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskType
import org.json.JSONArray
import org.json.JSONObject

private data class ApprovedAction(val id: String, val label: String, val profile: String)
private data class DeveloperTask(val id: String, val label: String, val running: Boolean, val exitCode: Int?, val output: String)
private data class DeveloperStatus(val enabled: Boolean, val tasks: List<DeveloperTask>)

private fun parseDeveloperStatus(detail: String): DeveloperStatus? = runCatching {
    require(detail.length <= 8192)
    val json = JSONObject(detail)
    val tasks = json.getJSONArray("tasks")
    require(json.get("enabled") is Boolean && tasks.length() <= 12)
    val ids = mutableSetOf<String>()
    DeveloperStatus(json.getBoolean("enabled"), List(tasks.length()) { index ->
        val item = tasks.getJSONObject(index)
        val id = item.getString("id")
        val label = item.getString("label")
        val output = item.getString("output")
        require(id.length in 1..32 && id.none { it.isISOControl() } && ids.add(id))
        require(label.length in 1..80 && label.none { it.isISOControl() } && output.length <= 4096)
        require(item.get("running") is Boolean && (item.isNull("exitCode") || item.get("exitCode") is Int))
        DeveloperTask(id, label, item.getBoolean("running"), if (item.isNull("exitCode")) null else item.getInt("exitCode"), output)
    })
}.getOrNull()

private fun parseApprovedActions(detail: String): List<ApprovedAction>? = runCatching {
    require(detail.length <= 20000)
    val array = JSONArray(detail)
    require(array.length() <= 24)
    val ids = mutableSetOf<String>()
    fun safe(value: String, limit: Int) = value.isNotBlank() && value.length <= limit && value.none { it.isISOControl() }
    List(array.length()) { index ->
        val item = array.getJSONObject(index)
        val id = item.getString("id")
        val label = item.getString("label")
        val profile = item.getString("profile")
        require(safe(id, 128) && safe(label, 100) && safe(profile, 64) && ids.add(id))
        ApprovedAction(id, label, profile)
    }
}.getOrNull()

@Composable
fun ControlScreen(onDesktop: () -> Unit, onBack: () -> Unit, onFiles: () -> Unit = {}) {
    val context = LocalContext.current
    val lang = context.resources.configuration.locales[0]?.language ?: "en"
    val session = (context.applicationContext as ReyDeskApplication).session
    val ready by session.controlReady.collectAsState()
    val result by session.companionResult.collectAsState()
    val host by session.connectedHostName.collectAsState()
    var clipboard by remember { mutableStateOf("") }
    var confirmLock by remember { mutableStateOf(false) }
    var desktopRequested by remember { mutableStateOf<String?>(null) }
    var hadHost by remember { mutableStateOf(false) }
    var actions by remember { mutableStateOf<List<ApprovedAction>>(emptyList()) }
    var listRequest by remember { mutableStateOf<String?>(null) }
    var lastListRequest by remember { mutableStateOf<String?>(null) }
    var actionRequest by remember { mutableStateOf<String?>(null) }
    var actionFilter by remember { mutableStateOf("") }
    var listLoaded by remember { mutableStateOf(false) }
    var listFailed by remember { mutableStateOf(false) }
    val controlPreferences = remember { context.getSharedPreferences("reydesk_control", android.content.Context.MODE_PRIVATE) }
    var showDeveloper by remember { mutableStateOf(controlPreferences.getBoolean("show_developer", false)) }
    var developer by remember { mutableStateOf<DeveloperStatus?>(null) }
    var developerRequest by remember { mutableStateOf<String?>(null) }
    var lastDeveloperRequest by remember { mutableStateOf<String?>(null) }
    var developerFailed by remember { mutableStateOf(false) }
    LaunchedEffect(ready, host) {
        if (!ready) { actions = emptyList(); listRequest = null; lastListRequest = null; actionRequest = null; listLoaded = false; listFailed = false; developer = null; developerRequest = null; lastDeveloperRequest = null; developerFailed = false }
    }
    LaunchedEffect(host) {
        if (host != null) hadHost = true else if (hadHost) onBack()
    }
    LaunchedEffect(result) {
        if (developerRequest != null && result?.requestId == developerRequest) {
            developer = if (result?.success == true && showDeveloper) parseDeveloperStatus(result!!.detail) else null
            developerFailed = developer == null
            developerRequest = null
        }
        if (listRequest != null && result?.requestId == listRequest) {
            val parsed = if (result?.success == true) parseApprovedActions(result!!.detail) else null
            actions = parsed.orEmpty()
            listLoaded = true
            listFailed = parsed == null
            listRequest = null
        }
        if (actionRequest != null && result?.requestId == actionRequest) actionRequest = null
        if (desktopRequested != null && result?.requestId == desktopRequested) {
            desktopRequested = null
            if (result?.success == true) onDesktop()
        }
    }
    if (confirmLock) RdDialog(
        onDismiss = { confirmLock = false }, title = stringResource(R.string.companion_lock),
        confirmText = stringResource(R.string.companion_lock), dismissText = stringResource(android.R.string.cancel), destructive = true,
        onConfirm = { confirmLock = false; session.requestCompanion("lock") },
    ) { Text(stringResource(R.string.companion_lock_confirm), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted) }
    Column(Modifier.fillMaxSize().background(ReyDeskColors.Bg).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        ScreenHeading(stringResource(R.string.companion_control), host ?: stringResource(R.string.companion_connect_first))
        Text(stringResource(R.string.companion_control_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            RdButton(stringResource(R.string.companion_desktop), { desktopRequested = session.requestCompanion("desktop") }, Modifier.weight(1f), glyph = "connection", enabled = ready && desktopRequested == null)
            RdButton(stringResource(R.string.companion_disconnect), { session.disconnectActiveSession(); onBack() }, Modifier.weight(1f), kind = RdButtonKind.Secondary, glyph = "disconnect")
        }
        RdButton(stringResource(R.string.companion_files), onFiles, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "transfers")
        ConsolePanel(Modifier.fillMaxWidth()) {
            SectionLabel(stringResource(R.string.companion_approved_actions), "shield")
            Text(stringResource(R.string.companion_actions_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            RdButton(stringResource(R.string.companion_actions_refresh), {
                listRequest = session.requestCompanion("list_actions")
                lastListRequest = listRequest
            }, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "refresh", enabled = ready && listRequest == null && actionRequest == null)
            if (actions.isNotEmpty()) {
                RdTextField(actionFilter, { if (it.length <= 100) actionFilter = it }, stringResource(R.string.companion_actions_filter), singleLine = true)
                actions.filter { it.label.contains(actionFilter, true) || it.profile.contains(actionFilter, true) }.groupBy { it.profile }.forEach { (profile, items) ->
                    Text(profile, style = MaterialTheme.typography.labelMedium, color = ReyDeskColors.Muted)
                    items.forEach { action ->
                        RdButton(action.label, { actionRequest = session.requestCompanion("approved_action", actionId = action.id) }, Modifier.fillMaxWidth(),
                            kind = RdButtonKind.Secondary, enabled = ready && actionRequest == null && listRequest == null)
                    }
                }
            }
            if (listLoaded && actions.isEmpty()) Text(stringResource(if (listFailed) R.string.companion_actions_failed else R.string.companion_actions_empty), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            if (actionRequest != null) Text(stringResource(R.string.companion_actions_waiting), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
        }
        ConsolePanel(Modifier.fillMaxWidth()) {
            SectionLabel(stringResource(R.string.companion_media), "pulse")
            listOf(listOf("previous" to R.string.companion_previous, "next" to R.string.companion_next),
                listOf("play_pause" to R.string.companion_play_pause, "mute" to R.string.companion_mute),
                listOf("volume_down" to R.string.companion_volume_down, "volume_up" to R.string.companion_volume_up)).forEach { row ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    row.forEach { (operation, label) -> RdButton(stringResource(label), { session.requestCompanion(operation) }, Modifier.weight(1f), kind = RdButtonKind.Secondary, enabled = ready) }
                }
            }
            RdButton(stringResource(R.string.companion_lock), { confirmLock = true }, Modifier.fillMaxWidth(), kind = RdButtonKind.Danger, glyph = "power", enabled = ready)
        }
        ConsolePanel(Modifier.fillMaxWidth()) {
            SectionLabel(stringResource(R.string.companion_clipboard), "copy")
            Text(stringResource(R.string.companion_clipboard_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            RdTextField(clipboard, { if (it.length <= 8192) clipboard = it }, stringResource(R.string.companion_clipboard_text), helper = "${Digits.localize(clipboard.length.toString(), lang)} / ${Digits.localize("8192", lang)}", minLines = 3)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                RdButton(stringResource(R.string.companion_clipboard_get), { session.requestCompanion("clipboard_get") }, Modifier.weight(1f), kind = RdButtonKind.Secondary, enabled = ready)
                RdButton(stringResource(R.string.companion_clipboard_set), { session.requestCompanion("clipboard_set", clipboard) }, Modifier.weight(1f), enabled = ready)
            }
        }
        ConsolePanel(Modifier.fillMaxWidth()) {
            SwitchRow(stringResource(R.string.companion_show_developer), showDeveloper, {
                showDeveloper = it
                controlPreferences.edit().putBoolean("show_developer", it).apply()
                if (!it) { developer = null; developerFailed = false }
            }, glyph = "code")
            if (showDeveloper) {
                Text(stringResource(R.string.companion_developer_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Warning)
                RdButton(stringResource(R.string.companion_developer_refresh), {
                    developerRequest = session.requestCompanion("developer_status")
                    lastDeveloperRequest = developerRequest
                }, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "refresh", enabled = ready && developerRequest == null && actionRequest == null)
                if (developerFailed) Text(stringResource(R.string.companion_actions_failed), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Danger)
                developer?.let { status ->
                    if (!status.enabled) Text(stringResource(R.string.companion_developer_disabled), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                    if (status.tasks.isEmpty()) Text(stringResource(R.string.companion_developer_empty), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                    status.tasks.forEach { task ->
                        HorizontalDivider(color = ReyDeskColors.Line)
                        Text(task.label, style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                        Text(if (task.running) stringResource(R.string.companion_developer_running) else task.exitCode?.let { stringResource(R.string.companion_developer_exit, it) } ?: stringResource(R.string.companion_developer_stopped), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                        RdButton(stringResource(if (task.running) R.string.companion_developer_stop else R.string.companion_developer_start), {
                            actionRequest = session.requestCompanion(if (task.running) "developer_stop" else "developer_start", actionId = task.id)
                        }, Modifier.fillMaxWidth(), kind = if (task.running) RdButtonKind.Danger else RdButtonKind.Secondary,
                            enabled = ready && status.enabled && actionRequest == null && developerRequest == null)
                        if (task.output.isNotEmpty()) Text(task.output, style = ReyDeskType.Mono, color = ReyDeskColors.Muted)
                    }
                }
                if (actionRequest != null) Text(stringResource(R.string.companion_actions_waiting), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            }
        }
        result?.takeIf { it.requestId != lastListRequest && it.requestId != lastDeveloperRequest }?.let {
            ConsolePanel(Modifier.fillMaxWidth()) {
                Text(stringResource(if (it.success) R.string.companion_ack_success else R.string.companion_ack_failed), style = MaterialTheme.typography.titleMedium, color = if (it.success) ReyDeskColors.Success else ReyDeskColors.Danger)
                Text(it.detail.take(20000), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
            }
        }
        if (!ready) Text(stringResource(R.string.companion_connect_first), color = ReyDeskColors.Muted, style = MaterialTheme.typography.bodySmall)
    }
}
