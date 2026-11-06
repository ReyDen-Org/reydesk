package ir.reyden.reydesk.ui.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

enum class RdToastKind { Info, Success, Warning, Error }

data class RdToastMessage(
    val text: String,
    val kind: RdToastKind = RdToastKind.Info,
    val actionLabel: String? = null,
    val onAction: (() -> Unit)? = null,
    val id: Long = System.nanoTime(),
)

/** Hold one of these per screen/host and call [show]. Errors stay until closed, others hide after 4s. */
@Stable
class RdToastState {
    var current by mutableStateOf<RdToastMessage?>(null)
        private set
    fun show(text: String, kind: RdToastKind = RdToastKind.Info, actionLabel: String? = null, onAction: (() -> Unit)? = null) {
        current = RdToastMessage(text, kind, actionLabel, onAction)
    }
    fun dismiss() { current = null }
}

@Composable
fun rememberRdToastState(): RdToastState = remember { RdToastState() }

/** Place at the bottom of a Box. Above the bottom nav: pass the nav height as [bottomInset]. */
@Composable
fun RdToastHost(state: RdToastState, modifier: Modifier = Modifier, bottomInset: androidx.compose.ui.unit.Dp = 16.dp, closeLabel: String = "") {
    val animations = rememberAnimationsEnabled()
    val density = LocalDensity.current
    val message = state.current
    LaunchedEffect(message?.id) {
        if (message != null && message.kind != RdToastKind.Error) { delay(4000); state.dismiss() }
    }
    Box(modifier.padding(horizontal = 16.dp).padding(bottom = bottomInset), contentAlignment = Alignment.BottomCenter) {
        AnimatedVisibility(
            visible = message != null,
            enter = fadeIn(rdTween(animations, RdMotion.Base)) + slideInVertically(rdTween(animations, RdMotion.Base)) { with(density) { 8.dp.roundToPx() } },
            exit = fadeOut(rdTween(animations)),
        ) {
            // Keep the last message while the exit animation runs.
            val shown = remember(message?.id) { message }
            if (shown != null) RdToast(shown, onClose = state::dismiss, closeLabel = closeLabel)
        }
    }
}

/** Contract 2.16 toast body. Stateless so previews and tests can draw every kind. */
@Composable
fun RdToast(message: RdToastMessage, onClose: () -> Unit, modifier: Modifier = Modifier, closeLabel: String = "") {
    val (glyph, tint) = when (message.kind) {
        RdToastKind.Info -> "info" to ReyDeskColors.Accent
        RdToastKind.Success -> "check" to ReyDeskColors.Success
        RdToastKind.Warning -> "warning" to ReyDeskColors.Warning
        RdToastKind.Error -> "warning" to ReyDeskColors.Danger
    }
    val shape = ReyDeskShapes.Control
    Row(
        modifier.widthIn(max = 480.dp).defaultMinSize(minHeight = 48.dp).clip(shape).background(ReyDeskColors.Surface, shape)
            .border(1.dp, ReyDeskColors.LineStrong, shape).padding(start = 14.dp, end = 4.dp)
            .semantics { liveRegion = LiveRegionMode.Polite },
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        AppGlyph(glyph, Modifier.size(16.dp), tint)
        Text(message.text, Modifier.weight(1f, fill = false).padding(vertical = 10.dp), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
        if (message.actionLabel != null && message.onAction != null) {
            RdButton(message.actionLabel, { message.onAction.invoke(); onClose() }, kind = RdButtonKind.Ghost)
        }
        if (message.kind == RdToastKind.Error) RdIconButton("close", onClose, closeLabel)
        else Box(Modifier.size(10.dp))
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun ToastPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        RdToast(RdToastMessage("Copied"), {})
        RdToast(RdToastMessage("Paired with Pixel", RdToastKind.Success), {})
        RdToast(RdToastMessage("Weak connection", RdToastKind.Warning, "Retry", {}), {})
        RdToast(RdToastMessage("Could not reach the server", RdToastKind.Error), {}, closeLabel = "Close")
    }
}
