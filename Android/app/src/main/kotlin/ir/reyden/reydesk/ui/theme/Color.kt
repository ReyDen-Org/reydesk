package ir.reyden.reydesk.ui.theme

import androidx.compose.ui.graphics.Color

/** Single dark token set. Mirrors Windows Theme/Tokens.Dark.xaml (see docs/design-system.md). Raw hex lives only here. */
object ReyDeskColors {
    /** Compile-time ARGB of [Bg] for @Preview backgroundColor, which needs a constant. */
    const val PreviewBg: Long = 0xFF080B11
    val Ink0 = Color(0xFF06080C)
    val Bg = Color(0xFF080B11)
    val Surface = Color(0xFF0E131B)
    val Panel = Color(0xFF141A24)
    val PanelTop = Color(0xFF1A212D)
    val Hover = Color(0xFF1B2230)
    val Button = Color(0xFF171E29)
    val Line = Color(0xFF222A37)
    val LineStrong = Color(0xFF323C4D)
    val LineInput = Color(0xFF6B7486)
    val Fg = Color(0xFFF3F5F8)
    val Muted = Color(0xFFA7B0BF)
    val Faint = Color(0xFF8590A2)
    val Disabled = Color(0xFF6B7383)

    val Accent = Color(0xFFFFB020)
    val AccentFill = Color(0xFFFFB020)
    val AccentFillHover = Color(0xFFFFC04D)
    val AccentFillPressed = Color(0xFFE69A10)
    val AccentHover = Color(0xFFFFC04D)
    val AccentInk = Color(0xFF080B11)
    val OnAccent = Color(0xFF080B11)
    val AccentDim = Color(0xFF1D1912)
    val Tile = Color(0xFF2E2B24)
    val TileLine = Color(0xFF433823)
    val Selected = Color(0xFF26231C)
    val Tint = Color(0xFF352F23)
    val TintLine = Color(0xFF5F4A23)
    val TonalPressed = Color(0xFF483B23)
    val RingTrack = Color(0xFF302513)

    val Danger = Color(0xFFFF5A5F)
    val Success = Color(0xFF3DDC84)
    val Warning = Color(0xFFFF8A3D)
    val Live = Color(0xFFFF4D5E)
    val SuccessBg = Color(0xFF1A3531)
    val WarningBg = Color(0xFF352A28)
    val WarningLine = Color(0xFF5F3E2C)
    val DangerBg = Color(0xFF35232C)
    val DangerLine = Color(0xFF5F2E37)
}
