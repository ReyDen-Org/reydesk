package ir.reyden.reydesk.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

enum class RingState { Idle, Connecting, ConnectedLocal, ConnectedRemote, Error }

/**
 * Status ring hero (contract 2.3). Box 232 (compact 200), disc 176 (compact 152), edge ring, 44dp center glyph, then the state line
 * (dot + 15 Bold, polite live region) and an optional [badge] (path badge). When [onClick] is set (Android idle = scan QR) the ring presses
 * to .965 and shows a dashed focus ring; [actionLabel] sits under it. State changes crossfade in 320ms. Loops are static under reduced motion.
 */
@Composable
fun StatusRing(
    state: RingState,
    statusText: String,
    modifier: Modifier = Modifier,
    compact: Boolean = false,
    onClick: (() -> Unit)? = null,
    actionLabel: String? = null,
    badge: (@Composable () -> Unit)? = null,
) {
    val animations = rememberAnimationsEnabled()
    val box = if (compact) 200.dp else 232.dp
    val disc = if (compact) 152.dp else 176.dp
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val focused by source.collectIsFocusedAsState()
    val pressScale by animateFloatAsState(if (pressed) .965f else 1f, rdTween(animations), label = "ringPress")

    val edgeTarget = when (state) {
        RingState.Idle -> ReyDeskColors.LineStrong
        RingState.Error -> ReyDeskColors.Danger
        else -> ReyDeskColors.Accent
    }
    val glowTarget = when (state) {
        RingState.Idle -> ReyDeskColors.Accent to .30f
        RingState.Connecting -> ReyDeskColors.Accent to .45f
        RingState.Error -> ReyDeskColors.Danger to .55f
        else -> ReyDeskColors.Accent to .72f
    }
    val edge by animateColorAsState(edgeTarget, rdTween(animations, RdMotion.Slow), label = "ringEdge")
    val glowColor by animateColorAsState(glowTarget.first, rdTween(animations, RdMotion.Slow), label = "ringGlowColor")
    val glowBase by animateFloatAsState(glowTarget.second, rdTween(animations, RdMotion.Slow), label = "ringGlowAlpha")
    val breathing = state == RingState.ConnectedLocal || state == RingState.ConnectedRemote
    val transition = rememberInfiniteTransition(label = "ring")
    val breath = if (animations && breathing) transition.animateFloat(.5f, .95f, infiniteRepeatable(tween(2200), RepeatMode.Reverse), label = "breath").value else glowBase
    val sweep = if (animations && state == RingState.Connecting) transition.animateFloat(0f, 360f, infiniteRepeatable(tween(1300, easing = LinearEasing)), label = "sweep").value else 0f
    val edgeWidth = when (state) { RingState.Idle -> 1.5f; RingState.Error -> 2f; else -> 2.5f }
    val glyph = when (state) {
        RingState.ConnectedLocal -> "phone_check"
        RingState.ConnectedRemote -> "globe"
        RingState.Error -> "warning"
        else -> "devices"
    }
    val glyphColor = when (state) { RingState.Error -> ReyDeskColors.Danger; RingState.Idle -> ReyDeskColors.Muted; else -> ReyDeskColors.Accent }
    val dot = when (state) {
        RingState.Idle -> ReyDeskColors.Faint
        RingState.Connecting -> ReyDeskColors.Warning
        RingState.Error -> ReyDeskColors.Danger
        else -> ReyDeskColors.Success
    }

    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Box(
            Modifier.size(box).scale(pressScale)
                .let { if (onClick != null) it.clickable(source, null, role = Role.Button, onClick = onClick) else it },
            contentAlignment = Alignment.Center,
        ) {
            Canvas(Modifier.size(box)) {
                val c = center
                val glowAlpha = glowColor.copy(alpha = (if (breathing) breath else glowBase) * .55f)
                drawCircle(Brush.radialGradient(listOf(glowAlpha, Color.Transparent), c, size.minDimension / 2f), size.minDimension / 2f, c)
                val d = disc.toPx()
                drawCircle(Brush.radialGradient(listOf(ReyDeskColors.PanelTop, ReyDeskColors.Surface), c, d / 2f), d / 2f, c)
                val topLeft = Offset(c.x - d / 2f, c.y - d / 2f)
                if (state == RingState.Connecting) {
                    drawCircle(ReyDeskColors.RingTrack, d / 2f, c, style = Stroke(2.5.dp.toPx()))
                    drawArc(ReyDeskColors.Accent, sweep - 90f, 120f, false, topLeft, Size(d, d), style = Stroke(2.5.dp.toPx(), cap = StrokeCap.Round))
                } else {
                    drawCircle(edge, d / 2f, c, style = Stroke(edgeWidth.dp.toPx()))
                }
                if (focused && onClick != null) {
                    val fd = if (compact) 192.dp.toPx() else 212.dp.toPx()
                    drawCircle(ReyDeskColors.Accent, fd / 2f, c, style = Stroke(2.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(10f, 8f))))
                }
            }
            AppGlyph(glyph, Modifier.size(44.dp), glyphColor)
        }
        if (actionLabel != null && state == RingState.Idle) {
            Text(actionLabel, style = MaterialTheme.typography.labelLarge, color = ReyDeskColors.Accent)
        }
        Row(
            Modifier.semantics { liveRegion = LiveRegionMode.Polite },
            horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(8.dp).background(dot, CircleShape))
            Text(statusText, style = MaterialTheme.typography.titleLarge.copy(fontSize = 15.sp), color = ReyDeskColors.Fg)
        }
        if (badge != null) Box(Modifier.padding(top = 2.dp)) { badge() }
    }
}


@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360, heightDp = 1500)
@Composable
private fun StatusRingPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(20.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        StatusRing(RingState.Idle, "Waiting for your phone", onClick = {}, actionLabel = "Scan QR")
        StatusRing(RingState.Connecting, "Connecting", compact = true)
        StatusRing(RingState.ConnectedLocal, "Pixel 8", badge = { PathBadge(RdPath.Lan, "LAN") })
        StatusRing(RingState.ConnectedRemote, "Pixel 8", compact = true, badge = { PathBadge(RdPath.Relay, "Relay") })
        StatusRing(RingState.Error, "Connection failed")
    }
}
