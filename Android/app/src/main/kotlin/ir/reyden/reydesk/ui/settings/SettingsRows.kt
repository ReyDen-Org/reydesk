package ir.reyden.reydesk.ui.settings

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.runtime.LaunchedEffect
import kotlinx.coroutines.delay
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.IntSize
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.LayoutDirection
import ir.reyden.reydesk.ui.components.AppGlyph
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskType
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.components.RdIconTile
import ir.reyden.reydesk.ui.theme.ReyDeskColors

/** Contract 2.11 toggle row: optional 28 tile, label, optional helper, whole row clickable at 48dp minimum. */
@Composable
internal fun SwitchRow(
    label: String, checked: Boolean, onChange: ((Boolean) -> Unit)?, modifier: Modifier = Modifier,
    glyph: String? = null, helper: String? = null, enabled: Boolean = true,
) {
    val usable = enabled && onChange != null
    Row(
        modifier.fillMaxWidth().heightIn(min = 48.dp)
            .toggleable(value = checked, enabled = usable, role = Role.Switch, onValueChange = { onChange?.invoke(it) })
            .padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically,
    ) {
        if (glyph != null) RdIconTile(glyph, small = true)
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(label, style = MaterialTheme.typography.bodyLarge, color = if (usable) ReyDeskColors.Fg else ReyDeskColors.Disabled)
            if (helper != null) Text(helper, style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
        }
        Switch(checked = checked, onCheckedChange = null, enabled = usable)
    }
}

@Composable
internal fun InfoRow(label: String, value: String, valueStyle: TextStyle = MaterialTheme.typography.bodyMedium) {
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(16.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Fg)
        Text(value, style = valueStyle, color = ReyDeskColors.Muted)
    }
}

/** Plain 48dp text field in the contract input style (same frame as the Desk ID field); URLs are always LTR. */
@Composable
internal fun UrlField(label: String, value: String, onChange: (String) -> Unit, error: String? = null) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    var fieldSize by remember { mutableStateOf(IntSize.Zero) }
    val density = LocalDensity.current
    val bringIntoView = remember { BringIntoViewRequester() }
    LaunchedEffect(focused) {
        // Wait for the IME resize, then scroll the field (and the Save button below it) into view.
        // The rect is extended by the Save button height (48dp plus gap) below the field.
        if (focused) { delay(300); bringIntoView.bringIntoView(Rect(0f, 0f, fieldSize.width.toFloat(), fieldSize.height + with(density) { 64.dp.toPx() })) }
    }
    Column(Modifier.onSizeChanged { fieldSize = it }.bringIntoViewRequester(bringIntoView), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = ReyDeskColors.Muted)
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
            BasicTextField(
                value = value, onValueChange = onChange, singleLine = true, interactionSource = source,
                textStyle = ReyDeskType.Mono.copy(color = ReyDeskColors.Fg), cursorBrush = SolidColor(ReyDeskColors.Accent),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth(),
                decorationBox = { inner ->
                    Box(
                        Modifier.fillMaxWidth().height(48.dp).clip(ReyDeskShapes.Control).background(ReyDeskColors.Hover, ReyDeskShapes.Control)
                            .border(
                                if (focused || error != null) BorderStroke(1.5.dp, if (error != null) ReyDeskColors.Danger else ReyDeskColors.Accent) else BorderStroke(1.dp, ReyDeskColors.LineInput),
                                ReyDeskShapes.Control,
                            ).padding(horizontal = 12.dp),
                        contentAlignment = Alignment.CenterStart,
                    ) {
                        if (value.isEmpty()) Text("wss://", style = ReyDeskType.Mono, color = ReyDeskColors.Faint)
                        inner()
                    }
                },
            )
        }
        if (error != null) Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            AppGlyph("warning", Modifier.size(14.dp), ReyDeskColors.Danger)
            Text(error, style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Danger)
        }
    }
}
