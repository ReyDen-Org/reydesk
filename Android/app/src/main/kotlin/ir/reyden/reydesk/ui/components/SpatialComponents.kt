package ir.reyden.reydesk.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

/** Card anatomy (contract 2.4/2.8): panel gradient, radius 16, 1dp line, padding 16x14. Highlighted = accent tint line. */
@Composable
fun ConsolePanel(modifier: Modifier = Modifier, highlighted: Boolean = false, content: @Composable ColumnScope.() -> Unit) {
    val shape = ReyDeskShapes.Card
    Column(
        modifier.clip(shape)
            .background(Brush.verticalGradient(listOf(ReyDeskColors.PanelTop, ReyDeskColors.Panel)), shape)
            .border(1.dp, if (highlighted) ReyDeskColors.TintLine else ReyDeskColors.Line, shape)
            .padding(horizontal = 16.dp, vertical = 14.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp), content = content,
    )
}

@Composable
fun ScreenHeading(title: String, subtitle: String, modifier: Modifier = Modifier) {
    Column(modifier.padding(bottom = 4.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
        Text(title, style = MaterialTheme.typography.headlineMedium, color = ReyDeskColors.Fg)
        Text(subtitle, style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
    }
}

@Composable
fun SectionLabel(title: String, kind: String, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
        RdIconTile(kind, small = true)
        Text(title, style = MaterialTheme.typography.titleMedium, color = ReyDeskColors.Fg)
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun CardPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        ScreenHeading("Connection", "Waiting for your phone")
        ConsolePanel {
            SectionLabel("Device", "devices")
            Text("Card body", style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
        }
        ConsolePanel(highlighted = true) {
            Text("Highlighted card", style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
        }
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun GlyphPreview() = ReyDeskTheme {
    val kinds = listOf("connection", "devices", "settings", "phone_hub", "phone_check", "transfers", "copy", "check", "qr", "globe",
        "router", "arrow_link", "server", "shield", "code", "update", "pulse", "key", "power", "trash", "edit", "info", "close",
        "chevron", "down", "plus", "folder", "bell", "eye", "search", "warning", "id")
    androidx.compose.foundation.layout.FlowRow(Modifier.padding(16.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        kinds.forEach { AppGlyph(it, Modifier.size(24.dp), ReyDeskColors.Accent) }
    }
}
