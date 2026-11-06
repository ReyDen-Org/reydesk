package ir.reyden.reydesk.rendezvous

import kotlin.random.Random

/** Why the server refused authentication, independent of the protobuf enum so it can be unit tested. */
enum class AuthFailure { KeyMismatch, BadSignature, UnknownId, Revoked, RateLimited, Other }

/** What to do after a failed connection attempt (T18 H1 step 4). */
sealed class ReconnectDecision {
    /** Do not retry: the same request would fail again and only feed the server's per-IP limits. */
    data class Stop(val reason: String) : ReconnectDecision()
    /** Server forgot our Desk ID: drop it and register again at once. */
    object Reregister : ReconnectDecision()
    /** Retry after exponential backoff, but not before [minDelayMs] (server retry-after). */
    data class Retry(val minDelayMs: Long) : ReconnectDecision()
}

object ReconnectPolicy {
    /** A server cannot park the client for longer than this with a retry-after value. */
    const val MaxRetryAfterMs = 5 * 60_000L
    const val DefaultRateLimitedMs = 30_000L

    fun decide(failure: AuthFailure, retryAfterMs: Long = 0): ReconnectDecision = when (failure) {
        AuthFailure.BadSignature, AuthFailure.KeyMismatch, AuthFailure.Revoked ->
            ReconnectDecision.Stop("Rendezvous rejected this device: $failure")
        AuthFailure.UnknownId -> ReconnectDecision.Reregister
        AuthFailure.RateLimited ->
            ReconnectDecision.Retry(if (retryAfterMs > 0) retryAfterMs.coerceAtMost(MaxRetryAfterMs) else DefaultRateLimitedMs)
        AuthFailure.Other -> ReconnectDecision.Retry(retryAfterMs.coerceIn(0, MaxRetryAfterMs))
    }

    /** Wait before the next attempt: exponential with jitter capped at 60 s, never below the server's retry-after. */
    fun waitMs(attempt: Int, retryAfterMs: Long = 0, random: Random = Random.Default): Long =
        maxOf(retryAfterMs.coerceIn(0, MaxRetryAfterMs), Backoff.delayMs(attempt, random))
}
