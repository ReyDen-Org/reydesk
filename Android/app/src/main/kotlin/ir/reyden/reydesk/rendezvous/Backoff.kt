package ir.reyden.reydesk.rendezvous

import kotlin.random.Random

/** Reconnect delays 1, 2, 4 ... 60 s with +-20% jitter, never above 60 s (spec 3.5). */
object Backoff {
    const val MaxMs = 60_000L

    /** A connection that stayed up this long resets the attempt counter. */
    const val ResetAfterMs = 5 * 60_000L

    fun delayMs(attempt: Int, random: Random = Random.Default): Long {
        val base = minOf(MaxMs, 1000L shl attempt.coerceIn(0, 6))
        val jitter = 0.8 + random.nextDouble() * 0.4
        return minOf(MaxMs, (base * jitter).toLong())
    }
}
