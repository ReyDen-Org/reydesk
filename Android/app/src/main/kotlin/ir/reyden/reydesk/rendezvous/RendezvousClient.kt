package ir.reyden.reydesk.rendezvous

import com.google.protobuf.ByteString
import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.identity.DeviceIdentityService
import ir.reyden.reydesk.identity.IdentityPayloads
import ir.reyden.reydesk.protocol.rendezvous.v1.ClientAuth
import ir.reyden.reydesk.protocol.rendezvous.v1.AuthError
import ir.reyden.reydesk.protocol.rendezvous.v1.ErrorCode
import ir.reyden.reydesk.protocol.rendezvous.v1.IceConfigRequest
import ir.reyden.reydesk.protocol.rendezvous.v1.OpenPipe
import ir.reyden.reydesk.protocol.rendezvous.v1.Ping
import ir.reyden.reydesk.protocol.rendezvous.v1.PipeClose
import ir.reyden.reydesk.protocol.rendezvous.v1.PipeData
import ir.reyden.reydesk.protocol.rendezvous.v1.PipeDecision
import ir.reyden.reydesk.protocol.rendezvous.v1.PipeFailReason
import ir.reyden.reydesk.protocol.rendezvous.v1.PipePurpose
import ir.reyden.reydesk.protocol.rendezvous.v1.Pong
import ir.reyden.reydesk.protocol.rendezvous.v1.RendezvousFrame
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString.Companion.toByteString
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/** Signs rendezvous challenges with the device key. */
interface RendezvousIdentity {
    /** P-256 SubjectPublicKeyInfo DER. */
    val publicKey: ByteArray
    fun sign(data: ByteArray): ByteArray
}

fun DeviceIdentityService.asRendezvousIdentity(): RendezvousIdentity = object : RendezvousIdentity {
    override val publicKey: ByteArray get() = this@asRendezvousIdentity.publicKey
    override fun sign(data: ByteArray): ByteArray = this@asRendezvousIdentity.sign(data)
}

sealed class RendezvousState {
    object Stopped : RendezvousState()
    object Connecting : RendezvousState()
    data class Online(val deskId: String) : RendezvousState()
    data class Backoff(val retryInMs: Long) : RendezvousState()
    /** Not retried: key mismatch, revoked or bad signature. */
    data class Failed(val reason: String) : RendezvousState()
}

class IncomingPipeRequest(val pipeId: String, val requesterDeskId: String, val requesterFp: ByteArray, val purpose: PipePurpose)

class PipeFailedException(val reason: PipeFailReason) : IOException("Pipe failed: $reason")

/**
 * Rendezvous (signaling) client, spec docs/identity-and-pairing.md section 3. The server only
 * routes bytes: callers must run [TlsOverPipe] with the pinned host key over every [RelayPipe].
 *
 * [initialDeskId] is the persisted Desk ID (empty = not registered yet); [onDeskIdChanged] is called
 * when the server assigns or discards one (UNKNOWN_ID -> "" then a fresh registration). Persisting it is the caller's job.
 */
class RendezvousClient(
    url: String,
    private val identity: RendezvousIdentity,
    initialDeskId: String,
    private val onDeskIdChanged: (String) -> Unit,
    private val scope: CoroutineScope,
    private val appVersion: String,
    /** The URL the server binds into auth signatures (its PUBLIC_WS_URL); defaults to [url]. */
    publicWsUrl: String = url,
    /** Only for a local dev server (ws://). Release builds leave this false and need wss://. */
    allowCleartextForDev: Boolean = false,
    /** Read on every ICE decision: TURN credentials are requested and kept only while this returns true (default off). */
    private val relayAllowed: () -> Boolean = { false },
    private val platform: String = "android",
    private val http: OkHttpClient = OkHttpClient.Builder().connectTimeout(10, TimeUnit.SECONDS).build(),
) {
    /** Canonical form (RendezvousUrl.normalize): connected to, and signed into the auth payload. */
    private val url: String = RendezvousUrl.normalize(url, allowCleartextForDev)
        ?: throw IllegalArgumentException("Rendezvous URL must be wss://.")
    private val signedUrl: String = RendezvousUrl.normalize(publicWsUrl, allowCleartextForDev)
        ?: throw IllegalArgumentException("Rendezvous URL must be wss://.")

    private val _state = MutableStateFlow<RendezvousState>(RendezvousState.Stopped)
    val state: StateFlow<RendezvousState> = _state.asStateFlow()

    private val _ice = MutableStateFlow<IceConfigState?>(null)
    /** Latest ICE servers. STUN only unless relay is allowed; null until the first IceConfig after auth. */
    val iceConfig: StateFlow<IceConfigState?> = _ice.asStateFlow()

    private val _incoming = MutableSharedFlow<IncomingPipeRequest>(extraBufferCapacity = 8)
    val incomingPipes: SharedFlow<IncomingPipeRequest> = _incoming.asSharedFlow()

    @Volatile var deskId: String = initialDeskId
        private set

    private val seq = AtomicLong(0)
    private val current = AtomicReference<Conn?>(null)
    private val openMutex = Mutex()
    private var loop: Job? = null

    init {
        require(initialDeskId.isEmpty() || DeskIdCodec.check(initialDeskId)) { "Invalid saved Desk ID." }
    }

    @Synchronized fun start() {
        if (loop?.isActive == true) return
        loop = scope.launch {
            var attempt = 0
            while (true) {
                _state.value = RendezvousState.Connecting
                val startedAt = System.currentTimeMillis()
                val result = try { connectOnce() } catch (e: CancellationException) { throw e } catch (e: Exception) {
                    ConnResult(fatal = null, retryAfterMs = 0)
                }
                if (result.fatal != null) { _state.value = RendezvousState.Failed(result.fatal); return@launch }
                if (System.currentTimeMillis() - startedAt >= Backoff.ResetAfterMs) attempt = 0
                val wait = if (result.immediate) 0L else ReconnectPolicy.waitMs(attempt, result.retryAfterMs)
                if (!result.immediate) attempt++
                _state.value = RendezvousState.Backoff(wait)
                delay(wait)
            }
        }
    }

    @Synchronized fun stop() {
        loop?.cancel()
        loop = null
        current.getAndSet(null)?.shutdown("client stopping")
        _state.value = RendezvousState.Stopped
    }

    /**
     * Ask the server for TURN credentials (answered with an IceConfig frame). A no-op while relay is not
     * allowed: the credentials are never requested then (T18 H2).
     */
    fun requestIceConfig() {
        if (!relayAllowed()) return
        current.get()?.takeIf { it.authed }?.send { it.setIceConfigRequest(IceConfigRequest.getDefaultInstance()) }
    }

    private var lastIceRequestMs = 0L

    /**
     * ICE config for a remote session. Relay off: the STUN-only config from the initial IceConfig, no request.
     * Relay on: asks for TURN credentials when none are valid and waits briefly for the answer; falls back to
     * whatever is held (STUN) when the server does not answer or has relay disabled.
     */
    suspend fun iceConfigForSession(waitMs: Long = 8_000, nowMs: () -> Long = System::currentTimeMillis): IceConfigState? {
        if (!relayAllowed()) return _ice.value
        val held = _ice.value
        if (held?.hasUsableTurn(nowMs()) == true) return held
        if (nowMs() - lastIceRequestMs < IceRequestMinGapMs) return held
        lastIceRequestMs = nowMs()
        requestIceConfig()
        return try { withTimeout(waitMs) { _ice.first { it !== held } } } catch (_: kotlinx.coroutines.TimeoutCancellationException) { held }
    }

    /** Opens a pipe to [targetDeskId] and waits for the server to report it open (host decision + mailbox wait included). */
    suspend fun openPipe(targetDeskId: String, purpose: PipePurpose, code: String = ""): RelayPipe {
        val canonical = (DeskIdCodec.parse(targetDeskId) as? DeskIdCodec.Result.Ok)?.value
            ?: throw IllegalArgumentException("Invalid Desk ID.")
        return openMutex.withLock {
            val conn = current.get()?.takeIf { it.authed } ?: throw IllegalStateException("Not connected to the rendezvous server.")
            val pending = CompletableDeferred<RelayPipe>()
            conn.pendingOpen.set(pending)
            try {
                conn.send { it.setOpenPipe(OpenPipe.newBuilder().setTargetDeskId(canonical).setPurpose(purpose).setCode(code)) }
                Log.i(LogTag, "openPipe $purpose sent")
                // A timeout is a failed open, not a cancellation: withTimeout's CancellationException left the UI stuck on "Connecting".
                (withTimeoutOrNull(OpenPipeTimeoutMs) { pending.await() } ?: throw PipeFailedException(PipeFailReason.PIPE_FAIL_REASON_TIMEOUT))
                    .also { Log.i(LogTag, "openPipe $purpose opened") }
            } catch (e: PipeFailedException) {
                Log.w(LogTag, "openPipe $purpose failed: ${e.reason}")
                throw e
            } finally {
                conn.pendingOpen.compareAndSet(pending, null)
            }
        }
    }

    /** Accepts an [IncomingPipeRequest]; the host decision window is 10 s. */
    suspend fun acceptPipe(request: IncomingPipeRequest): RelayPipe {
        val conn = current.get()?.takeIf { it.authed } ?: throw IllegalStateException("Not connected to the rendezvous server.")
        val pending = CompletableDeferred<RelayPipe>()
        conn.pendingAccept[request.pipeId] = pending
        try {
            conn.send { it.setPipeDecision(PipeDecision.newBuilder().setPipeId(request.pipeId).setAccept(true)) }
            return withTimeout(OpenPipeTimeoutMs) { pending.await() }
        } finally {
            conn.pendingAccept.remove(request.pipeId, pending)
        }
    }

    fun declinePipe(request: IncomingPipeRequest) {
        current.get()?.takeIf { it.authed }?.send { it.setPipeDecision(PipeDecision.newBuilder().setPipeId(request.pipeId).setAccept(false)) }
    }

    private class ConnResult(val fatal: String?, val retryAfterMs: Long, val immediate: Boolean = false)

    private suspend fun connectOnce(): ConnResult {
        val conn = Conn()
        current.set(conn)
        val ws = http.newWebSocket(Request.Builder().url(url).build(), conn)
        conn.ws = ws
        val watchdog = scope.launch {
            delay(AuthDeadlineMs)
            if (!conn.authed) conn.shutdown("auth timeout")
            while (true) {
                delay(PingEveryMs)
                if (System.currentTimeMillis() - conn.lastRxMs > SilenceCloseMs) { conn.shutdown("server silent"); break }
                conn.send { it.setPing(Ping.getDefaultInstance()) }
            }
        }
        try {
            conn.closed.await()
        } finally {
            watchdog.cancel()
            conn.shutdown("closing")
            current.compareAndSet(conn, null)
            conn.failAll()
        }
        val retryAfter = if (conn.retryAfterMs > 0) conn.retryAfterMs
            else if (conn.rateLimited) ReconnectPolicy.DefaultRateLimitedMs else 0L
        return ConnResult(conn.fatal, retryAfter, conn.reregister)
    }

    private inner class Conn : WebSocketListener() {
        lateinit var ws: WebSocket
        val closed = CompletableDeferred<Unit>()
        @Volatile var authed = false
        @Volatile var lastRxMs = System.currentTimeMillis()
        @Volatile var fatal: String? = null
        @Volatile var retryAfterMs = 0L
        @Volatile var reregister = false
        @Volatile var rateLimited = false
        private var helloSeen = false
        private var sentDeskId = ""
        val pendingOpen = AtomicReference<CompletableDeferred<RelayPipe>?>(null)
        val pendingAccept = ConcurrentHashMap<String, CompletableDeferred<RelayPipe>>()
        val pipes = ConcurrentHashMap<String, RelayPipe>()

        fun send(build: (RendezvousFrame.Builder) -> Unit) {
            val b = RendezvousFrame.newBuilder().setSeq(seq.incrementAndGet())
            build(b)
            ws.send(b.build().toByteArray().toByteString())
        }

        fun shutdown(reason: String) {
            runCatching { ws.close(1000, reason) }
            closed.complete(Unit)
        }

        fun failAll() {
            val e = IOException("Rendezvous connection closed")
            pendingOpen.getAndSet(null)?.completeExceptionally(e)
            pendingAccept.values.forEach { it.completeExceptionally(e) }
            pendingAccept.clear()
            pipes.values.forEach { it.remoteClosed() } // pipes do not survive a reconnect
            pipes.clear()
        }

        private fun badPeer(reason: String) {
            shutdown(reason)
        }

        override fun onMessage(webSocket: WebSocket, bytes: okio.ByteString) {
            lastRxMs = System.currentTimeMillis()
            if (bytes.size > MaxFrameBytes) return badPeer("oversized frame")
            val frame = try { RendezvousFrame.parseFrom(bytes.toByteArray()) } catch (_: Exception) { return badPeer("bad frame") }
            try {
                handle(frame)
            } catch (e: Exception) {
                badPeer("frame handling failed")
            }
        }

        private fun handle(f: RendezvousFrame) {
            if (f.msgCase == RendezvousFrame.MsgCase.ERROR && !authed) {
                // Rate limited before or after the auth verdict: honour retry-after, then wait for the close.
                retryAfterMs = maxOf(retryAfterMs, f.error.retryAfterMs.toLong())
                if (!helloSeen || f.error.code == ErrorCode.ERROR_CODE_RATE_LIMITED) {
                    rateLimited = true
                    return shutdown("server error")
                }
                return
            }
            if (!helloSeen) {
                if (f.msgCase != RendezvousFrame.MsgCase.SERVER_HELLO) return badPeer("expected ServerHello")
                helloSeen = true
                val hello = f.serverHello
                if (hello.protocol != 1 || hello.nonce.size() != 32) return badPeer("bad ServerHello")
                sentDeskId = deskId
                val spki = identity.publicKey
                val sig = identity.sign(IdentityPayloads.rendezvousAuth(hello.nonce.toByteArray(), signedUrl, spki, sentDeskId))
                send {
                    it.setClientAuth(ClientAuth.newBuilder().setSpki(ByteString.copyFrom(spki)).setDeskId(sentDeskId)
                        .setPlatform(platform).setAppVersion(appVersion).setSig(ByteString.copyFrom(sig)))
                }
                return
            }
            if (!authed) {
                if (f.msgCase != RendezvousFrame.MsgCase.AUTH_RESULT) return badPeer("expected AuthResult")
                val r = f.authResult
                if (!r.ok) {
                    val failure = when (r.error) {
                        AuthError.AUTH_ERROR_KEY_MISMATCH -> AuthFailure.KeyMismatch
                        AuthError.AUTH_ERROR_BAD_SIGNATURE -> AuthFailure.BadSignature
                        AuthError.AUTH_ERROR_UNKNOWN_ID -> AuthFailure.UnknownId
                        AuthError.AUTH_ERROR_REVOKED -> AuthFailure.Revoked
                        AuthError.AUTH_ERROR_RATE_LIMITED -> AuthFailure.RateLimited
                        else -> AuthFailure.Other
                    }
                    when (val d = ReconnectPolicy.decide(failure, retryAfterMs)) {
                        is ReconnectDecision.Stop -> fatal = d.reason
                        ReconnectDecision.Reregister -> {
                            // Server lost this ID: discard it and register a new one on the next connect.
                            deskId = ""
                            onDeskIdChanged("")
                            reregister = true
                        }
                        is ReconnectDecision.Retry -> if (failure == AuthFailure.RateLimited) rateLimited = true
                    }
                    // The server follows a rate-limited AuthResult with an Error frame carrying retry-after, then
                    // closes; keep listening for it (the auth deadline closes us if it never comes).
                    if (failure != AuthFailure.RateLimited) shutdown("auth failed")
                    return
                }
                val assigned = (DeskIdCodec.parse(r.deskId) as? DeskIdCodec.Result.Ok)?.value
                if (assigned == null || (sentDeskId.isNotEmpty() && assigned != sentDeskId)) return badPeer("bad Desk ID in AuthResult")
                if (assigned != deskId) { deskId = assigned; onDeskIdChanged(assigned) }
                authed = true
                _state.value = RendezvousState.Online(assigned)
                return
            }
            when (f.msgCase) {
                RendezvousFrame.MsgCase.ICE_CONFIG -> IceConfigState.from(f.iceConfig).let { _ice.value = if (relayAllowed()) it else it.withoutTurn() }
                RendezvousFrame.MsgCase.INCOMING_PIPE -> {
                    val p = f.incomingPipe
                    if (p.pipeId.isNotBlank()) _incoming.tryEmit(IncomingPipeRequest(p.pipeId, p.requesterDeskId, p.requesterFp.toByteArray(), p.purpose))
                }
                RendezvousFrame.MsgCase.PIPE_OPENED -> {
                    val o = f.pipeOpened
                    val waiter = pendingAccept.remove(o.pipeId) ?: pendingOpen.getAndSet(null) ?: return
                    val pipe = newPipe(o.pipeId, o.peerSpki.toByteArray())
                    pipes[o.pipeId] = pipe
                    waiter.complete(pipe)
                }
                RendezvousFrame.MsgCase.PIPE_FAILED -> {
                    val x = f.pipeFailed
                    val waiter = pendingAccept.remove(x.pipeId) ?: pendingOpen.getAndSet(null)
                    waiter?.completeExceptionally(PipeFailedException(x.reason))
                }
                RendezvousFrame.MsgCase.PIPE_DATA -> {
                    val d = f.pipeData
                    if (d.data.size() <= RelayPipe.MaxChunk) pipes[d.pipeId]?.deliver(d.data.toByteArray())
                }
                RendezvousFrame.MsgCase.PIPE_CLOSE -> pipes.remove(f.pipeClose.pipeId)?.remoteClosed()
                RendezvousFrame.MsgCase.PING -> send { it.setPong(Pong.getDefaultInstance()) }
                RendezvousFrame.MsgCase.ERROR -> retryAfterMs = maxOf(retryAfterMs, f.error.retryAfterMs.toLong())
                else -> {}
            }
        }

        private fun newPipe(id: String, peerSpki: ByteArray): RelayPipe = RelayPipe(id, peerSpki,
            sender = { chunk ->
                var waited = 0
                while (ws.queueSize() > MaxQueuedBytes) { // backpressure towards the server's pipe cap
                    if (closed.isCompleted || waited > 30_000) throw IOException("Rendezvous connection stalled")
                    Thread.sleep(10); waited += 10
                }
                if (closed.isCompleted) throw IOException("Rendezvous connection closed")
                send { it.setPipeData(PipeData.newBuilder().setPipeId(id).setData(ByteString.copyFrom(chunk))) }
            },
            onLocalClose = {
                pipes.remove(id)
                if (!closed.isCompleted) send { it.setPipeClose(PipeClose.newBuilder().setPipeId(id)) }
            })

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(1000, null); closed.complete(Unit) }
        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) { closed.complete(Unit) }
        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) { closed.complete(Unit) }
    }

    private companion object {
        const val AuthDeadlineMs = 10_000L
        const val PingEveryMs = 25_000L
        const val SilenceCloseMs = 60_000L
        const val OpenPipeTimeoutMs = 35_000L
        /** logcat tag: pipe purpose and outcome only, never codes, keys or Desk IDs. */
        const val LogTag = "ReyDeskRendezvous"
        const val IceRequestMinGapMs = 10_000L
        const val MaxFrameBytes = 32 * 1024
        const val MaxQueuedBytes = 256L * 1024
    }
}
