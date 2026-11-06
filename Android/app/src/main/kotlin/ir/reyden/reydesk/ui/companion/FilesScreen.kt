package ir.reyden.reydesk.ui.companion

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.companion.ReceiveFolderStore
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdIconTile
import ir.reyden.reydesk.ui.components.ScreenHeading
import ir.reyden.reydesk.ui.components.SectionLabel
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskType

@Composable
fun FilesScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val session = (context.applicationContext as ReyDeskApplication).session
    val folders = remember { ReceiveFolderStore(context.applicationContext) }
    var selectedFolder by remember { mutableStateOf(folders.selected()) }
    var folderError by remember { mutableStateOf(false) }
    val chooseFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            folderError = runCatching { folders.remember(uri) }.isFailure
            selectedFolder = folders.selected()
        }
    }
    val transfer by session.transferUi.collectAsState()
    val ready by session.controlReady.collectAsState()
    var pickerTransferId by rememberSaveable { mutableStateOf<String?>(null) }
    var pickerSessionId by rememberSaveable { mutableStateOf("") }
    var senderSessionId by rememberSaveable { mutableStateOf("") }
    val openFile = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri -> if (uri != null) session.sendFile(uri, senderSessionId) }
    val saveFile = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        pickerTransferId?.let { id -> session.chooseFileDestination(id, uri, pickerSessionId) }
        pickerTransferId = null
    }
    val lang = context.resources.configuration.locales[0]?.language ?: "en"
    val busy = transfer.state in listOf("preparing", "waiting", "sending", "receiving", "offer")
    Column(Modifier.fillMaxSize().background(ReyDeskColors.Bg).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        RdButton(stringResource(R.string.companion_back), onBack, kind = RdButtonKind.Ghost)
        ScreenHeading(stringResource(R.string.companion_files), stringResource(R.string.files_subtitle))
        ConsolePanel(Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.files_privacy), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            val sendEnabled = ready && !busy
            Column(
                Modifier.fillMaxWidth().clip(ReyDeskShapes.Card).dashedBorder(if (sendEnabled) ReyDeskColors.LineStrong else ReyDeskColors.Line)
                    .clickable(enabled = sendEnabled, role = Role.Button) { senderSessionId = session.activeSessionOrNull()?.sessionIdentity().orEmpty(); openFile.launch(arrayOf("*/*")) }
                    .padding(20.dp),
                horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                RdIconTile("folder")
                Text(stringResource(R.string.files_choose_send), style = MaterialTheme.typography.titleMedium, color = if (sendEnabled) ReyDeskColors.Fg else ReyDeskColors.Disabled)
            }
            if (!ready) Text(stringResource(R.string.companion_connect_first), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
        }
        ConsolePanel(Modifier.fillMaxWidth()) {
            Text(stringResource(R.string.receive_folder_help), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            Text(selectedFolder?.let { folders.label(it) } ?: stringResource(R.string.receive_folder_unset), style = ReyDeskType.Mono, color = ReyDeskColors.Fg)
            if (folderError || selectedFolder?.let { !folders.available(it) } == true) Text(stringResource(R.string.receive_folder_unavailable), color = ReyDeskColors.Danger, style = MaterialTheme.typography.bodySmall)
            RdButton(stringResource(R.string.receive_folder_choose), { chooseFolder.launch(selectedFolder) }, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "folder", enabled = !busy)
            if (selectedFolder != null) RdButton(stringResource(R.string.receive_folder_forget), { folders.forget(); selectedFolder = null; folderError = false }, Modifier.fillMaxWidth(), kind = RdButtonKind.Ghost, enabled = !busy)
        }
        transfer.incoming?.let { offer ->
            ConsolePanel(Modifier.fillMaxWidth(), highlighted = true) {
                SectionLabel(stringResource(R.string.files_incoming), "transfers")
                Text(offer.fileName, style = MaterialTheme.typography.bodyLarge, color = ReyDeskColors.Fg)
                Text("${Digits.localize(offer.fileSizeBytes.toString(), lang)} ${stringResource(R.string.files_bytes)}", style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
                Text(stringResource(R.string.files_choose_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    RdButton(stringResource(R.string.files_accept), { pickerTransferId = offer.transferId; pickerSessionId = session.activeSessionOrNull()?.sessionIdentity().orEmpty(); saveFile.launch(offer.fileName) }, Modifier.weight(1f), enabled = pickerTransferId == null && ready)
                    RdButton(stringResource(R.string.files_decline), { session.chooseFileDestination(offer.transferId, null, session.activeSessionOrNull()?.sessionIdentity().orEmpty()) }, Modifier.weight(1f), kind = RdButtonKind.Secondary)
                }
            }
        }
        if (transfer.state != "idle" || transfer.detail.isNotEmpty()) ConsolePanel(Modifier.fillMaxWidth()) {
            Text(transfer.state.replace('_', ' '), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
            if (transfer.total > 0) {
                RdProgress((transfer.bytes.toDouble() / transfer.total).toFloat())
                Text("${Digits.localize(transfer.bytes.toString(), lang)} / ${Digits.localize(transfer.total.toString(), lang)} ${stringResource(R.string.files_bytes)}", style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            }
            if (transfer.detail.isNotEmpty()) Text(transfer.detail, style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
            if (busy) RdButton(stringResource(android.R.string.cancel), { session.cancelFileTransfer() }, Modifier.fillMaxWidth(), kind = RdButtonKind.Ghost)
        }
    }
}
