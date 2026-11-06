package ir.reyden.reydesk.ui.companion

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.heightIn
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import ir.reyden.reydesk.ui.components.AppGlyph
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes

/** Contract 2.11 field colors: input bg, input line, accent focus line, radius 12. */
@Composable
internal fun rdFieldColors() = OutlinedTextFieldDefaults.colors(
    focusedContainerColor = ReyDeskColors.Hover, unfocusedContainerColor = ReyDeskColors.Hover,
    focusedBorderColor = ReyDeskColors.Accent, unfocusedBorderColor = ReyDeskColors.LineInput,
    focusedTextColor = ReyDeskColors.Fg, unfocusedTextColor = ReyDeskColors.Fg, cursorColor = ReyDeskColors.Accent,
    focusedLabelColor = ReyDeskColors.Accent, unfocusedLabelColor = ReyDeskColors.Muted,
)

/** Text field with a 12-medium label above (contract 2.11) and optional helper below. */
@Composable
internal fun RdTextField(
    value: String, onValueChange: (String) -> Unit, label: String, modifier: Modifier = Modifier,
    helper: String? = null, singleLine: Boolean = false, minLines: Int = 1,
) {
    Column(modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = ReyDeskColors.Muted)
        OutlinedTextField(
            value = value, onValueChange = onValueChange, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp),
            singleLine = singleLine, minLines = minLines, shape = ReyDeskShapes.Control, colors = rdFieldColors(),
        )
        if (helper != null) Text(helper, style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Faint)
    }
}

/** Contract 2.11 progress: 8dp pill, track Hover, fill gradient AccentFill to AccentHover. */
@Composable
internal fun RdProgress(fraction: Float, modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().height(8.dp).clip(ReyDeskShapes.Pill).background(ReyDeskColors.Hover)) {
        Box(
            Modifier.fillMaxWidth(fraction.coerceIn(0f, 1f)).height(8.dp).clip(ReyDeskShapes.Pill)
                .background(Brush.horizontalGradient(listOf(ReyDeskColors.AccentFill, ReyDeskColors.AccentHover))),
        )
    }
}

/** Dashed 1.5dp rounded border (contract 3: drop zone card). */
internal fun Modifier.dashedBorder(color: Color): Modifier = drawBehind {
    drawRoundRect(
        color = color, cornerRadius = CornerRadius(16.dp.toPx()),
        style = Stroke(width = 1.5.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(10.dp.toPx(), 7.dp.toPx()))),
    )
}

/** Contract 2.11 checkbox: 18dp, radius 6, off = 1.5 input line, on = accent fill + white check. Click handling is on the row. */
@Composable
internal fun RdCheck(checked: Boolean) {
    Box(
        Modifier.size(18.dp).clip(ReyDeskShapes.Xs)
            .background(if (checked) ReyDeskColors.AccentFill else Color.Transparent, ReyDeskShapes.Xs)
            .border(1.5.dp, if (checked) ReyDeskColors.AccentFill else ReyDeskColors.LineInput, ReyDeskShapes.Xs),
        contentAlignment = Alignment.Center,
    ) { if (checked) AppGlyph("check", Modifier.size(14.dp), ReyDeskColors.OnAccent) }
}
