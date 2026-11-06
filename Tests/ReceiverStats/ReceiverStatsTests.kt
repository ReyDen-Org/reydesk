import ir.reyden.reydesk.webrtc.receiverIntervalMeanMs
import ir.reyden.reydesk.webrtc.receiverCounterRate
import ir.reyden.reydesk.webrtc.receiverPacketLossPercent
import ir.reyden.reydesk.settings.StreamPreferences

fun main() {
    fun checkMean(name: String, actual: Double?, expected: Double?) {
        check(if (expected == null) actual == null else actual != null && kotlin.math.abs(actual - expected) < 0.00001) {
            "$name: expected=$expected actual=$actual"
        }
    }
    checkMean("interval rather than lifetime average", receiverIntervalMeanMs(1.9, 110.0, 1.0, 100.0), 90.0)
    checkMean("no previous sample", receiverIntervalMeanMs(1.9, 110.0, null, null), null)
    checkMean("no newly emitted frames", receiverIntervalMeanMs(1.0, 100.0, 1.0, 100.0), null)
    checkMean("counter reset", receiverIntervalMeanMs(0.1, 2.0, 1.0, 100.0), null)
    checkMean("invalid partial reset", receiverIntervalMeanMs(0.1, 110.0, 1.0, 100.0), null)
    checkMean("missing field", receiverIntervalMeanMs(null, 110.0, 1.0, 100.0), null)
    checkMean("nonfinite field", receiverIntervalMeanMs(Double.NaN, 110.0, 1.0, 100.0), null)
    checkMean("zero processing time", receiverIntervalMeanMs(1.0, 110.0, 1.0, 100.0), 0.0)
    checkMean("actual FPS interval", receiverCounterRate(160.0, 40.0, 2.0), 60.0)
    checkMean("throughput uses elapsed interval", receiverCounterRate(3000000.0, 1000000.0, 2.0)?.times(0.008), 8000.0)
    checkMean("unknown initial rate", receiverCounterRate(160.0, null, 2.0), null)
    checkMean("reset rate", receiverCounterRate(10.0, 40.0, 2.0), null)
    checkMean("real zero loss", receiverPacketLossPercent(5.0, 200.0, 5.0, 100.0), 0.0)
    checkMean("interval loss estimate", receiverPacketLossPercent(15.0, 190.0, 5.0, 100.0), 10.0)
    checkMean("negative correction is unavailable", receiverPacketLossPercent(-1.0, 200.0, 0.0, 100.0), null)
    checkMean("negative delta is unavailable", receiverPacketLossPercent(3.0, 200.0, 5.0, 100.0), null)
    checkMean("no traffic loss unavailable", receiverPacketLossPercent(5.0, 100.0, 5.0, 100.0), null)
    check(StreamPreferences.normalized(-1, 9999, Int.MAX_VALUE) == StreamPreferences()) { "invalid phone values inherit host limits" }
    check(StreamPreferences.normalized(720, 30, 4000) == StreamPreferences(720, 30, 4000)) { "phone choices persist normalized values" }
    check(StreamPreferences.normalized(0, 0, 0) == StreamPreferences()) { "phone default inherits host profile" }
    println("Receiver metrics and preferences: 20/20 PASS")
}
