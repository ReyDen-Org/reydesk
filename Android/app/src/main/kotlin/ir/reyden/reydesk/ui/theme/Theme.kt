package ir.reyden.reydesk.ui.theme

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

private val C = ReyDeskColors

private val ReyDeskDarkColors = darkColorScheme(
    primary = C.AccentFill,
    onPrimary = C.OnAccent,
    primaryContainer = C.Tile,
    onPrimaryContainer = C.Accent,
    secondary = C.Accent,
    onSecondary = C.AccentInk,
    background = C.Bg,
    onBackground = C.Fg,
    surface = C.Surface,
    onSurface = C.Fg,
    surfaceVariant = C.Panel,
    onSurfaceVariant = C.Muted,
    surfaceContainerLowest = C.Ink0,
    surfaceContainerLow = C.Surface,
    surfaceContainer = C.Panel,
    surfaceContainerHigh = C.Hover,
    surfaceContainerHighest = C.Hover,
    outline = C.LineInput,
    outlineVariant = C.Line,
    error = C.Danger,
    onError = Color.White,
    errorContainer = C.DangerBg,
    onErrorContainer = C.Danger,
)

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

/**
 * ReyDesk Material3 theme. Dark only.
 * [darkTheme] is a compatibility shim for callers that still pass the old theme setting; it is ignored.
 */
@Composable
fun ReyDeskTheme(
    @Suppress("UNUSED_PARAMETER") darkTheme: Boolean = true,
    content: @Composable () -> Unit,
) {
    val language = LocalConfiguration.current.locales[0]?.language
    val rtl = language == "fa"

    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = view.context.findActivity()?.window ?: return@SideEffect
            @Suppress("DEPRECATION")
            window.statusBarColor = C.Bg.toArgb()
            @Suppress("DEPRECATION")
            window.navigationBarColor = C.Bg.toArgb()
            val controller = WindowCompat.getInsetsController(window, view)
            controller.isAppearanceLightStatusBars = false
            controller.isAppearanceLightNavigationBars = false
        }
    }

    MaterialTheme(
        colorScheme = ReyDeskDarkColors,
        typography = reyDeskTypography(rtl),
        shapes = ReyDeskMaterialShapes,
        content = content,
    )
}
