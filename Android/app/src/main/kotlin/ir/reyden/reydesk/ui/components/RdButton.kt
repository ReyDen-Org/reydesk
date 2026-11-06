package ir.reyden.reydesk.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsHoveredAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.hoverable
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

enum class RdButtonKind { Primary, Secondary, Tonal, Ghost, Danger }

private data class ButtonLook(val rest: Color, val hover: Color, val pressed: Color, val line: Color?, val text: Color)

private fun look(kind: RdButtonKind): ButtonLook = when (kind) {
    RdButtonKind.Primary -> ButtonLook(ReyDeskColors.AccentFill, ReyDeskColors.AccentFillHover, ReyDeskColors.AccentFillPressed, null, ReyDeskColors.OnAccent)
    RdButtonKind.Secondary -> ButtonLook(ReyDeskColors.Button, ReyDeskColors.Hover, ReyDeskColors.Line, ReyDeskColors.LineStrong, ReyDeskColors.Fg)
    RdButtonKind.Tonal -> ButtonLook(ReyDeskColors.Tile, ReyDeskColors.Tint, ReyDeskColors.TonalPressed, ReyDeskColors.TileLine, ReyDeskColors.Accent)
    RdButtonKind.Ghost -> ButtonLook(Color.Transparent, ReyDeskColors.Hover, ReyDeskColors.Line, null, ReyDeskColors.Muted)
    RdButtonKind.Danger -> ButtonLook(
        ReyDeskColors.DangerBg, lerp(ReyDeskColors.DangerBg, ReyDeskColors.DangerLine, .4f), lerp(ReyDeskColors.DangerBg, ReyDeskColors.DangerLine, .75f),
        ReyDeskColors.DangerLine, ReyDeskColors.Danger,
    )
}

/**
 * Contract 2.10 button. Height 48dp (touch target), radius 12, padding 16, label 14 Medium, optional 18 glyph.
 * Pressed scale .98 in 120ms, focus ring 2dp accent, disabled = panel + line + disabled text.
 */
@Composable
fun RdButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    kind: RdButtonKind = RdButtonKind.Primary,
    glyph: String? = null,
    enabled: Boolean = true,
) {
    val animations = rememberAnimationsEnabled()
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val hovered by source.collectIsHoveredAsState()
    val focused by source.collectIsFocusedAsState()
    val l = look(kind)
    val bgTarget = when {
        !enabled -> ReyDeskColors.Panel
        pressed -> l.pressed
        hovered -> l.hover
        else -> l.rest
    }
    val bg by animateColorAsState(bgTarget, rdTween(animations), label = "btnBg")
    val fg = when {
        !enabled -> ReyDeskColors.Disabled
        kind == RdButtonKind.Ghost && (hovered || pressed) -> ReyDeskColors.Fg
        else -> l.text
    }
    val scale by animateFloatAsState(if (pressed && enabled) .98f else 1f, rdTween(animations), label = "btnScale")
    val shape = ReyDeskShapes.Control
    val border = when {
        focused -> BorderStroke(2.dp, ReyDeskColors.Accent)
        !enabled -> BorderStroke(1.dp, ReyDeskColors.Line)
        l.line != null -> BorderStroke(1.dp, l.line)
        else -> null
    }
    Row(
        modifier.scale(scale).defaultMinSize(minWidth = 88.dp, minHeight = 48.dp).clip(shape).background(bg, shape)
            .let { if (border != null) it.border(border, shape) else it }
            .hoverable(source)
            .clickable(source, null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (glyph != null) AppGlyph(glyph, Modifier.size(18.dp), fg)
        Text(text, style = MaterialTheme.typography.labelLarge, color = fg, maxLines = 1)
    }
}

/** Square ghost icon button, 48dp target (device card actions, toolbar). Danger tint on hover for destructive actions. */
@Composable
fun RdIconButton(glyph: String, onClick: () -> Unit, contentDescription: String, modifier: Modifier = Modifier, danger: Boolean = false, active: Boolean = false, enabled: Boolean = true) {
    val animations = rememberAnimationsEnabled()
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    val hovered by source.collectIsHoveredAsState()
    val focused by source.collectIsFocusedAsState()
    val bgTarget = when {
        pressed -> if (danger) ReyDeskColors.DangerBg else ReyDeskColors.Line
        hovered -> if (danger) ReyDeskColors.DangerBg else ReyDeskColors.Hover
        active -> ReyDeskColors.Selected
        else -> Color.Transparent
    }
    val bg by animateColorAsState(bgTarget, rdTween(animations), label = "iconBtnBg")
    val tint = when {
        !enabled -> ReyDeskColors.Disabled
        danger && (hovered || pressed) -> ReyDeskColors.Danger
        active -> ReyDeskColors.Accent
        hovered || pressed -> ReyDeskColors.Fg
        else -> ReyDeskColors.Muted
    }
    val shape = RoundedCornerShape(12.dp)
    androidx.compose.foundation.layout.Box(
        modifier.size(48.dp).clip(shape).background(bg, shape)
            .let { if (focused) it.border(2.dp, ReyDeskColors.Accent, shape) else it }
            .hoverable(source)
            .clickable(source, null, enabled = enabled, role = Role.Button, onClick = onClick)
            .androidxSemantics(contentDescription),
        contentAlignment = Alignment.Center,
    ) { AppGlyph(glyph, Modifier.size(22.dp), tint) }
}

private fun Modifier.androidxSemantics(description: String): Modifier =
    this.then(Modifier.semantics { this.contentDescription = description })

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun ButtonPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        RdButtonKind.entries.forEach { RdButton(it.name, {}, kind = it, glyph = "plus") }
        RdButton("Disabled", {}, enabled = false)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            RdIconButton("edit", {}, "Edit")
            RdIconButton("trash", {}, "Delete", danger = true)
            RdIconButton("eye", {}, "View", active = true)
        }
    }
}
