package ir.reyden.reydesk.ui.connection

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.OffsetMapping
import androidx.compose.ui.text.input.TransformedText
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import ir.reyden.reydesk.R
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.ui.components.AppGlyph
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdDialog
import ir.reyden.reydesk.ui.components.RdPath
import ir.reyden.reydesk.ui.text.Digits
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.ui.theme.ReyDeskTheme
import ir.reyden.reydesk.ui.theme.ReyDeskType

/** Shows the raw Desk ID as XXXX-XXXX-XXXX; the stored text never holds dashes. */
private val DeskIdTransformation = VisualTransformation { text ->
    TransformedText(
        AnnotatedString(deskIdDisplay(text.text)),
        object : OffsetMapping {
            override fun originalToTransformed(offset: Int) = deskIdToDisplayOffset(offset)
            override fun transformedToOriginal(offset: Int) = deskIdToRawOffset(offset)
        },
    )
}

@Composable
private fun FieldFrame(label: String, error: String?, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium, color = ReyDeskColors.Muted)
        content()
        if (error != null) Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
            AppGlyph("warning", Modifier.size(14.dp), ReyDeskColors.Danger)
            Text(error, style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Danger)
        }
    }
}

/** Desk ID input (contract 2.11): mono, LTR, stored raw without dashes, shown in groups of 4, check character validated before any network call. */
@Composable
internal fun DeskIdField(value: String, onChange: (String) -> Unit, enabled: Boolean = true, modifier: Modifier = Modifier) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    val raw = deskIdRaw(value)
    val complete = raw.length == DESK_ID_LENGTH
    val invalid = complete && DeskIdCodec.parse(raw) is DeskIdCodec.Result.Err
    FieldFrame(stringResource(R.string.internet_desk_id_label), if (invalid) stringResource(R.string.internet_id_invalid) else null) {
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
            BasicTextField(
                value = raw, onValueChange = { onChange(deskIdRaw(it)) }, visualTransformation = DeskIdTransformation, enabled = enabled, singleLine = true,
                interactionSource = source, textStyle = ReyDeskType.Id.copy(color = ReyDeskColors.Fg),
                cursorBrush = SolidColor(ReyDeskColors.Accent),
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters, autoCorrectEnabled = false, keyboardType = KeyboardType.Password),
                modifier = modifier.fillMaxWidth(),
                decorationBox = { inner ->
                    Box(
                        Modifier.fillMaxWidth().height(48.dp).clip(ReyDeskShapes.Control).background(ReyDeskColors.Hover, ReyDeskShapes.Control)
                            .border(
                                if (focused || invalid) BorderStroke(1.5.dp, if (invalid) ReyDeskColors.Danger else ReyDeskColors.Accent) else BorderStroke(1.dp, ReyDeskColors.LineInput),
                                ReyDeskShapes.Control,
                            ).padding(horizontal = 12.dp),
                        contentAlignment = Alignment.CenterStart,
                    ) {
                        if (raw.isEmpty()) Text("XXXX-XXXX-XXXX", style = ReyDeskType.Id, color = ReyDeskColors.Faint)
                        inner()
                    }
                },
            )
        }
    }
}

/** Six equal-width cells, 52dp high, radius 10, mono 22, LTR (contract 2.11). One hidden field feeds all cells. */
@Composable
internal fun CodeCells(value: String, onChange: (String) -> Unit, enabled: Boolean = true) {
    val source = remember { MutableInteractionSource() }
    val focused by source.collectIsFocusedAsState()
    FieldFrame(stringResource(R.string.internet_code_label), null) {
        CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
            BasicTextField(
                value = value, onValueChange = { onChange(Digits.toLatin(it).filter { c -> c in '0'..'9' }.take(6)) },
                enabled = enabled, singleLine = true, interactionSource = source,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                textStyle = ReyDeskType.Mono.copy(color = ReyDeskColors.Fg),
                cursorBrush = SolidColor(ReyDeskColors.Accent),
                decorationBox = {
                    // The whole row is the one text field, so it is the tap target: full width, at least 52dp high.
                    Row(Modifier.fillMaxWidth().heightIn(min = 52.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        for (i in 0 until 6) {
                            val active = focused && i == value.length.coerceAtMost(5)
                            Box(
                                Modifier.weight(1f).heightIn(min = 52.dp).clip(ReyDeskShapes.Sm).background(ReyDeskColors.Hover, ReyDeskShapes.Sm)
                                    .border(if (active) BorderStroke(1.5.dp, ReyDeskColors.Accent) else BorderStroke(1.dp, ReyDeskColors.LineInput), ReyDeskShapes.Sm),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(value.getOrNull(i)?.toString().orEmpty(), style = ReyDeskType.Mono.copy(fontSize = 22.sp, lineHeight = 28.sp), color = ReyDeskColors.Fg)
                            }
                        }
                    }
                },
            )
        }
    }
}

/** SAS compare card (contract 2.5): both devices show the same 6 digits in Latin mono. */
@Composable
internal fun SasDialog(sas: String, onSame: () -> Unit, onDifferent: () -> Unit) {
    val mismatchFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { mismatchFocus.requestFocus() } }
    RdDialog(
        onDismiss = onDifferent, title = stringResource(R.string.sas_title),
        confirmText = stringResource(R.string.sas_match), onConfirm = onSame,
    ) {
        Column(verticalArrangement = Arrangement.spacedBy(12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Box(
                Modifier.fillMaxWidth().clip(ReyDeskShapes.Card).background(ReyDeskColors.Tile, ReyDeskShapes.Card)
                    .border(1.dp, ReyDeskColors.TileLine, ReyDeskShapes.Card).padding(vertical = 18.dp),
                contentAlignment = Alignment.Center,
            ) {
                CompositionLocalProvider(LocalLayoutDirection provides LayoutDirection.Ltr) {
                    Text(sas.chunked(3).joinToString(" "), style = ReyDeskType.Sas, color = ReyDeskColors.Fg, textAlign = TextAlign.Center)
                }
            }
            Text(stringResource(R.string.sas_body), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
            RdButton(stringResource(R.string.sas_mismatch), onDifferent, Modifier.fillMaxWidth().focusRequester(mismatchFocus), kind = RdButtonKind.Danger)
        }
    }
}

/** Short text for the server state line and settings row. */
@Composable
internal fun remoteStateText(state: RemoteServerState): String = stringResource(
    when (state) {
        RemoteServerState.Off -> R.string.internet_state_off
        RemoteServerState.Connecting -> R.string.internet_state_connecting
        RemoteServerState.Online -> R.string.internet_state_online
        RemoteServerState.Offline -> R.string.internet_state_offline
    },
)

internal fun remoteStateColor(state: RemoteServerState) = when (state) {
    RemoteServerState.Online -> ReyDeskColors.Success
    RemoteServerState.Connecting -> ReyDeskColors.Warning
    RemoteServerState.Offline -> ReyDeskColors.Faint
    RemoteServerState.Off -> ReyDeskColors.Disabled
}

@androidx.compose.ui.tooling.preview.Preview(widthDp = 360)
@Composable
private fun RemoteViewsPreview() = ReyDeskTheme {
    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        DeskIdField("7KQ4-MZ2P-X9DA", {})
        CodeCells("482", {})
        Text(remoteStateText(RemoteServerState.Online), color = remoteStateColor(RemoteServerState.Online))
        val preview = RemoteUiState("7KQ4MZ2PX9DA", "4F7Q2M9XHC3D", RemoteServerState.Online, RdPath.Direct, true, sas = "482913")
        Text(preview.deskId.orEmpty())
    }
}
