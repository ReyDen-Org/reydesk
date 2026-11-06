package ir.reyden.reydesk.ui.components

import androidx.compose.animation.animateColor
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

/** Skeleton block (contract 2.11): shimmer between two token colors over 1.4s, static when animations are off. Give it the final element's size. */
@Composable
fun SkeletonBlock(modifier: Modifier = Modifier, shape: Shape = ReyDeskShapes.Xs) {
    val color: Color = if (rememberAnimationsEnabled()) {
        rememberInfiniteTransition(label = "skeleton").animateColor(
            ReyDeskColors.Line, ReyDeskColors.LineStrong,
            infiniteRepeatable(tween(1400), RepeatMode.Reverse), label = "skeletonColor",
        ).value
    } else ReyDeskColors.Line
    androidx.compose.foundation.layout.Box(modifier.clip(shape).background(color, shape))
}

/** Text-line skeletons with the contract's 60/40 width rhythm. */
@Composable
fun SkeletonLines(count: Int = 2, lineHeight: Dp = 14.dp, modifier: Modifier = Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        repeat(count) { i ->
            SkeletonBlock(Modifier.fillMaxWidth(if (i % 2 == 0) .6f else .4f).height(lineHeight))
        }
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun SkeletonPreview() = ReyDeskTheme {
    ConsolePanel(Modifier.padding(16.dp)) {
        SkeletonBlock(Modifier.fillMaxWidth(.5f).height(24.dp))
        SkeletonLines(3)
    }
}
