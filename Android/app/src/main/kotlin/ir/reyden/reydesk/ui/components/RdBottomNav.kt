package ir.reyden.reydesk.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.ui.draw.drawBehind
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

/**
 * Bottom nav (contract 2.2): 64dp, surface, 1dp top line. Selected = 56x32 pill (selected token) behind an accent glyph, label 12 Medium fg;
 * unselected = muted glyph, faint label. Items are 48dp+ touch targets. [items] is (route and glyph kind) to localized label.
 */
@Composable
fun RdBottomNav(selectedRoute: String?, items: List<Pair<String, String>>, onSelect: (String) -> Unit, modifier: Modifier = Modifier) {
    val animations = rememberAnimationsEnabled()
    Row(
        modifier.fillMaxWidth().background(ReyDeskColors.Surface)
            .drawBehind { drawRect(ReyDeskColors.Line, size = androidx.compose.ui.geometry.Size(size.width, 1.dp.toPx())) }
            .navigationBarsPadding().height(64.dp).selectableGroup(),
        horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically,
    ) {
        items.forEach { (route, label) ->
            val selected = route == selectedRoute
            val pill by animateColorAsState(if (selected) ReyDeskColors.Selected else androidx.compose.ui.graphics.Color.Transparent, rdTween(animations), label = "navPill")
            val tint by animateColorAsState(if (selected) ReyDeskColors.Accent else ReyDeskColors.Muted, rdTween(animations), label = "navTint")
            Column(
                Modifier.weight(1f).height(64.dp)
                    .selectable(selected, remember { MutableInteractionSource() }, null, role = Role.Tab) { onSelect(route) },
                horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterVertically),
            ) {
                Box(Modifier.width(56.dp).height(32.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(16.dp)).background(pill), contentAlignment = Alignment.Center) {
                    AppGlyph(route, Modifier.size(22.dp), tint)
                }
                Text(
                    label, style = MaterialTheme.typography.labelMedium, maxLines = 1,
                    fontWeight = FontWeight.Medium, color = if (selected) ReyDeskColors.Fg else ReyDeskColors.Faint,
                )
            }
        }
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun BottomNavPreview() = ReyDeskTheme {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        val items = listOf("connection" to "Connection", "devices" to "Devices", "settings" to "Settings")
        RdBottomNav("connection", items, {})
        RdBottomNav("devices", items, {})
        RdBottomNav("settings", items, {})
    }
}
