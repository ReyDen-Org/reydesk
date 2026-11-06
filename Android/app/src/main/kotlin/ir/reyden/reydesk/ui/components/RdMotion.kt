package ir.reyden.reydesk.ui.components

import android.provider.Settings
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.TweenSpec
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext

/** Motion tokens, contract 2.12. */
object RdMotion {
    const val Fast = 120
    const val Base = 200
    const val Slow = 320
    val EaseOut = CubicBezierEasing(0.22f, 1f, 0.36f, 1f)
}

/** False when the system animator duration scale is 0 (reduced motion). Loops must stay static then. */
@Composable
fun rememberAnimationsEnabled(): Boolean {
    val resolver = LocalContext.current.contentResolver
    return remember { Settings.Global.getFloat(resolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) > 0f }
}

/** Tween that collapses to zero duration under reduced motion. */
fun <T> rdTween(enabled: Boolean, ms: Int = RdMotion.Fast): TweenSpec<T> =
    tween(if (enabled) ms else 0, easing = RdMotion.EaseOut)
