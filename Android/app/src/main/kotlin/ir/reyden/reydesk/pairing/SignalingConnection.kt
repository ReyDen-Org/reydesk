package ir.reyden.reydesk.pairing

import ir.reyden.reydesk.protocol.v1.Envelope
import ir.reyden.reydesk.protocol.v1.IceCandidate
import ir.reyden.reydesk.protocol.v1.SessionAnswer
import ir.reyden.reydesk.protocol.v1.SessionOffer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import ir.reyden.reydesk.protocol.v1.ConnectionMedium
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * One long-lived TCP connection to the paired Windows host - carries the pairing
 * handshake (PairingClient.kt) and, once paired, WebRTC SDP/ICE signaling
 * (webrtc/WebRtcSession.kt), all as length-delimited Envelope messages on the same
 * socket (see docs/protocol.md "Pairing & signaling transport"). Mirrors
 * Windows/ReyDesk.Windows/Signaling/SignalingConnection.cs.
 */
class SignalingConnection(
    private val input: InputStream,
    private val output: OutputStream,
    private val closer: () -> Unit,
    /** LOCAL for the pinned-TLS LAN socket, REMOTE for pinned TLS over a rendezvous pipe. Selects the ICE policy. */
    val medium: ConnectionMedium = ConnectionMedium.CONNECTION_MEDIUM_LOCAL,
) {
    /** LAN socket form, unchanged behaviour. */
    constructor(socket: Socket) : this(socket.getInputStream(), socket.getOutputStream(), { socket.close() })

    /** Any authenticated pairing stream (LAN socket or relay pipe). */
    constructor(stream: PairedStream, medium: ConnectionMedium) : this(stream.input, stream.output, { stream.close() }, medium)

    private val writeLock = Any()
    private var readJob: Job? = null
    private val closed = AtomicBoolean(false)

    var onSessionOffer: ((SessionOffer) -> Unit)? = null
    var onSessionAnswer: ((SessionAnswer) -> Unit)? = null
    var onIceCandidate: ((IceCandidate) -> Unit)? = null
    /** Raised once, when the read loop ends for any reason (peer disconnected, etc.). */
    var onClosed: (() -> Unit)? = null

    fun startReadLoop(scope: CoroutineScope) {
        if (closed.get() || readJob != null) return
        readJob = scope.launch(Dispatchers.IO) {
            try {
                while (isActive) {
                    // parseDelimitedFrom returns null on a clean EOF (Java lite runtime) -
                    // any other broken/truncated read throws, caught below either way.
                    val envelope = EnvelopeFraming.read(input) ?: break
                    dispatch(envelope)
                }
            } catch (_: Exception) {
                // Connection dropped/malformed data - not worth crashing over, just close.
            } finally {
                close()
            }
        }
    }

    private fun dispatch(envelope: Envelope) {
        when (envelope.payloadCase) {
            Envelope.PayloadCase.PAIRING_REQUEST, Envelope.PayloadCase.PAIRING_RESPONSE ->
                throw java.io.IOException("Pairing cannot repeat on an authenticated connection")
            Envelope.PayloadCase.SESSION_OFFER -> onSessionOffer?.invoke(envelope.sessionOffer)
            Envelope.PayloadCase.SESSION_ANSWER -> onSessionAnswer?.invoke(envelope.sessionAnswer)
            Envelope.PayloadCase.ICE_CANDIDATE -> onIceCandidate?.invoke(envelope.iceCandidate)
            else -> {}
        }
    }

    /** Writes one Envelope to the socket. Safe to call from any thread. */
    fun send(envelope: Envelope) {
        synchronized(writeLock) {
            if (closed.get()) return
            EnvelopeFraming.write(output, envelope)
        }
    }

    fun close() {
        if (!closed.compareAndSet(false, true)) return
        readJob?.cancel()
        try { closer() } catch (_: Exception) { /* already closed */ }
        onClosed?.invoke()
    }
}
