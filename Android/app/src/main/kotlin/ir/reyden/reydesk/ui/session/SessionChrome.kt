package ir.reyden.reydesk.ui.session

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ui.components.AppGlyph
import ir.reyden.reydesk.ui.components.RdDialog
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskType
import ir.reyden.reydesk.protocol.v1.NegotiatedConfig
import ir.reyden.reydesk.protocol.v1.VideoCodec
import ir.reyden.reydesk.webrtc.ConnectionMetrics
import kotlinx.coroutines.delay
import java.util.Locale
import kotlin.math.roundToInt

private val ChromeText = ReyDeskColors.Fg
private val ChromeMuted = ReyDeskColors.Muted
private val ChromeAccent = ReyDeskColors.Accent
private const val Dash = "-"

/** The chrome floats above video; only its own hit targets consume remote gestures. */
@Composable
internal fun SessionChrome(
    hostName: String?, startedAt: Long?, metrics: ConnectionMetrics?, negotiated: NegotiatedConfig?, visible: Boolean, revealToken: Int,
    zoomPercent: Int, keyboardVisible: Boolean, onKeyboard: () -> Unit,
    onTouchHelp: () -> Unit, onResetView: () -> Unit, onZoom: () -> Unit, onDisconnect: () -> Unit,
    onHideAll: () -> Unit, keyboard: @Composable () -> Unit,
) {
    var expanded by remember { mutableStateOf(true) }
    var headerVisible by remember { mutableStateOf(true) }
    var showStats by remember { mutableStateOf(false) }
    var confirmHide by remember { mutableStateOf(false) }
    val statsScroll = rememberScrollState()
    var elapsed by remember { mutableLongStateOf(0L) }
    LaunchedEffect(startedAt) {
        while (true) { elapsed = startedAt?.let { ((System.currentTimeMillis() - it) / 1000).coerceAtLeast(0) } ?: 0; delay(1000) }
    }
    LaunchedEffect(visible, revealToken) { if (visible) { headerVisible = true; expanded = true } }
    val lang = LocalContext.current.resources.configuration.locales[0]?.language ?: "en"
    val elapsedText = Digits.localize("%02d:%02d:%02d".format(Locale.ROOT, elapsed / 3600, elapsed / 60 % 60, elapsed % 60), lang)
    if (confirmHide) RdDialog(
        onDismiss = { confirmHide = false },
        title = stringResource(R.string.session_hide_all),
        confirmText = stringResource(R.string.session_hide_all),
        onConfirm = { confirmHide = false; onHideAll() },
        dismissText = stringResource(android.R.string.cancel),
    ) { Text(stringResource(R.string.session_reveal_hint), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted) }
    if (showStats) RdDialog(
        onDismiss = { showStats = false },
        title = stringResource(R.string.session_connection_details),
        confirmText = stringResource(android.R.string.ok),
        onConfirm = { showStats = false },
    ) {
        Column(Modifier.fillMaxWidth().heightIn(max = 260.dp).verticalScroll(statsScroll), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(stringResource(R.string.session_connected_host, hostName ?: Dash), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
            Text(stringResource(R.string.session_duration_value, elapsedText), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
            MetricLine(stringResource(R.string.session_metric_resolution), if (metrics != null && metrics.width > 0) num("${metrics.width} x ${metrics.height}", lang) else Dash)
            MetricLine(stringResource(R.string.session_metric_negotiated), negotiated?.takeIf { it.widthPx > 0 && it.heightPx > 0 }?.let { num("${it.widthPx} x ${it.heightPx}", lang) } ?: Dash)
            MetricLine(stringResource(R.string.session_metric_fps_limit), negotiated?.fps?.takeIf { it > 0 }?.let { num("$it fps", lang) } ?: Dash)
            MetricLine(stringResource(R.string.session_metric_target_rate), negotiated?.targetBitrateKbps?.takeIf { it > 0 }?.let { num(String.format(Locale.ROOT, "%.1f Mbps", it / 1000.0), lang) } ?: Dash)
            MetricLine(stringResource(R.string.session_metric_codec), when (negotiated?.videoCodec) { VideoCodec.VIDEO_CODEC_H264 -> "H.264"; VideoCodec.VIDEO_CODEC_HEVC -> "HEVC"; else -> Dash })
            MetricLine(stringResource(R.string.session_metric_fps), metric(metrics?.fps, "fps", lang))
            MetricLine(stringResource(R.string.session_metric_speed), metrics?.bitrateKbps?.let { num(String.format(Locale.ROOT, "%.1f Mbps", it / 1000), lang) } ?: Dash)
            MetricLine(stringResource(R.string.session_metric_ping), metric(metrics?.roundTripTimeMs, "ms", lang))
            MetricLine(stringResource(R.string.session_metric_loss), metric(metrics?.packetLossPercent, "%", lang))
            MetricLine(stringResource(R.string.session_metric_jitter), metric(metrics?.jitterMs, "ms", lang))
            Text(stringResource(R.string.session_metrics_note), style = MaterialTheme.typography.labelSmall, color = ReyDeskColors.Faint)
        }
    }
    if (!visible) return
    // Edge-to-edge keeps the video full-screen. Consume IME once for chrome only;
    // systemBars/displayCutout exclude IME, avoiding duplicate subtraction.
    BoxWithConstraints(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.systemBars.union(WindowInsets.displayCutout)).imePadding()) {
        if (headerVisible && !keyboardVisible) FloatingChrome(top = true) { drag ->
            Row(Modifier.chromeSurface().then(drag).padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                DragGrip(Modifier)
                AppGlyph("connection", Modifier.size(16.dp), ChromeText)
                Box(Modifier.padding(horizontal = 6.dp).size(6.dp).background(ReyDeskColors.Success,RoundedCornerShape(3.dp)))
                Text(stringResource(R.string.session_connected_host, hostName ?: Dash), color = ChromeText, style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.widthIn(max = 230.dp).clickable { showStats = true }.padding(vertical = 12.dp), maxLines = 1)
                Text(elapsedText, color = ChromeText, style = ReyDeskType.Mono, modifier = Modifier.padding(horizontal = 12.dp))
                SmallChromeButton("stats", stringResource(R.string.session_connection_details)) { showStats = true }
                SmallChromeButton("close", stringResource(R.string.session_hide_header)) { headerVisible = false }
            }
        }
        FloatingChrome(top = false) { drag ->
            if (!expanded) {
                val label = stringResource(R.string.session_show_controls)
                Box(Modifier.chromeSurface().then(drag).size(48.dp).clickable { expanded = true }
                    .semantics { contentDescription = label }, contentAlignment = Alignment.Center) {
                    AppGlyph("drag", Modifier.size(18.dp), ChromeAccent)
                }
            } else Column(Modifier.widthIn(max = 620.dp).chromeSurface().then(drag).padding(8.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                Box(Modifier.fillMaxWidth().height(12.dp), contentAlignment = Alignment.Center) {
                    Box(Modifier.size(28.dp,3.dp).background(ChromeMuted.copy(alpha=.55f),RoundedCornerShape(2.dp)))
                }
                if (keyboardVisible) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.weight(1f)) { keyboard() }
                        SmallChromeButton("keyboard", stringResource(R.string.session_keyboard), onKeyboard)
                    }
                } else Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    ChromeTool("touch",stringResource(R.string.ui_touch),ChromeAccent,onTouchHelp)
                    ChromeTool("keyboard",stringResource(R.string.session_keyboard),if(keyboardVisible) ChromeAccent else ChromeText,onKeyboard)
                    ChromeTool("zoom","$zoomPercent%",ChromeText,onZoom)
                    ChromeTool("reset",stringResource(R.string.ui_reset_view),ChromeText,onResetView)
                    ChromeTool("stats",stringResource(R.string.session_connection_details),ChromeText) { showStats=true }
                    ChromeTool("disconnect",stringResource(R.string.session_disconnect),ReyDeskColors.Danger,onDisconnect)
                    Row {
                        SmallChromeButton("down",stringResource(R.string.session_collapse_controls)) { if(keyboardVisible) onKeyboard(); expanded=false }
                        SmallChromeButton("eye",stringResource(R.string.session_hide_all)) { confirmHide=true }
                    }
                }
            }
        }
    }
}

@Composable private fun MetricLine(label:String,value:String) {
    Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(12.dp)) {
        Text(label,Modifier.weight(1f),style=MaterialTheme.typography.bodyMedium,color=ReyDeskColors.Muted)
        Text(value,style=ReyDeskType.Mono,color=ReyDeskColors.Fg)
    }
}
private fun num(text:String,lang:String):String = Digits.localize(text,lang)
private fun metric(value:Double?,unit:String,lang:String):String = value?.let { num(String.format(Locale.ROOT,"%.1f %s",it,unit),lang) } ?: Dash
private fun Modifier.chromeSurface():Modifier = clip(ReyDeskShapes.Card).background(ReyDeskColors.Surface.copy(alpha=.92f))
    .border(1.dp,ReyDeskColors.LineStrong,ReyDeskShapes.Card)

/** Position is clamped after every size/IME change, so a dragged handle cannot get lost. */
@Composable private fun BoxWithConstraintsScope.FloatingChrome(top:Boolean,content:@Composable (Modifier)->Unit) {
    var measured by remember { mutableStateOf(IntSize.Zero) }
    var position by remember { mutableStateOf<Offset?>(null) }
    val maxX=(constraints.maxWidth-measured.width).coerceAtLeast(0).toFloat()
    val maxY=(constraints.maxHeight-measured.height).coerceAtLeast(0).toFloat()
    val shown = (position ?: Offset(maxX/2,if(top) 0f else maxY)).let { Offset(it.x.coerceIn(0f,maxX),it.y.coerceIn(0f,maxY)) }
    val latestShown by rememberUpdatedState(shown)
    val drag = Modifier.pointerInput(maxX,maxY) {
        detectDragGestures { change,amount ->
            change.consume()
            val current = position ?: latestShown
            position = Offset((current.x+amount.x).coerceIn(0f,maxX),(current.y+amount.y).coerceIn(0f,maxY))
        }
    }
    Box(Modifier.offset { IntOffset(shown.x.roundToInt(),shown.y.roundToInt()) }.onSizeChanged { measured=it }) { content(drag) }
}
@Composable private fun DragGrip(modifier:Modifier) {
    val label=stringResource(R.string.session_drag_controls)
    Box(modifier.size(32.dp,44.dp).semantics { contentDescription=label },contentAlignment=Alignment.Center) { AppGlyph("drag",Modifier.size(16.dp),ChromeMuted) }
}
@Composable private fun SmallChromeButton(kind:String,label:String,onClick:()->Unit) {
    Box(Modifier.size(48.dp).clip(ReyDeskShapes.Control).clickable(onClick=onClick).semantics { contentDescription=label },contentAlignment=Alignment.Center) { AppGlyph(kind,Modifier.size(24.dp),ChromeMuted) }
}
@Composable private fun ChromeTool(kind:String,label:String,tint:Color,onClick:()->Unit) {
    Column(Modifier.width(80.dp).heightIn(min=72.dp).clip(RoundedCornerShape(12.dp))
        .background(if(kind=="touch") ReyDeskColors.Tint else ReyDeskColors.Button)
        .border(1.dp,if(kind=="touch") ReyDeskColors.TintLine else ReyDeskColors.Line,RoundedCornerShape(12.dp))
        .clickable(onClick=onClick).padding(8.dp),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(4.dp,androidx.compose.ui.Alignment.CenterVertically)) {
        AppGlyph(kind,Modifier.size(24.dp),tint)
        Text(label,color=tint,style=MaterialTheme.typography.labelSmall,maxLines=2,textAlign=androidx.compose.ui.text.style.TextAlign.Center)
    }
}
