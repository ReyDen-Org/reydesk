package ir.reyden.reydesk.webrtc

/** Actual receive-path measurements; null is unavailable, not measured zero. */
data class ConnectionMetrics(
    val fps: Double? = null,
    val bitrateKbps: Double? = null,
    val roundTripTimeMs: Double? = null,
    val packetLossPercent: Double? = null,
    val jitterMs: Double? = null,
    val width: Int = 0,
    val height: Int = 0,
    val framesDropped: Long = 0,
)
