package ir.reyden.reydesk.settings

/** Zero means host default. The host still enforces its own upper limits. */
data class StreamPreferences(val maxHeight: Int = 0, val maxFps: Int = 0, val bitrateKbps: Int = 0, val preset: String = "Custom") {
    companion object {
        val presets = listOf("Auto", "Performance", "Balanced", "Quality", "Custom")
        fun forPreset(name: String): StreamPreferences = when (name) {
            "Performance" -> StreamPreferences(720, 30, 4000, name)
            "Balanced" -> StreamPreferences(1080, 30, 8000, name)
            "Quality", "Auto" -> StreamPreferences(1080, 60, 12000, name)
            else -> StreamPreferences()
        }
        fun normalized(height: Int, fps: Int, bitrate: Int, preset: String = "Custom") = StreamPreferences(
            height.takeIf { it in setOf(0, 540, 720, 1080, 1440, 2160) } ?: 0,
            fps.takeIf { it in setOf(0, 15, 30, 60) } ?: 0,
            bitrate.takeIf { it in setOf(0, 4000, 8000, 12000, 20000, 30000, 50000) } ?: 0,
            preset.takeIf { it in presets } ?: "Custom",
        )
    }
}
