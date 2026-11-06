package ir.reyden.reydesk.webrtc

// WebRTC delay counters are cumulative seconds/counts. A missing baseline or
// reset is not an interval measurement and must be displayed as unavailable.
internal fun receiverIntervalMeanMs(total: Double?, count: Double?, previousTotal: Double?, previousCount: Double?): Double? {
    if (total == null || count == null || previousTotal == null || previousCount == null) return null
    if (!total.isFinite() || !count.isFinite() || !previousTotal.isFinite() || !previousCount.isFinite()) return null
    if (total < 0 || count < 0 || previousTotal < 0 || previousCount < 0) return null
    val emitted = count - previousCount
    val seconds = total - previousTotal
    return if (emitted > 0 && seconds >= 0) seconds * 1000 / emitted else null
}

internal fun receiverCounterRate(current: Double?, previous: Double?, seconds: Double?): Double? {
    if (current == null || previous == null || seconds == null ||
        !current.isFinite() || !previous.isFinite() || !seconds.isFinite() ||
        current < 0 || previous < 0 || seconds <= 0 || current < previous) return null
    return (current - previous) / seconds
}

// Interval packet loss is an estimate from WebRTC counters, not a cumulative
// delivery guarantee. Negative counters (duplicate-packet correction) or resets
// cannot establish zero loss and must remain unavailable.
internal fun receiverPacketLossPercent(lost: Double?, received: Double?, previousLost: Double?, previousReceived: Double?): Double? {
    val lostDelta = receiverCounterRate(lost, previousLost, 1.0) ?: return null
    val receivedDelta = receiverCounterRate(received, previousReceived, 1.0) ?: return null
    val total = lostDelta + receivedDelta
    return if (total > 0) lostDelta * 100 / total else null
}
