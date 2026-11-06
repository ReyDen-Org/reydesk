package ir.reyden.reydesk.ui.tutorial

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.VectorConverter
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.paneTitle
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.R
import ir.reyden.reydesk.settings.TutorialPrefs
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdIconButton
import ir.reyden.reydesk.ui.components.RdMotion
import ir.reyden.reydesk.ui.components.RdSegmented
import ir.reyden.reydesk.ui.components.rememberAnimationsEnabled
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** State of the spotlight tour. Lives while the app runs; starting always resets to step 1. */
object TourController {
    var active by mutableStateOf(false)
        private set
    var pathKey by mutableStateOf(TutPath.Wifi.key)
        private set
    var wifiMethod by mutableStateOf(TutMethod.Qr)
    var internetMethod by mutableStateOf(TutMethod.Qr)
    var index by mutableIntStateOf(0)

    /** Window bounds of the visible anchors, fed by [tutorialTarget]. */
    internal val anchors = mutableStateMapOf<String, Rect>()

    /** Asks the anchor [id] to scroll itself into view. */
    internal var focus by mutableStateOf<HighlightRequest?>(null)
        private set

    val steps: List<TutStep> get() = tutorialSteps(TutPath.from(pathKey), wifiMethod, internetMethod)

    fun start(path: String? = null, wifi: TutMethod = TutMethod.Qr, internet: TutMethod = TutMethod.Qr) {
        pathKey = TutPath.from(path).key
        wifiMethod = wifi; internetMethod = internet
        index = 0
        active = true
    }

    fun switchPath(path: TutPath) { pathKey = path.key; index = 0 }
    fun stop() { active = false; focus = null }
    internal fun focusOn(id: String) { focus = HighlightRequest(id, System.nanoTime()) }
}

private val Pad = 6.dp
private val Margin = 12.dp
private val Gap = 10.dp

/**
 * The tour overlay: dims the window, cuts a clickable hole around the step's anchor, rings it and shows the step card.
 * [currentRoute] and [onNavigate] let steps with a deep link open the right screen first.
 */
@Composable
fun GuidedTourOverlay(currentRoute: String?, onNavigate: (String) -> Unit) {
    if (!TourController.active) return
    val context = LocalContext.current
    val lang = context.resources.configuration.locales[0]?.language ?: "en"
    val animations = rememberAnimationsEnabled()
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()
    val prefs = remember { TutorialPrefs(context.applicationContext) }

    val steps = TourController.steps
    val current = TourController.index.coerceIn(0, steps.lastIndex)
    val step = steps[current]
    val last = current == steps.lastIndex

    fun close() { scope.launch { prefs.markSeen() }; TourController.stop() }
    BackHandler(enabled = true) { close() }

    // Navigate and wait up to 2.5 s for the anchor; the card stays centred until the bounds show up.
    LaunchedEffect(step.id) {
        val show = step.show ?: return@LaunchedEffect
        if (currentRoute != show.route) onNavigate(show.route)
        val target = show.target ?: return@LaunchedEffect
        var waited = 0
        while (waited < 2500 && TourController.anchors[target] == null) {
            TourController.focusOn(target)
            delay(100); waited += 100
        }
        TourController.focusOn(target)
    }

    val anchor = step.show?.target?.let { TourController.anchors[it] }
    var origin by remember { mutableStateOf(Offset.Zero) }
    var container by remember { mutableStateOf(androidx.compose.ui.unit.IntSize.Zero) }
    var cardSize by remember { mutableStateOf(androidx.compose.ui.unit.IntSize.Zero) }

    val pad = with(density) { Pad.toPx() }
    val cutTarget = anchor?.let { a ->
        Rect(a.left - origin.x - pad, a.top - origin.y - pad, a.right - origin.x + pad, a.bottom - origin.y + pad)
    }
    val cut = remember { Animatable(Rect.Zero, Rect.VectorConverter) }
    var hasCut by remember { mutableStateOf(false) }
    LaunchedEffect(cutTarget) {
        if (cutTarget == null) { hasCut = false; return@LaunchedEffect }
        if (!hasCut || !animations) { cut.snapTo(cutTarget); hasCut = true }
        else cut.animateTo(cutTarget, tween(RdMotion.Base))
    }
    val cutRect = if (cutTarget != null && hasCut) cut.value else null

    val pulse = if (animations) {
        rememberInfiniteTransition(label = "tourPulse").animateFloat(
            .5f, 1f, infiniteRepeatable(tween(600, easing = LinearEasing), RepeatMode.Reverse), label = "ringAlpha",
        ).value
    } else 1f

    val ringPx = with(density) { 2.dp.toPx() }
    val radius = with(density) { 14.dp.toPx() }

    Box(
        Modifier.fillMaxSize()
            .onGloballyPositioned { origin = it.positionInWindow() }
            .onSizeChanged { container = it },
    ) {
        Canvas(Modifier.fillMaxSize().graphicsLayer { compositingStrategy = CompositingStrategy.Offscreen }) {
            drawRect(Color.Black.copy(alpha = .72f))
            cutRect?.let { r ->
                drawRoundRect(Color.Black, r.topLeft, r.size, CornerRadius(radius), blendMode = BlendMode.Clear)
                drawRoundRect(
                    ReyDeskColors.Accent.copy(alpha = pulse), Offset(r.left - ringPx / 2, r.top - ringPx / 2),
                    Size(r.width + ringPx, r.height + ringPx), CornerRadius(radius + ringPx / 2), style = androidx.compose.ui.graphics.drawscope.Stroke(ringPx),
                )
            }
        }
        // Blockers: four panes around the hole (or one full pane) swallow taps; the hole has no input node so the app below gets them.
        val blocker = Modifier.pointerInput(Unit) { detectTapGestures { } }
        if (cutRect == null) Box(Modifier.fillMaxSize().then(blocker)) else {
            val r = cutRect
            fun pane(l: Float, t: Float, w: Float, h: Float) = Modifier
                .offset { IntOffset(l.toInt(), t.toInt()) }
                .size(with(density) { w.coerceAtLeast(0f).toDp() }, with(density) { h.coerceAtLeast(0f).toDp() })
                .then(blocker)
            val cw = container.width.toFloat(); val ch = container.height.toFloat()
            Box(pane(0f, 0f, cw, r.top))
            Box(pane(0f, r.bottom, cw, ch - r.bottom))
            Box(pane(0f, r.top, r.left, r.height))
            Box(pane(r.right, r.top, cw - r.right, r.height))
        }

        val cardWidthDp = minOf(340f, with(density) { container.width.toDp().value } - 24f).coerceAtLeast(120f).dp
        val marginPx = with(density) { Margin.toPx() }
        val gapPx = with(density) { Gap.toPx() }
        val topInset = WindowInsets.systemBars.getTop(density).toFloat()
        val bottomInset = WindowInsets.systemBars.getBottom(density).toFloat()
        val cardAlpha = remember { Animatable(0f) }
        val cardShift = remember { Animatable(1f) }
        LaunchedEffect(step.id) {
            if (!animations) { cardAlpha.snapTo(1f); cardShift.snapTo(0f); return@LaunchedEffect }
            cardAlpha.snapTo(0f); cardShift.snapTo(1f)
            launch { cardAlpha.animateTo(1f, tween(160)) }
            cardShift.animateTo(0f, tween(160))
        }
        val cardMaxH = with(density) { (container.height - topInset - bottomInset - 2 * marginPx).coerceAtLeast(200f).toDp() }
        val nextFocus = remember { FocusRequester() }
        LaunchedEffect(step.id) {
            delay(60)
            try { nextFocus.requestFocus() } catch (_: IllegalStateException) { /* Next not attached yet; focus stays where it was. */ }
        }

        val stepNo = Digits.localize((current + 1).toString(), lang)
        val total = Digits.localize(steps.size.toString(), lang)
        val announce = stringResource(R.string.tut_a11yStep, stepNo, total, stringResource(step.titleRes))
        Column(
            Modifier
                .width(cardWidthDp)
                .offset {
                    val w = cardSize.width.toFloat(); val h = cardSize.height.toFloat()
                    val cw = container.width.toFloat(); val chh = container.height.toFloat()
                    val minY = topInset + marginPx; val maxY = chh - bottomInset - marginPx - h
                    val r = cutRect
                    var x = (cw - w) / 2f
                    var y = (chh - h) / 2f
                    if (r != null) {
                        x = (r.center.x - w / 2f)
                        val below = r.bottom + gapPx
                        val above = r.top - gapPx - h
                        y = when {
                            below <= maxY -> below
                            above >= minY -> above
                            else -> (chh - h) / 2f
                        }
                    }
                    x = x.coerceIn(marginPx, (cw - w - marginPx).coerceAtLeast(marginPx))
                    y = y.coerceIn(minY, maxY.coerceAtLeast(minY))
                    IntOffset(x.toInt(), y.toInt() + (cardShift.value * 8 * density.density).toInt())
                }
                .graphicsLayer { alpha = if (cardSize.width == 0) 0f else cardAlpha.value }
                .onSizeChanged { cardSize = it }
                .semantics { paneTitle = announce; contentDescription = announce }
                .clip(ReyDeskShapes.Card)
                .background(ReyDeskColors.PanelTop)
                .border(1.dp, ReyDeskColors.LineStrong, ReyDeskShapes.Card)
                .pointerInput(Unit) { detectTapGestures { } }
                .heightIn(max = cardMaxH)
                .verticalScroll(rememberScrollState())
                .padding(14.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    stringResource(R.string.tut_stepOf, stepNo, total), style = MaterialTheme.typography.labelMedium,
                    color = ReyDeskColors.Muted, modifier = Modifier.weight(1f).semantics { liveRegion = LiveRegionMode.Polite },
                )
                RdIconButton("close", { close() }, stringResource(R.string.tour_skip))
            }
            Text(tr(step.titleRes, lang), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, color = ReyDeskColors.Fg)
            step.fork?.let { methods ->
                val isWifi = step.id == "a3"
                val labels = mapOf(
                    TutMethod.Qr to tr(R.string.tut_methodQr, lang), TutMethod.Nearby to tr(R.string.tut_methodNearby, lang),
                    TutMethod.Code to tr(R.string.tut_methodCode, lang),
                )
                RdSegmented(
                    methods, if (isWifi) TourController.wifiMethod else TourController.internetMethod,
                    { m -> if (isWifi) TourController.wifiMethod = m else TourController.internetMethod = m },
                    { labels.getValue(it) }, Modifier.fillMaxWidth(),
                )
            }
            TourText(tr(R.string.tut_thisDevice, lang), tr(step.phoneRes, lang), true)
            TourText(tr(R.string.tut_otherDevice, lang), tr(step.pcRes, lang), false)
            step.pathLink?.let { link ->
                RdButton(tr(link.labelRes, lang), { TourController.switchPath(link.path) }, kind = RdButtonKind.Ghost)
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                RdButton(tr(R.string.tut_back, lang), { TourController.index = (current - 1).coerceAtLeast(0) }, Modifier.weight(1f), kind = RdButtonKind.Secondary, enabled = current > 0)
                RdButton(
                    tr(if (last) R.string.tut_done else R.string.tut_next, lang),
                    { if (last) close() else TourController.index = current + 1 },
                    Modifier.weight(1f).focusRequester(nextFocus),
                )
            }
        }
    }
}

@Composable
private fun TourText(header: String, body: String, thisDevice: Boolean) {
    Column(
        Modifier.fillMaxWidth().clip(ReyDeskShapes.Control)
            .background(if (thisDevice) ReyDeskColors.Tint else ReyDeskColors.Bg)
            .border(1.dp, if (thisDevice) ReyDeskColors.TintLine else ReyDeskColors.Line, ReyDeskShapes.Control)
            .padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Text(header, style = MaterialTheme.typography.labelMedium, color = if (thisDevice) ReyDeskColors.Accent else ReyDeskColors.Muted)
        Text(body, style = MaterialTheme.typography.bodySmall, color = if (thisDevice) ReyDeskColors.Fg else ReyDeskColors.Muted)
    }
}
