package ir.reyden.reydesk.ui.settings

import android.app.Activity
import android.content.ContextWrapper
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.wrapContentHeight
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.BuildConfig
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.settings.DEFAULT_RENDEZVOUS_URL
import ir.reyden.reydesk.settings.RemoteSettingsRepository
import ir.reyden.reydesk.ui.connection.RemoteServerState
import ir.reyden.reydesk.settings.AppSettingsRepository
import ir.reyden.reydesk.settings.StreamPreferences
import ir.reyden.reydesk.ui.companion.requestNotificationAccess
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdSelect
import ir.reyden.reydesk.ui.components.ScreenHeading
import ir.reyden.reydesk.ui.components.SectionLabel
import ir.reyden.reydesk.ui.tutorial.tutorialTarget
import ir.reyden.reydesk.ui.connection.RemoteUiState
import ir.reyden.reydesk.ui.connection.remoteStateColor
import ir.reyden.reydesk.ui.connection.remoteStateText
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskType
import kotlinx.coroutines.launch

private val SupportedLanguages = listOf(
    // Each language is named in its own script so a user in the wrong language can still find theirs.
    "en" to "English", "fa" to "فارسی",
)

private val QualityPresets = listOf(R.string.quality_auto, R.string.quality_performance, R.string.quality_balanced, R.string.quality_quality, R.string.quality_custom)

/**
 * [remote] and the two toggle callbacks are supplied by the identity v2 code; until then the internet toggles are shown disabled
 * and the state reads "off".
 */
@Composable
fun SettingsScreen(
    settings: AppSettingsRepository,
    onPhoneHub: () -> Unit = {},
    onFiles: () -> Unit = {},
    remote: RemoteUiState? = null,
    onRemoteToggle: ((Boolean) -> Unit)? = null,
    onRelayToggle: ((Boolean) -> Unit)? = null,
    onTutorial: () -> Unit = {},
) {
    val context = LocalContext.current
    val coroutineScope = rememberCoroutineScope()
    val streaming by settings.streaming.collectAsState(initial = StreamPreferences())
    val currentLocaleTag = context.resources.configuration.locales[0]?.language ?: "en"
    val currentLanguage = SupportedLanguages.firstOrNull { it.first == currentLocaleTag } ?: SupportedLanguages.first()
    val uriHandler = LocalUriHandler.current
    val rc = (context.applicationContext as ReyDeskApplication).session.remote
    val liveRemote by rc.uiState.collectAsState()
    val remoteUi = remote ?: liveRemote
    val serverState = remoteUi.serverState
    val remoteToggle: (Boolean) -> Unit = onRemoteToggle ?: rc::setRemoteAllowed
    val relayToggle: (Boolean) -> Unit = onRelayToggle ?: rc::setRelayAllowed
    val remoteRepo = remember { RemoteSettingsRepository(context.applicationContext) }
    var savedUrl by remember { mutableStateOf("") }
    var urlInput by rememberSaveable { mutableStateOf("") }
    var advancedOpen by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        savedUrl = remoteRepo.load().rendezvousUrl
        if (urlInput.isEmpty()) urlInput = savedUrl
    }
    val urlTrim = urlInput.trim()
    val urlCanonical = if (urlTrim.isEmpty()) "" else ir.reyden.reydesk.rendezvous.RendezvousUrl.normalize(urlTrim, BuildConfig.DEBUG)
    val urlValid = urlCanonical != null
    val effectiveHost = remember(savedUrl) {
        runCatching { java.net.URI(savedUrl.ifEmpty { DEFAULT_RENDEZVOUS_URL }).host }.getOrNull().orEmpty()
    }

    Box(Modifier.fillMaxSize().background(ReyDeskColors.Bg), contentAlignment = Alignment.TopCenter) {
        Column(
            Modifier.widthIn(max = 560.dp).fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            ScreenHeading(stringResource(R.string.nav_settings), stringResource(R.string.ui_settings_subtitle))
            ConsolePanel(Modifier.fillMaxWidth()) {
                SectionLabel(stringResource(R.string.tut_settingsTitle), "help")
                Text(stringResource(R.string.tut_settingsBody), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                RdButton(stringResource(R.string.tut_settingsOpen), onTutorial, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "help")
            }
            ConsolePanel(Modifier.fillMaxWidth()) {
                SectionLabel(stringResource(R.string.phone_hub_title), "phone_hub")
                Text(stringResource(R.string.phone_hub_subtitle), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                RdButton(stringResource(R.string.phone_hub_grant), { requestNotificationAccess(context) }, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "bell")
                RdButton(stringResource(R.string.phone_hub_manage), onPhoneHub, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "phone_hub")
                RdButton(stringResource(R.string.companion_files), onFiles, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "transfers")
            }
            ConsolePanel(Modifier.fillMaxWidth()) {
                SectionLabel(stringResource(R.string.settings_language), "globe")
                RdSelect(
                    options = SupportedLanguages, selected = currentLanguage,
                    optionLabel = { it.second }, modifier = Modifier.fillMaxWidth(),
                    onSelect = { choice ->
                        context.getSharedPreferences("reydesk_locale", android.content.Context.MODE_PRIVATE)
                            .edit().putString("language", choice.first).apply()
                        context.findActivity()?.recreate()
                    },
                )
            }
            ConsolePanel(Modifier.fillMaxWidth().tutorialTarget("settings_internet")) {
                SectionLabel(stringResource(R.string.settings_internet_title), "server")
                SwitchRow(
                    stringResource(R.string.settings_internet_enable), remoteUi.remoteAllowed, remoteToggle,
                )
                Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.settings_internet_server), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
                    Box(Modifier.size(8.dp).background(remoteStateColor(serverState), ReyDeskShapes.Pill))
                    Text(
                        if (remoteUi.remoteAllowed && serverState == RemoteServerState.Offline) stringResource(R.string.internet_state_unreachable) else remoteStateText(serverState),
                        style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
                }
                remoteUi.deskId?.let { InfoRow(stringResource(R.string.internet_desk_id_label), Digits.ltr(it.filter { c -> c.isLetterOrDigit() }.chunked(4).joinToString("-")), ReyDeskType.Mono) }
                remoteUi.keyId?.let { InfoRow(stringResource(R.string.settings_key_id), Digits.ltr(it.filter { c -> c.isLetterOrDigit() }.chunked(4).joinToString("-")), ReyDeskType.Mono) }
                Box(Modifier.tutorialTarget("settings_relay")) {
                    SwitchRow(
                        stringResource(R.string.settings_relay), remoteUi.relayAllowed, relayToggle,
                        helper = stringResource(R.string.settings_relay_hint),
                    )
                }
                Text(stringResource(R.string.settings_internet_privacy_host, effectiveHost), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                RdButton(stringResource(R.string.settings_advanced), { advancedOpen = !advancedOpen }, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary)
                if (advancedOpen) {
                    InfoRow(stringResource(R.string.settings_rendezvous_effective), Digits.ltr(effectiveHost), ReyDeskType.Mono)
                    UrlField(
                        stringResource(R.string.settings_rendezvous_label), urlInput, { urlInput = it },
                        error = if (urlValid) null else stringResource(R.string.settings_rendezvous_invalid),
                    )
                    Text(stringResource(R.string.settings_rendezvous_hint_default), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                    if (urlCanonical != savedUrl) RdButton(
                        stringResource(R.string.settings_rendezvous_save),
                        { rc.setRendezvousUrl(urlCanonical.orEmpty()); savedUrl = urlCanonical.orEmpty(); urlInput = savedUrl },
                        Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, enabled = urlValid,
                    )
                    if (savedUrl.isNotEmpty()) RdButton(
                        stringResource(R.string.settings_rendezvous_reset),
                        { rc.setRendezvousUrl(""); savedUrl = ""; urlInput = "" },
                        Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary,
                    )
                }
            }
            ConsolePanel(Modifier.fillMaxWidth()) {
                SectionLabel(stringResource(R.string.ui_input_controls), "touch")
                Text(stringResource(R.string.session_gesture_hint), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
                Text(stringResource(R.string.settings_control_help), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            }
            ConsolePanel(Modifier.fillMaxWidth()) {
                SectionLabel(stringResource(R.string.ui_connection_streaming), "connection")
                val presetIndex = StreamPreferences.presets.indexOf(streaming.preset).let { if (it < 0) 4 else it }
                SelectRow(stringResource(R.string.quality_preset), QualityPresets.indices.toList(), presetIndex, { context.getString(QualityPresets[it]) }) { index ->
                    coroutineScope.launch {
                        settings.setStreaming(if (StreamPreferences.presets[index] == "Custom") streaming.copy(preset = "Custom") else StreamPreferences.forPreset(StreamPreferences.presets[index]))
                    }
                }
                Text(stringResource(R.string.quality_auto_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                val defaultLabel = stringResource(R.string.settings_host_default)
                val resolutions = listOf(0, 540, 720, 1080, 1440, 2160)
                SelectRow(stringResource(R.string.settings_resolution), resolutions, streaming.maxHeight.takeIf { it in resolutions } ?: 0, { if (it == 0) defaultLabel else "${it}p" }) {
                    coroutineScope.launch { settings.setStreaming(streaming.copy(maxHeight = it, preset = "Custom")) }
                }
                val frameRates = listOf(0, 15, 30, 60)
                SelectRow(stringResource(R.string.settings_frame_rate), frameRates, streaming.maxFps.takeIf { it in frameRates } ?: 0, { if (it == 0) defaultLabel else "$it fps" }) {
                    coroutineScope.launch { settings.setStreaming(streaming.copy(maxFps = it, preset = "Custom")) }
                }
                val bitrates = listOf(0, 4000, 8000, 12000, 20000, 30000, 50000)
                SelectRow(stringResource(R.string.settings_bitrate), bitrates, streaming.bitrateKbps.takeIf { it in bitrates } ?: 0, { if (it == 0) defaultLabel else "${it / 1000} Mbps" }) {
                    coroutineScope.launch { settings.setStreaming(streaming.copy(bitrateKbps = it, preset = "Custom")) }
                }
                Text(stringResource(R.string.settings_next_connection), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            }
            ConsolePanel(Modifier.fillMaxWidth()) {
                SectionLabel(stringResource(R.string.ui_about), "help")
                InfoRow(stringResource(R.string.settings_version), Digits.ltr(BuildConfig.APP_VERSION + if (BuildConfig.DEBUG) " (debug)" else ""), ReyDeskType.Mono)
                InfoRow(stringResource(R.string.ui_build), Digits.ltr("${BuildConfig.VERSION_CODE} / ${BuildConfig.BUILD_TYPE}"), ReyDeskType.Mono)
                Text(
                    "reyden.ir/reydesk", style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Accent,
                    modifier = Modifier.heightIn(min = 48.dp).clickable { uriHandler.openUri("https://reyden.ir/reydesk") }.wrapContentHeight(Alignment.CenterVertically),
                )
                Text(stringResource(R.string.settings_licences_title), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                Text(stringResource(R.string.settings_licences_body), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                var showLicences by rememberSaveable { mutableStateOf(false) }
                RdButton(
                    stringResource(if (showLicences) R.string.settings_licences_hide else R.string.settings_licences_show),
                    { showLicences = !showLicences }, kind = RdButtonKind.Secondary,
                )
                if (showLicences) {
                    val licenceContext = LocalContext.current
                    val licenceText = remember {
                        listOf("ReyDesk-MIT.txt", "THIRD_PARTY_NOTICES.md", "OFL-Inter.txt", "OFL-JetBrainsMono.txt", "OFL-Vazirmatn.txt").joinToString("\n\n----\n\n") { name ->
                            runCatching { licenceContext.assets.open("licenses/$name").bufferedReader().use { it.readText() } }.getOrDefault(name)
                        }
                    }
                    Text(Digits.ltr(licenceText), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
                }
                Row(Modifier.fillMaxWidth().heightIn(min = 48.dp), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.settings_credit_prefix), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
                    Text(
                        "ReyDen", style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Accent,
                        modifier = Modifier.heightIn(min = 48.dp).clickable { uriHandler.openUri("https://reyden.ir") }.padding(horizontal = 4.dp).wrapContentHeight(Alignment.CenterVertically),
                    )
                }
            }
            Text(
                stringResource(R.string.settings_copyright, java.time.Year.now().value),
                color = ReyDeskColors.Muted, style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp).clickable { uriHandler.openUri("https://reyden.ir") }.padding(horizontal = 24.dp, vertical = 16.dp),
                textAlign = TextAlign.Center,
            )
        }
    }
}

/** Also reused by SessionScreen for its existing orientation handling. */
internal tailrec fun android.content.Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

@Composable
private fun <T> SelectRow(label: String, options: List<T>, selectedIndex: Int, optionLabel: (T) -> String, onSelected: (T) -> Unit) {
    RdSelect(
        options = options, selected = options.getOrElse(selectedIndex) { options.first() }, onSelect = onSelected,
        optionLabel = optionLabel, label = label, modifier = Modifier.fillMaxWidth(),
    )
}
