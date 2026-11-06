package ir.reyden.reydesk.ui.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

/**
 * Contract 2.15 dialog: surface, 1dp line-strong, radius 16, padding 24, title 19 Bold, body 14 muted,
 * buttons at the reading end. [destructive] makes the confirm a danger button and puts default focus on Cancel.
 * Enter 200ms (opacity + scale .97 to 1), exit 120ms.
 */
@Composable
fun RdDialog(
    onDismiss: () -> Unit,
    title: String,
    confirmText: String,
    onConfirm: () -> Unit,
    modifier: Modifier = Modifier,
    dismissText: String? = null,
    destructive: Boolean = false,
    body: @Composable () -> Unit,
) {
    val animations = rememberAnimationsEnabled()
    val shown = remember { MutableTransitionState(false).apply { targetState = true } }
    val cancelFocus = remember { FocusRequester() }
    LaunchedEffect(destructive) { if (destructive) runCatching { cancelFocus.requestFocus() } }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        AnimatedVisibility(
            shown,
            enter = fadeIn(rdTween(animations, RdMotion.Base)) + scaleIn(rdTween(animations, RdMotion.Base), initialScale = .97f),
            exit = fadeOut(rdTween(animations)) + scaleOut(rdTween(animations), targetScale = .97f),
        ) {
            DialogCard(title, confirmText, onConfirm, onDismiss, dismissText, destructive, cancelFocus, modifier, body)
        }
    }
}

@Composable
private fun DialogCard(
    title: String, confirmText: String, onConfirm: () -> Unit, onDismiss: () -> Unit, dismissText: String?,
    destructive: Boolean, cancelFocus: FocusRequester, modifier: Modifier, body: @Composable () -> Unit,
) {
    val shape = ReyDeskShapes.Card
    Column(
        modifier.padding(horizontal = 24.dp).widthIn(max = 400.dp).clip(shape).background(ReyDeskColors.Surface, shape)
            .border(1.dp, ReyDeskColors.LineStrong, shape).padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(title, style = MaterialTheme.typography.titleLarge, color = ReyDeskColors.Fg)
        androidx.compose.runtime.CompositionLocalProvider(androidx.compose.material3.LocalContentColor provides ReyDeskColors.Muted) {
            androidx.compose.material3.ProvideTextStyle(MaterialTheme.typography.bodyMedium, body)
        }
        // Actions: 48dp targets, 12dp gap, equal-width when two (one full-width when alone) so they are never compact.
        Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            if (dismissText != null) RdButton(dismissText, onDismiss, Modifier.weight(1f).focusRequester(cancelFocus), kind = RdButtonKind.Secondary)
            RdButton(confirmText, onConfirm, if (dismissText != null) Modifier.weight(1f) else Modifier.fillMaxWidth(), kind = if (destructive) RdButtonKind.Danger else RdButtonKind.Primary)
        }
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun DialogPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        DialogCard("Compare codes", "Codes match", {}, {}, "Not the same", false, FocusRequester(), Modifier) { Text("Both screens must show the same code.") }
        DialogCard("Disconnect", "Disconnect", {}, {}, "Cancel", true, FocusRequester(), Modifier) { Text("The session will end.") }
    }
}
