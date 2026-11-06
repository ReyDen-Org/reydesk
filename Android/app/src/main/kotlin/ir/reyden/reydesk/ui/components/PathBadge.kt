package ir.reyden.reydesk.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

enum class RdPath { Lan, Direct, Relay }

/** Connection path pill (contract 2.6): 26 high, glyph 14 + 12 SemiBold. [label] is the localized path name. */
@Composable
fun PathBadge(path: RdPath, label: String, modifier: Modifier = Modifier) {
    val bg: Color
    val line: Color
    val tint: Color
    val glyph: String
    when (path) {
        RdPath.Lan -> { bg = ReyDeskColors.Tint; line = ReyDeskColors.TintLine; tint = ReyDeskColors.Accent; glyph = "router" }
        RdPath.Direct -> { bg = Color.Transparent; line = ReyDeskColors.Accent; tint = ReyDeskColors.Accent; glyph = "arrow_link" }
        RdPath.Relay -> { bg = ReyDeskColors.WarningBg; line = ReyDeskColors.WarningLine; tint = ReyDeskColors.Warning; glyph = "server" }
    }
    Row(
        modifier.height(26.dp).background(bg, ReyDeskShapes.Pill).border(1.dp, line, ReyDeskShapes.Pill).padding(horizontal = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically,
    ) {
        AppGlyph(glyph, Modifier.size(14.dp), tint)
        Text(label, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold, color = tint, maxLines = 1)
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg)
@Composable
private fun PathBadgePreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        PathBadge(RdPath.Lan, "LAN")
        PathBadge(RdPath.Direct, "Direct")
        PathBadge(RdPath.Relay, "Relay")
    }
}
