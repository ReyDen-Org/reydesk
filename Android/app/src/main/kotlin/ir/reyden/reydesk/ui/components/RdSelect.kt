package ir.reyden.reydesk.ui.components

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.MutableTransitionState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsHoveredAsState
import androidx.compose.foundation.hoverable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusTarget
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.input.key.type
import androidx.compose.ui.input.key.utf16CodePoint
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.DpOffset
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntRect
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Popup
import androidx.compose.ui.window.PopupPositionProvider
import androidx.compose.ui.window.PopupProperties
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

private class BelowAnchor(private val gapPx: Int) : PopupPositionProvider {
    override fun calculatePosition(anchorBounds: IntRect, windowSize: IntSize, layoutDirection: LayoutDirection, popupContentSize: IntSize): IntOffset {
        val x = if (layoutDirection == LayoutDirection.Ltr) anchorBounds.left else anchorBounds.right - popupContentSize.width
        val below = anchorBounds.bottom + gapPx
        val y = if (below + popupContentSize.height <= windowSize.height) below else (anchorBounds.top - gapPx - popupContentSize.height).coerceAtLeast(0)
        return IntOffset(x.coerceIn(0, (windowSize.width - popupContentSize.width).coerceAtLeast(0)), y)
    }
}

/**
 * Contract 2.9 listbox. Custom Popup, never the platform dropdown menus.
 * Trigger 48dp, input bg + input line, radius 12, chevron rotates 180 on open. Popover: surface, line-strong, radius 16,
 * padding 4, 8dp elevation, max 320dp then scroll, width = trigger. Options 48dp with a check at the reading end.
 * Keyboard: Up/Down/Home/End/Enter/Escape and type-ahead; focus returns to the trigger.
 */
@Composable
fun <T> RdSelect(
    options: List<T>,
    selected: T?,
    onSelect: (T) -> Unit,
    optionLabel: (T) -> String,
    modifier: Modifier = Modifier,
    placeholder: String = "",
    label: String? = null,
    enabled: Boolean = true,
) {
    val animations = rememberAnimationsEnabled()
    val density = LocalDensity.current
    val source = remember { MutableInteractionSource() }
    val hovered by source.collectIsHoveredAsState()
    val focused by source.collectIsFocusedAsState()
    var open by remember { mutableStateOf(false) }
    var triggerWidth by remember { mutableIntStateOf(0) }
    val transition = remember { MutableTransitionState(false) }
    transition.targetState = open
    val chevron by animateFloatAsState(if (open) 180f else 0f, rdTween(animations, RdMotion.Base), label = "chevron")
    val triggerFocus = remember { FocusRequester() }
    val shape = ReyDeskShapes.Control
    val lineColor = when {
        !enabled -> ReyDeskColors.Line
        focused || open -> ReyDeskColors.Accent
        hovered -> ReyDeskColors.LineStrong
        else -> ReyDeskColors.LineInput
    }
    val selectedLabel = selected?.let(optionLabel)

    Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
        if (label != null) Text(label, style = MaterialTheme.typography.labelMedium, color = ReyDeskColors.Muted)
        Box {
            Row(
                Modifier.fillMaxWidth().heightIn(min = 48.dp).onSizeChanged { triggerWidth = it.width }
                    .clip(shape).background(ReyDeskColors.Surface, shape)
                    .border(if (focused || open) 1.5.dp else 1.dp, lineColor, shape)
                    .focusRequester(triggerFocus).hoverable(source)
                    .clickable(source, null, enabled = enabled, role = Role.DropdownList) { open = !open }
                    .semantics { if (selectedLabel != null) stateDescription = selectedLabel }
                    .padding(horizontal = 12.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(
                    selectedLabel ?: placeholder, Modifier.weight(1f), maxLines = 1,
                    style = MaterialTheme.typography.bodyMedium,
                    color = when {
                        !enabled -> ReyDeskColors.Disabled
                        selectedLabel != null -> ReyDeskColors.Fg
                        else -> ReyDeskColors.Faint
                    },
                )
                AppGlyph("down", Modifier.size(16.dp).rotate(chevron), if (enabled) ReyDeskColors.Muted else ReyDeskColors.Disabled)
            }
            if (transition.currentState || transition.targetState) {
                val close = { open = false; runCatching { triggerFocus.requestFocus() }; Unit }
                Popup(
                    popupPositionProvider = remember(density) { BelowAnchor(with(density) { 4.dp.roundToPx() }) },
                    onDismissRequest = { open = false },
                    properties = PopupProperties(focusable = true),
                ) {
                    PopupBody(transition, animations, density, options, selected, optionLabel, with(density) { triggerWidth.toDp() }, { onSelect(it); close() }, close)
                }
            }
        }
    }
}

@Composable
private fun <T> SelectList(options: List<T>, selected: T?, optionLabel: (T) -> String, width: Dp, onSelect: (T) -> Unit, onClose: () -> Unit) {
    val shape = ReyDeskShapes.Card
    val scroll = rememberScrollState()
    val focus = remember { FocusRequester() }
    var active by remember { mutableIntStateOf(options.indexOf(selected).coerceAtLeast(0)) }
    var typed by remember { mutableStateOf("") }
    var typedAt by remember { mutableStateOf(0L) }
    LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }
    Column(
        Modifier.width(width).heightIn(max = 320.dp)
            .shadow(8.dp, shape).clip(shape).background(ReyDeskColors.Surface, shape).border(1.dp, ReyDeskColors.LineStrong, shape)
            .focusRequester(focus).focusTarget()
            .onPreviewKeyEvent { e ->
                if (e.type != KeyEventType.KeyDown || options.isEmpty()) return@onPreviewKeyEvent false
                when (e.key) {
                    Key.DirectionDown -> { active = (active + 1).coerceAtMost(options.lastIndex); true }
                    Key.DirectionUp -> { active = (active - 1).coerceAtLeast(0); true }
                    Key.MoveHome -> { active = 0; true }
                    Key.MoveEnd -> { active = options.lastIndex; true }
                    Key.Enter, Key.NumPadEnter -> { onSelect(options[active]); true }
                    Key.Escape -> { onClose(); true }
                    else -> {
                        val ch = e.utf16CodePoint
                        if (ch > 32 && Character.isLetterOrDigit(ch)) {
                            val now = System.currentTimeMillis()
                            typed = if (now - typedAt > 800) "" else typed
                            typed += String(Character.toChars(ch)); typedAt = now
                            val hit = options.indexOfFirst { optionLabel(it).startsWith(typed, ignoreCase = true) }
                            if (hit >= 0) active = hit
                            true
                        } else false
                    }
                }
            }
            .padding(4.dp).verticalScroll(scroll),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        options.forEachIndexed { index, option ->
            val isSelected = option == selected
            val bg = when {
                isSelected -> ReyDeskColors.Selected
                index == active -> ReyDeskColors.Hover
                else -> androidx.compose.ui.graphics.Color.Transparent
            }
            Row(
                Modifier.fillMaxWidth().heightIn(min = 48.dp).clip(ReyDeskShapes.Xs).background(bg, ReyDeskShapes.Xs)
                    .clickable(role = Role.RadioButton) { onSelect(option) }
                    .padding(horizontal = 10.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(
                    optionLabel(option), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium,
                    fontWeight = if (isSelected) FontWeight.SemiBold else FontWeight.Normal, color = ReyDeskColors.Fg,
                )
                if (isSelected) AppGlyph("check", Modifier.size(16.dp), ReyDeskColors.Accent)
            }
        }
    }
}

@Preview(showBackground = true, backgroundColor = ReyDeskColors.PreviewBg, widthDp = 360)
@Composable
private fun SelectPreview() = ReyDeskTheme {
    val items = listOf("Desktop", "Control", "Files")
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        RdSelect(items, "Control", {}, { it }, label = "Mode")
        RdSelect(items, null, {}, { it }, placeholder = "Choose")
        RdSelect(items, "Files", {}, { it }, enabled = false)
        // Open state: the popover content on its own, since Popup does not render in static previews.
        SelectList(items, "Control", { it }, 328.dp, {}, {})
    }
}

@Composable
private fun <T> PopupBody(
    transition: MutableTransitionState<Boolean>, animations: Boolean, density: androidx.compose.ui.unit.Density,
    options: List<T>, selected: T?, optionLabel: (T) -> String, width: Dp, onSelect: (T) -> Unit, onClose: () -> Unit,
) {
    AnimatedVisibility(
        transition,
        enter = fadeIn(rdTween(animations)) + slideInVertically(rdTween(animations)) { -with(density) { 3.dp.roundToPx() } },
        exit = fadeOut(rdTween(animations)),
    ) { SelectList(options, selected, optionLabel, width, onSelect, onClose) }
}
