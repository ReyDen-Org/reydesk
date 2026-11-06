import ir.reyden.reydesk.rendezvous.AuthFailure
import ir.reyden.reydesk.rendezvous.Backoff
import ir.reyden.reydesk.rendezvous.ReconnectDecision
import ir.reyden.reydesk.rendezvous.ReconnectPolicy
import ir.reyden.reydesk.rendezvous.RendezvousUrl
import kotlin.random.Random

/** T18 H1 steps 3-4 on Android: URL normalisation and the reconnect decisions (pure, no protobuf needed). */
fun policyTests(check: (String, Boolean) -> Unit) {
    fun n(s: String?, dev: Boolean = false) = RendezvousUrl.normalize(s, dev) ?: "<invalid>"

    check("url: built-in default is canonical", n("wss://rdv.reyden.ir/v1/ws") == "wss://rdv.reyden.ir/v1/ws" && n("WSS://Rdv.Reyden.IR:443/v1/ws/") == "wss://rdv.reyden.ir/v1/ws")
    check("url: lowercase scheme and host", n("WSS://Rendezvous.Example.COM/v1/ws") == "wss://rendezvous.example.com/v1/ws")
    check("url: trim and trailing slash", n("  wss://host.example/v1/ws/  ") == "wss://host.example/v1/ws")
    check("url: bare host slash", n("wss://host.example/") == "wss://host.example")
    check("url: default port dropped", n("wss://host.example:443/v1/ws") == "wss://host.example/v1/ws")
    check("url: other port kept", n("wss://host.example:8443/v1/ws") == "wss://host.example:8443/v1/ws")
    check("url: variants collapse to one", n("wss://HOST.example:443/v1/ws/") == n("wss://host.example/v1/ws"))
    check("url: path case kept", n("wss://host.example/V1/Ws") == "wss://host.example/V1/Ws")
    check("url: ws rejected for public host even in dev", n("ws://host.example/v1/ws", true) == "<invalid>")
    check("url: ws rejected when dev off", n("ws://127.0.0.1:13260/v1/ws") == "<invalid>")
    for (host in listOf("127.0.0.1:13260", "localhost:1", "10.0.2.2:13260", "pc.local"))
        check("url: dev ws accepted for $host", n("WS://$host/v1/ws/", true) == "ws://$host/v1/ws")
    check("url: http rejected", n("https://host.example/v1/ws", true) == "<invalid>")
    check("url: no scheme rejected", n("host.example/v1/ws") == "<invalid>")
    check("url: empty rejected", n("  ") == "<invalid>" && n(null) == "<invalid>")
    check("url: credentials rejected", n("wss://user:pw@host.example/v1/ws") == "<invalid>")
    check("url: query and fragment rejected", n("wss://host.example/v1/ws?x=1") == "<invalid>" && n("wss://host.example/v1/ws#a") == "<invalid>")
    check("url: inner whitespace rejected", n("wss://host .example/v1/ws") == "<invalid>")

    check("policy: BAD_SIGNATURE stops", ReconnectPolicy.decide(AuthFailure.BadSignature) is ReconnectDecision.Stop)
    check("policy: KEY_MISMATCH stops", ReconnectPolicy.decide(AuthFailure.KeyMismatch) is ReconnectDecision.Stop)
    check("policy: UNKNOWN_ID re-registers", ReconnectPolicy.decide(AuthFailure.UnknownId) == ReconnectDecision.Reregister)
    check("policy: RATE_LIMITED retries with a default floor",
        (ReconnectPolicy.decide(AuthFailure.RateLimited) as? ReconnectDecision.Retry)?.minDelayMs == ReconnectPolicy.DefaultRateLimitedMs)
    check("policy: RATE_LIMITED honours retry-after",
        (ReconnectPolicy.decide(AuthFailure.RateLimited, 12_000) as? ReconnectDecision.Retry)?.minDelayMs == 12_000L)
    check("policy: absurd retry-after is capped",
        (ReconnectPolicy.decide(AuthFailure.RateLimited, 3_600_000) as? ReconnectDecision.Retry)?.minDelayMs == ReconnectPolicy.MaxRetryAfterMs)

    val rnd = Random(7)
    check("backoff never above 60 s, even with jitter", (0..200).all { Backoff.delayMs(it % 12, rnd) <= 60_000L })
    check("backoff first 800..1200", (0..50).all { Backoff.delayMs(0, rnd) in 800..1200 })
    check("wait: retry-after is a floor", ReconnectPolicy.waitMs(0, 20_000, rnd) == 20_000L)
    check("wait: larger backoff wins over a small retry-after", ReconnectPolicy.waitMs(6, 2_000, rnd) in 48_000..60_000)
    check("wait: no retry-after is plain backoff", ReconnectPolicy.waitMs(2, 0, rnd) in 3_200..4_800)
}
