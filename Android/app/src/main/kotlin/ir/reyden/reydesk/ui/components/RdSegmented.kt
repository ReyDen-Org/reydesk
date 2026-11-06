package ir.reyden.reydesk.ui.components

import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

/**
 * Contract 2.11 segmented control. Container radius 14, padding 4, panel + 1dp line; items 40 radius 10.
 * Active item = hover bg, inset 1dp line-strong, accent glyph, fg label. The active background slides in 200ms.
 */
@Composable
fun <T> RdSegmented(
    options: List<T>,
    selected: T,
    onSelect: (T) -> Unit,
    optionLabel: (T) -> String,
    modifier: Modifier = Modifier,
    optionGlyph: ((T) -> String?)? = null,
) {
    val animations = rememberAnimationsEnabled()
    val rtl = LocalLayoutDirection.current == LayoutDirection.Rtl
    val outer = RoundedCornerShape(14.dp)
    val inner = RoundedCornerShape(10.dp)
    BoxWithConstraints(modifier.fillMaxWidth().clip(outer).background(ReyDeskColors.Panel, outer).border(1.dp, ReyDeskColors.Line, outer).padding(4.dp)) {
        val itemWidth = maxWidth / options.size.coerceAtLeast(1)
        val index = options.indexOf(selected).coerceAtLeast(0)
        val slide by animateDpAsState(itemWidth * index, rdTween(animations, RdMotion.Base), label = "segSlide")
        Box(
            Modifier.offset(x = if (rtl) -slide else slide).width(itemWidth).height(40.dp).align(Alignment.CenterStart)
                .background(ReyDeskColors.Hover, inner).border(1.dp, ReyDeskColors.LineStrong, inner),
        )
        Row(Modifier.selectableGroup()) {
            options.forEach { option ->
                val active = option == selected
                Row(
                    Modifier.width(itemWidth).height(40.dp).clip(inner)
                        .clickable(role = Role.RadioButton) { onSelect(option) },
                    horizontalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterHorizontally), verticalAlignment = Alignment.CenterVertically,
                ) {
                    optionGlyph?.invoke(option)?.let { AppGlyph(it, Modifier.size(18.dp), if (active) ReyDeskColors.Accent else ReyDeskColors.Muted) }
                    Text(
                        optionLabel(option), style = MaterialTheme.typography.labelLarge, maxLines = 1,
                        fontWeight = if (active) FontWeight.SemiBold else FontWeight.Medium,
                        color = if (active) ReyDeskColors.Fg else ReyDeskColors.Muted,
                    )
                }
            }
        }
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun SegmentedPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        RdSegmented(listOf("Desktop", "Control"), "Desktop", {}, { it }, optionGlyph = { if (it == "Desktop") "devices" else "touch" })
        RdSegmented(listOf("Low", "Balanced", "High"), "High", {}, { it })
    }
}
