package ir.reyden.reydesk.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import ir.reyden.reydesk.R

val Vazirmatn = FontFamily(
    Font(R.font.vazirmatn_regular, FontWeight.W400),
    Font(R.font.vazirmatn_medium, FontWeight.W500),
    Font(R.font.vazirmatn_semibold, FontWeight.W600),
    Font(R.font.vazirmatn_bold, FontWeight.W700),
)

val Inter = FontFamily(
    Font(R.font.inter_regular, FontWeight.W400),
    Font(R.font.inter_medium, FontWeight.W500),
    Font(R.font.inter_semibold, FontWeight.W600),
    Font(R.font.inter_bold, FontWeight.W700),
)

val JetBrainsMono = FontFamily(
    Font(R.font.jetbrains_mono_regular, FontWeight.W400),
    Font(R.font.jetbrains_mono_medium, FontWeight.W500),
    Font(R.font.jetbrains_mono_bold, FontWeight.W700),
)

/** Mono styles are never locale-switched. */
object ReyDeskType {
    val Id = TextStyle(fontFamily = JetBrainsMono, fontWeight = FontWeight.Bold, fontSize = 22.sp, lineHeight = 28.sp, letterSpacing = 1.sp)
    val Sas = TextStyle(fontFamily = JetBrainsMono, fontWeight = FontWeight.Bold, fontSize = 36.sp, lineHeight = 44.sp)
    val Mono = TextStyle(fontFamily = JetBrainsMono, fontWeight = FontWeight.Normal, fontSize = 13.sp, lineHeight = 18.sp)
}

/** Vazirmatn for FA/AR (rtl = true, line height x1.15), Inter otherwise. */
fun reyDeskTypography(rtl: Boolean): Typography {
    val family = if (rtl) Vazirmatn else Inter
    val k = if (rtl) 1.15f else 1f
    fun style(size: Int, line: Int, weight: FontWeight) = TextStyle(
        fontFamily = family,
        fontWeight = weight,
        fontSize = size.sp,
        lineHeight = (line * k).sp,
    )
    return Typography(
        headlineMedium = style(24, 32, FontWeight.Bold),
        titleLarge = style(19, 26, FontWeight.Bold),
        titleMedium = style(15, 22, FontWeight.SemiBold),
        bodyLarge = style(15, 24, FontWeight.Normal),
        bodyMedium = style(14, 22, FontWeight.Normal),
        bodySmall = style(13, 20, FontWeight.Normal),
        labelLarge = style(14, 20, FontWeight.Medium),
        labelMedium = style(12, 16, FontWeight.Medium),
        labelSmall = style(11, 14, FontWeight.Medium),
    )
}

/** Compatibility shim for older callers; prefer reyDeskTypography(rtl). */
val ReyDeskTypography: Typography = reyDeskTypography(rtl = false)
