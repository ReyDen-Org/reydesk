package ir.reyden.reydesk.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.LocalContentColor
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.dp
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import kotlin.math.cos
import kotlin.math.sin

private const val StrokeWidth = 1.9f

private fun rr(x: Float, y: Float, w: Float, h: Float, r: Float): String =
    "M${x + r} ${y}h${w - 2 * r}a$r $r 0 0 1 $r ${r}v${h - 2 * r}a$r $r 0 0 1 -$r ${r}h-${w - 2 * r}a$r $r 0 0 1 -$r -${r}v-${h - 2 * r}a$r $r 0 0 1 $r -${r}z"

private fun circ(cx: Float, cy: Float, r: Float): String =
    "M${cx - r} ${cy}a$r $r 0 1 0 ${2 * r} 0a$r $r 0 1 0 -${2 * r} 0"

/** Stroke icons on a 24 grid (Lucide path data, ISC licence). Each kind is a list of SVG path strings. */
private val StrokeIcons: Map<String, List<String>> = mapOf(
    "connection" to listOf("M9 17H7A5 5 0 0 1 7 7h2", "M15 7h2a5 5 0 1 1 0 10h-2", "M8 12h8"),
    "devices" to listOf("M18 8V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h8", "M10 19v-3", "M6 20h12", rr(16f, 12f, 6f, 10f, 2f)),
    "phone_hub" to listOf(rr(5f, 2f, 14f, 20f, 2f), "M12 18h.01"),
    "phone_check" to listOf(rr(5f, 2f, 14f, 20f, 2f), "m9.5 11 2 2 3-3.5", "M12 18h.01"),
    "transfers" to listOf("M8 3 4 7l4 4", "M4 7h16", "m16 21 4-4-4-4", "M20 17H4"),
    "copy" to listOf(rr(8f, 8f, 14f, 14f, 2f), "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"),
    "check" to listOf("M20 6 9 17l-5-5"),
    "qr" to listOf(
        rr(3f, 3f, 5f, 5f, 1f), rr(16f, 3f, 5f, 5f, 1f), rr(3f, 16f, 5f, 5f, 1f),
        "M21 16h-3a2 2 0 0 0-2 2v3", "M21 21v.01", "M12 7v3a2 2 0 0 1-2 2H7", "M3 12h.01", "M12 3h.01",
        "M12 16v.01", "M16 12h1", "M21 12v.01", "M12 21v-1",
    ),
    "globe" to listOf(circ(12f, 12f, 10f), "M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20", "M2 12h20"),
    "router" to listOf(rr(2f, 14f, 20f, 8f, 2f), "M6.01 18H6", "M10.01 18H10", "M15 10v4", "M17.84 7.17a4 4 0 0 0-5.66 0", "M20.66 4.34a8 8 0 0 0-11.31 0"),
    "arrow_link" to listOf("M7 7h10v10", "M7 17 17 7"),
    "server" to listOf(rr(2f, 2f, 20f, 8f, 2f), rr(2f, 14f, 20f, 8f, 2f), "M6 6h.01", "M6 18h.01"),
    "shield" to listOf("M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"),
    "code" to listOf("m16 18 6-6-6-6", "m8 6-6 6 6 6"),
    "update" to listOf("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "M7 10l5 5 5-5", "M12 15V3"),
    "pulse" to listOf("M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"),
    "key" to listOf("m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4", "m21 2-9.6 9.6", circ(7.5f, 15.5f, 5.5f)),
    "power" to listOf("M12 2v10", "M18.4 6.6a9 9 0 1 1-12.77.04"),
    "trash" to listOf("M3 6h18", "M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6", "M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"),
    "edit" to listOf("M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z"),
    "info" to listOf(circ(12f, 12f, 10f), "M12 16v-4", "M12 8h.01"),
    "close" to listOf("M18 6 6 18", "m6 6 12 12"),
    "chevron" to listOf("m9 18 6-6-6-6"),
    "down" to listOf("m6 9 6 6 6-6"),
    "plus" to listOf("M5 12h14", "M12 5v14"),
    "folder" to listOf("M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z"),
    "bell" to listOf("M10.268 21a2 2 0 0 0 3.464 0", "M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"),
    "eye" to listOf("M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0", circ(12f, 12f, 3f)),
    "search" to listOf("m21 21-4.34-4.34", circ(11f, 11f, 8f)),
    "warning" to listOf("m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3", "M12 9v4", "M12 17h.01"),
    "id" to listOf(rr(2f, 5f, 20f, 14f, 2f), circ(9f, 11f, 2f), "M6.17 15a3 3 0 0 1 5.66 0", "M16 10h2", "M16 14h2"),
    "help" to listOf(circ(12f, 12f, 10f), "M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3", "M12 17h.01"),
    "wifi" to listOf("M12 20h.01", "M2 8.82a15 15 0 0 1 20 0", "M5 12.859a10 10 0 0 1 14 0", "M8.5 16.429a5 5 0 0 1 7 0"),
    "signal" to listOf("M2 20h.01", "M7 20v-4", "M12 20v-8", "M17 20V8", "M22 4v16"),
    "monitor" to listOf(rr(2f, 3f, 20f, 14f, 2f), "M8 21h8", "M12 17v4"),
    "smartphone" to listOf(rr(5f, 2f, 14f, 20f, 2f), "M12 18h.01"),
    "refresh" to listOf("M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8", "M21 3v5h-5", "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16", "M8 16H3v5"),
    "scan" to listOf("M3 7V5a2 2 0 0 1 2-2h2", "M17 3h2a2 2 0 0 1 2 2v2", "M21 17v2a2 2 0 0 1-2 2h-2", "M7 21H5a2 2 0 0 1-2-2v-2", "M7 12h10"),
    "disconnect" to listOf("m16 17 5-5-5-5", "M21 12H9", "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"),
    "keyboard" to listOf(rr(2f, 4f, 20f, 16f, 2f), "M6 8h.01", "M10 8h.01", "M14 8h.01", "M18 8h.01", "M8 12h.01", "M12 12h.01", "M16 12h.01", "M7 16h10"),
    "zoom" to listOf(circ(11f, 11f, 8f), "m21 21-4.34-4.34", "M8 11h6", "M11 8v6"),
    "reset" to listOf("M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8", "M3 3v5h5"),
    "touch" to listOf(
        "M18 11V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2", "M14 10V4a2 2 0 0 0-2-2a2 2 0 0 0-2 2v2", "M10 10.5V6a2 2 0 0 0-2-2a2 2 0 0 0-2 2v8",
        "M18 8a2 2 0 1 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.86-5.99-2.34l-3.6-3.6a2 2 0 0 1 2.83-2.82L7 15",
    ),
)

private val parsedIcons = HashMap<String, List<Path>>()

private fun pathsFor(kind: String): List<Path>? {
    val strings = StrokeIcons[kind] ?: return null
    return synchronized(parsedIcons) { parsedIcons.getOrPut(kind) { strings.map { PathParser().parsePathString(it).toPath() } } }
}

/**
 * Native stroke icons on a 24 grid, stroke 1.9, round caps and joins, no emoji, no icon library.
 * Labels belong to the controls. Directional kinds (chevron) are mirrored by the caller in RTL.
 */
@Composable
fun AppGlyph(kind: String, modifier: Modifier = Modifier, color: Color = LocalContentColor.current) {
    val paths = remember(kind) { pathsFor(kind) }
    Canvas(modifier.size(24.dp).clearAndSetSemantics { }) {
        val stroke = Stroke(StrokeWidth, cap = StrokeCap.Round, join = StrokeJoin.Round)
        withTransform({ scale(size.width / 24f, size.height / 24f, Offset.Zero) }) {
            if (paths != null) {
                paths.forEach { drawPath(it, color, style = stroke) }
            } else {
                drawLegacy(kind, color, stroke)
            }
        }
    }
}

private fun androidx.compose.ui.graphics.drawscope.DrawScope.drawLegacy(kind: String, color: Color, stroke: Stroke) {
    fun line(x: Float, y: Float, xx: Float, yy: Float) = drawLine(color, Offset(x, y), Offset(xx, yy), StrokeWidth, StrokeCap.Round)
    when (kind) {
        "settings" -> {
            val gear = Path().apply {
                for (i in 0 until 32) {
                    val angle = (i * Math.PI / 16.0 - Math.PI / 2.0)
                    val radius = if (i % 4 < 2) 9.5f else 7.6f
                    val x = 12f + cos(angle).toFloat() * radius
                    val y = 12f + sin(angle).toFloat() * radius
                    if (i == 0) moveTo(x, y) else lineTo(x, y)
                }
                close()
            }
            drawPath(gear, color, style = stroke)
            drawCircle(color, 3.2f, Offset(12f, 12f), style = stroke)
        }
        "drag" -> for (y in 0..2) for (x in 0..1) drawCircle(color, 1.1f, Offset(9f + x * 6f, 6f + y * 6f))
        "stats" -> { line(5f, 19f, 5f, 13f); line(12f, 19f, 12f, 8f); line(19f, 19f, 19f, 3f) }
        "pin" -> for (y in 0..2) for (x in 0..2) drawRoundRect(color, Offset(4f + x * 6f, 4f + y * 6f), Size(3f, 3f), CornerRadius(.6f), style = stroke)
        else -> {
            drawRoundRect(color, Offset(2f, 3f), Size(20f, 14f), CornerRadius(2f), style = stroke)
            line(8f, 21f, 16f, 21f); line(12f, 17f, 12f, 21f)
        }
    }
}

/** Accent icon tile: 34 radius 11 with 18 glyph, or 28 radius 9 with 16 glyph (contract 2.14). */
@Composable
fun RdIconTile(kind: String, modifier: Modifier = Modifier, small: Boolean = false) {
    val shape = if (small) ReyDeskShapes.TileSm else ReyDeskShapes.Tile
    Box(
        modifier.size(if (small) 28.dp else 34.dp).clip(shape)
            .background(ReyDeskColors.Tile, shape).border(1.dp, ReyDeskColors.TileLine, shape),
        contentAlignment = Alignment.Center,
    ) { AppGlyph(kind, Modifier.size(if (small) 16.dp else 18.dp), ReyDeskColors.Accent) }
}
