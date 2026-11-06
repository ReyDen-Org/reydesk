package ir.reyden.reydesk.ui.tutorial

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.layout.boundsInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.settings.TutorialPrefs
import ir.reyden.reydesk.ui.components.AppGlyph
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdIconButton
import ir.reyden.reydesk.ui.components.RdIconTile
import ir.reyden.reydesk.ui.components.RdMotion
import ir.reyden.reydesk.ui.components.RdSegmented
import ir.reyden.reydesk.ui.components.rdTween
import ir.reyden.reydesk.ui.components.rememberAnimationsEnabled
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/** A pending "Show me" request: the anchor [id] on the target screen draws a pulsing ring once, then the request clears. */
data class HighlightRequest(val id: String, val nonce: Long)

object TutorialHighlight {
    var request by mutableStateOf<HighlightRequest?>(null)
        private set

    fun show(id: String) { request = HighlightRequest(id, System.nanoTime()) }
    internal fun clear(done: HighlightRequest) { if (request == done) request = null }
}

/**
 * Marks a view as a tutorial "Show me" target. When the request for [id] arrives the view scrolls into view and a 2dp accent ring
 * pulses twice over 1.2s (held static for 2s under reduced motion).
 */
@OptIn(ExperimentalFoundationApi::class)
fun Modifier.tutorialTarget(id: String): Modifier = composed {
    val mine = TutorialHighlight.request?.takeIf { it.id == id }
    val alpha = remember { Animatable(0f) }
    val requester = remember { BringIntoViewRequester() }
    val animations = rememberAnimationsEnabled()
    LaunchedEffect(mine?.nonce) {
        if (mine == null) return@LaunchedEffect
        delay(250)
        requester.bringIntoView()
        if (animations) {
            repeat(2) { alpha.animateTo(1f, tween(300)); alpha.animateTo(.25f, tween(300)) }
            alpha.animateTo(0f, tween(RdMotion.Base))
        } else {
            alpha.snapTo(1f)
            delay(2000)
            alpha.snapTo(0f)
        }
        TutorialHighlight.clear(mine)
    }
    val focus = TourController.focus?.takeIf { it.id == id }
    LaunchedEffect(focus?.nonce) { if (focus != null) requester.bringIntoView() }
    DisposableEffect(id) { onDispose { TourController.anchors.remove(id) } }
    this.onGloballyPositioned { c ->
        val b = if (c.isAttached) c.boundsInWindow() else Rect.Zero
        if (b.isEmpty) TourController.anchors.remove(id) else TourController.anchors[id] = b
    }.bringIntoViewRequester(requester).drawWithContent {
        drawContent()
        val a = alpha.value
        if (a > 0f) {
            val w = 2.dp.toPx()
            drawRoundRect(
                ReyDeskColors.Accent.copy(alpha = a), topLeft = Offset(w / 2, w / 2), size = Size(size.width - w, size.height - w),
                cornerRadius = CornerRadius(14.dp.toPx()), style = Stroke(w),
            )
        }
    }
}

private val LatinRun = Regex("[A-Za-z][A-Za-z0-9\\-]*")

/** In fa and ar, wraps Latin runs (Wi-Fi, ReyDesk, QR) in LRI..PDI so they keep their order inside RTL text. */
private fun bidi(text: String, lang: String): String =
    if (lang == "fa") LatinRun.replace(text) { "⁦${it.value}⁩" } else text

@Composable
internal fun tr(id: Int, lang: String): String = bidi(stringResource(id), lang)

@Composable
fun TutorialScreen(initialPath: String?, onBack: () -> Unit, onShowMe: (TutShow) -> Unit, onStartTour: (String, TutMethod, TutMethod) -> Unit) {
    val context = LocalContext.current
    val prefs = remember { TutorialPrefs(context.applicationContext) }
    val scope = rememberCoroutineScope()
    val lang = context.resources.configuration.locales[0]?.language ?: "en"
    val animations = rememberAnimationsEnabled()
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val density = LocalDensity.current

    var pathKey by rememberSaveable { mutableStateOf(initialPath ?: TutPath.Wifi.key) }
    var restored by rememberSaveable { mutableStateOf(initialPath != null) }
    var wifiMethod by rememberSaveable { mutableStateOf(TutMethod.Qr.key) }
    var internetMethod by rememberSaveable { mutableStateOf(TutMethod.Qr.key) }
    var index by rememberSaveable { mutableIntStateOf(0) }
    var listOpen by rememberSaveable { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        if (!restored) { pathKey = prefs.path.first(); restored = true }
    }

    val path = TutPath.from(pathKey)
    val steps = tutorialSteps(path, TutMethod.from(wifiMethod), TutMethod.from(internetMethod))
    val current = index.coerceIn(0, steps.lastIndex)
    val step = steps[current]

    fun selectPath(p: TutPath) {
        pathKey = p.key; index = 0; listOpen = false
        if (p != TutPath.After) scope.launch { prefs.setPath(p.key) }
    }
    fun finish() { scope.launch { prefs.markSeen() }; onBack() }

    Column(Modifier.fillMaxSize().background(ReyDeskColors.Bg), horizontalAlignment = Alignment.CenterHorizontally) {
        Row(
            Modifier.widthIn(max = 560.dp).fillMaxWidth().heightIn(min = 56.dp).padding(horizontal = 8.dp),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Box(
                Modifier.size(48.dp).clip(ReyDeskShapes.Control).clickable(role = Role.Button, onClick = onBack)
                    .semantics { contentDescription = context.getString(R.string.tut_back) },
                contentAlignment = Alignment.Center,
            ) { AppGlyph("chevron", Modifier.size(22.dp).graphicsLayer { rotationZ = if (rtl) 0f else 180f }, ReyDeskColors.Muted) }
            Text(tr(R.string.tut_title, lang), style = MaterialTheme.typography.titleLarge, color = ReyDeskColors.Fg, maxLines = 1)
        }
        Column(
            Modifier.widthIn(max = 560.dp).fillMaxWidth().weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text(tr(R.string.tut_subtitle, lang), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
            RdButton(
                stringResource(R.string.tour_start),
                { onStartTour(pathKey, TutMethod.from(wifiMethod), TutMethod.from(internetMethod)) },
                Modifier.fillMaxWidth(), glyph = "arrow_link",
            )
            if (path == TutPath.After) {
                RdButton(tr(R.string.tut_pathWifi, lang), { selectPath(TutPath.Wifi) }, kind = RdButtonKind.Ghost, glyph = "wifi")
            } else {
                val labels = mapOf(
                    TutPath.Wifi to tr(R.string.tut_pathWifi, lang), TutPath.Internet to tr(R.string.tut_pathInternet, lang),
                    TutPath.Mobile to tr(R.string.tut_pathMobile, lang),
                )
                val glyphs = mapOf(TutPath.Wifi to "wifi", TutPath.Internet to "globe", TutPath.Mobile to "signal")
                RdSegmented(
                    options = listOf(TutPath.Wifi, TutPath.Internet, TutPath.Mobile), selected = path,
                    onSelect = { selectPath(it) }, optionLabel = { labels.getValue(it) }, optionGlyph = { glyphs[it] },
                    modifier = Modifier.fillMaxWidth(),
                )
                RdButton(tr(R.string.tut_alreadyPaired, lang), { selectPath(TutPath.After) }, kind = RdButtonKind.Ghost)
            }

            val stepLabel = stringResource(R.string.tut_stepOf, Digits.localize((current + 1).toString(), lang), Digits.localize(steps.size.toString(), lang))
            val announce = stringResource(R.string.tut_a11yStep, Digits.localize((current + 1).toString(), lang), Digits.localize(steps.size.toString(), lang), stringResource(step.titleRes))
            Text(
                stepLabel, style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted,
                modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite; contentDescription = announce },
            )
            val fraction by animateFloatAsState((current + 1f) / steps.size, rdTween(animations, RdMotion.Base), label = "tutProgress")
            Box(Modifier.fillMaxWidth().height(8.dp).clip(ReyDeskShapes.Pill).background(ReyDeskColors.Panel).border(1.dp, ReyDeskColors.Line, ReyDeskShapes.Pill)) {
                Box(Modifier.fillMaxWidth(fraction).fillMaxHeight().background(ReyDeskColors.Accent, ReyDeskShapes.Pill))
            }

            val shiftPx = with(density) { 8.dp.roundToPx() }
            AnimatedContent(
                targetState = step,
                transitionSpec = {
                    if (!animations) EnterTransition.None togetherWith ExitTransition.None else {
                        val forward = steps.indexOf(targetState) >= steps.indexOf(initialState)
                        val dir = (if (forward) 1 else -1) * (if (rtl) -1 else 1)
                        (fadeIn(tween(RdMotion.Base)) + slideInHorizontally(tween(RdMotion.Base)) { dir * shiftPx }) togetherWith fadeOut(tween(RdMotion.Base))
                    }
                },
                label = "tutStep",
            ) { s ->
                StepCard(
                    s, lang,
                    method = when (s.id) { "a3" -> TutMethod.from(wifiMethod); else -> TutMethod.from(internetMethod) },
                    onMethod = { m -> if (s.id == "a3") wifiMethod = m.key else internetMethod = m.key },
                    onShowMe = onShowMe, onPathLink = { selectPath(it) },
                )
            }

            Row(
                Modifier.fillMaxWidth().heightIn(min = 48.dp).clip(ReyDeskShapes.Control).clickable(role = Role.Button) { listOpen = !listOpen },
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(tr(R.string.tut_allSteps, lang), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg, modifier = Modifier.weight(1f))
                AppGlyph(if (listOpen) "down" else "chevron", Modifier.size(18.dp).graphicsLayer { if (!listOpen && rtl) rotationZ = 180f }, ReyDeskColors.Muted)
            }
            if (listOpen) steps.forEachIndexed { i, s ->
                StepRow(i, s, current, lang) { index = i }
            }
        }
        Row(
            Modifier.widthIn(max = 560.dp).fillMaxWidth().background(ReyDeskColors.Bg).padding(horizontal = 16.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            RdButton(tr(R.string.tut_back, lang), { index = (current - 1).coerceAtLeast(0) }, Modifier.weight(1f), kind = RdButtonKind.Secondary, enabled = current > 0)
            val last = current == steps.lastIndex
            RdButton(tr(if (last) R.string.tut_done else R.string.tut_next, lang), { if (last) finish() else index = current + 1 }, Modifier.weight(1f))
        }
    }
}

@Composable
private fun StepCard(
    step: TutStep, lang: String, method: TutMethod, onMethod: (TutMethod) -> Unit,
    onShowMe: (TutShow) -> Unit, onPathLink: (TutPath) -> Unit,
) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(tr(step.titleRes, lang), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, color = ReyDeskColors.Fg)
        step.fork?.let { methods ->
            val labels = mapOf(
                TutMethod.Qr to tr(R.string.tut_methodQr, lang), TutMethod.Nearby to tr(R.string.tut_methodNearby, lang),
                TutMethod.Code to tr(R.string.tut_methodCode, lang),
            )
            RdSegmented(methods, method, onMethod, { labels.getValue(it) }, Modifier.fillMaxWidth())
        }
        DeviceCard("smartphone", tr(R.string.tut_thisDevice, lang), tr(step.phoneRes, lang), true,
            step.show?.let { show -> tr(R.string.tut_showMe, lang) to { onShowMe(show) } })
        DeviceCard("monitor", tr(R.string.tut_otherDevice, lang), tr(step.pcRes, lang), false, null)
        step.pathLink?.let { link ->
            RdButton(tr(link.labelRes, lang), { onPathLink(link.path) }, kind = RdButtonKind.Ghost)
        }
    }
}

@Composable
private fun DeviceCard(glyph: String, header: String, body: String, thisDevice: Boolean, showMe: Pair<String, () -> Unit>?) {
    ConsolePanel(Modifier.fillMaxWidth(), highlighted = thisDevice) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.Top) {
            RdIconTile(glyph)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(header, style = MaterialTheme.typography.labelMedium, color = ReyDeskColors.Muted)
                Text(body, style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
            }
        }
        showMe?.let { (label, click) -> RdButton(label, click, kind = RdButtonKind.Tonal, glyph = "arrow_link") }
    }
}

@Composable
private fun StepRow(i: Int, step: TutStep, current: Int, lang: String, onClick: () -> Unit) {
    val done = i < current
    val active = i == current
    Row(
        Modifier.fillMaxWidth().heightIn(min = 48.dp).clip(ReyDeskShapes.Control).clickable(role = Role.Button, onClick = onClick),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        val bg = if (done || active) ReyDeskColors.Tint else ReyDeskColors.Panel
        val line = if (active) ReyDeskColors.Accent else if (done) ReyDeskColors.TintLine else ReyDeskColors.Line
        Box(Modifier.size(24.dp).clip(ReyDeskShapes.Pill).background(bg).border(1.dp, line, ReyDeskShapes.Pill), contentAlignment = Alignment.Center) {
            if (done) AppGlyph("check", Modifier.size(14.dp), ReyDeskColors.Accent)
            else Text(
                Digits.localize((i + 1).toString(), lang), style = MaterialTheme.typography.labelMedium, textAlign = TextAlign.Center,
                color = if (active) ReyDeskColors.Accent else ReyDeskColors.Muted,
            )
        }
        Text(
            tr(step.titleRes, lang), style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f),
            color = if (active) ReyDeskColors.Fg else ReyDeskColors.Muted, fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
        )
    }
}

/**
 * Dismissible first-run card for the top of the Connection screen. Shows only while the tutorial was never opened or dismissed and
 * no trusted computer exists. Never a dialog and never blocks the screen.
 */
@Composable
fun TutorialFirstRunCard(onOpen: () -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val prefs = remember { TutorialPrefs(context.applicationContext) }
    val session = (context.applicationContext as ReyDeskApplication).session
    val scope = rememberCoroutineScope()
    val lang = context.resources.configuration.locales[0]?.language ?: "en"
    // Defaults hide the card until both stores have answered, so users who already finished setup never see a flash.
    val seen by remember { prefs.seen }.collectAsState(initial = true)
    val noTrusted by remember { session.trustedDevices.devices.map { list -> list.none { !it.revoked } } }.collectAsState(initial = false)
    if (seen || !noTrusted) return
    ConsolePanel(modifier.fillMaxWidth(), highlighted = true) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.Top) {
            RdIconTile("help")
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(tr(R.string.tut_firstRunTitle, lang), style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
                Text(tr(R.string.tut_firstRunBody, lang), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            }
            RdIconButton("close", { scope.launch { prefs.markSeen() } }, stringResource(R.string.tut_dismiss))
        }
        RdButton(tr(R.string.tut_firstRunOpen, lang), { scope.launch { prefs.markSeen() }; onOpen() }, Modifier.fillMaxWidth())
    }
}
