package ir.reyden.reydesk.rendezvous

import java.io.Closeable
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.security.SecureRandom
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLEngineResult
import javax.net.ssl.SSLEngineResult.HandshakeStatus
import javax.net.ssl.SSLEngineResult.Status
import javax.net.ssl.SSLException
import javax.net.ssl.TrustManager

/**
 * TLS client over any raw duplex byte stream (a [RelayPipe]) using SSLEngine. The host key is
 * pinned with [PinnedHostKeyTrustManager], the same exact-SPKI check as the LAN SSLSocket path.
 * There is no fallback to system trust: the only TrustManager installed is the pinning one.
 */
object TlsOverPipe {
    /** Blocking handshake; throws (SSLException) when the host certificate key is not [hostSpki]. */
    fun connect(pipe: RelayPipe, hostSpki: ByteArray): TlsPipeStream =
        connect(pipe.input, pipe.output, hostSpki, pipe::close)

    fun connect(rawIn: InputStream, rawOut: OutputStream, hostSpki: ByteArray, onClose: () -> Unit = {}): TlsPipeStream {
        val context = SSLContext.getInstance("TLS").apply {
            init(null, arrayOf<TrustManager>(PinnedHostKeyTrustManager(hostSpki)), SecureRandom())
        }
        val engine = context.createSSLEngine().apply {
            useClientMode = true
            enabledProtocols = supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
        }
        val stream = TlsPipeStream(engine, rawIn, rawOut, onClose)
        try {
            stream.handshake()
        } catch (e: Exception) {
            stream.close()
            throw e
        }
        return stream
    }
}

/** Established TLS session exposing plain [input]/[output] like an SSLSocket would. */
class TlsPipeStream internal constructor(
    private val engine: SSLEngine,
    private val rawIn: InputStream,
    private val rawOut: OutputStream,
    private val onClose: () -> Unit,
) : Closeable {
    private val readLock = Any()
    private val writeLock = Any()
    private var netIn = ByteBuffer.allocate(engine.session.packetBufferSize) // write mode between calls
    private val netOut = ByteBuffer.allocate(engine.session.packetBufferSize)
    private val appIn = ByteBuffer.allocate(engine.session.applicationBufferSize) // write mode between calls
    @Volatile private var closed = false

    val input: InputStream = object : InputStream() {
        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) < 0) -1 else one[0].toInt() and 0xFF
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int = readPlain(b, off, len)
    }

    val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)
        override fun write(b: ByteArray, off: Int, len: Int) = writePlain(b, off, len)
    }

    internal fun handshake() {
        synchronized(readLock) {
            engine.beginHandshake()
            var hs = engine.handshakeStatus
            while (hs != HandshakeStatus.FINISHED && hs != HandshakeStatus.NOT_HANDSHAKING) {
                hs = when (hs) {
                    HandshakeStatus.NEED_TASK -> { runTasks(); engine.handshakeStatus }
                    HandshakeStatus.NEED_WRAP -> wrapOnce(Empty()).handshakeStatus
                    HandshakeStatus.NEED_UNWRAP -> {
                        val r = unwrapOnce() ?: throw EOFException("Pipe closed during TLS handshake")
                        r.handshakeStatus
                    }
                    else -> engine.handshakeStatus
                }
            }
        }
    }

    private fun runTasks() {
        while (true) { (engine.delegatedTask ?: break).run() }
    }

    /** Wraps [src] into netOut and writes the record to the pipe. */
    private fun wrapOnce(src: ByteBuffer): SSLEngineResult = synchronized(writeLock) {
        netOut.clear()
        val r = engine.wrap(src, netOut)
        if (r.status == Status.BUFFER_OVERFLOW) throw SSLException("TLS output buffer too small")
        netOut.flip()
        if (netOut.hasRemaining()) {
            rawOut.write(netOut.array(), netOut.arrayOffset() + netOut.position(), netOut.remaining())
            rawOut.flush()
        }
        r
    }

    /** One unwrap into appIn, reading more ciphertext as needed. Null at end of pipe. Hold readLock. */
    private fun unwrapOnce(): SSLEngineResult? {
        while (true) {
            netIn.flip()
            val r = if (netIn.hasRemaining()) engine.unwrap(netIn, appIn)
            else SSLEngineResult(Status.BUFFER_UNDERFLOW, engine.handshakeStatus, 0, 0)
            netIn.compact()
            if (r.status != Status.BUFFER_UNDERFLOW) return r
            if (!netIn.hasRemaining()) {
                val bigger = ByteBuffer.allocate(netIn.capacity() * 2)
                netIn.flip(); bigger.put(netIn); netIn = bigger
            }
            val n = rawIn.read(netIn.array(), netIn.arrayOffset() + netIn.position(), netIn.remaining())
            if (n < 0) return null
            netIn.position(netIn.position() + n)
        }
    }

    private fun readPlain(b: ByteArray, off: Int, len: Int): Int {
        if (len == 0) return 0
        synchronized(readLock) {
            while (true) {
                if (appIn.position() > 0) {
                    appIn.flip()
                    val n = minOf(len, appIn.remaining())
                    appIn.get(b, off, n)
                    appIn.compact()
                    return n
                }
                if (closed) return -1
                val r = unwrapOnce() ?: return -1
                if (r.status == Status.BUFFER_OVERFLOW) throw SSLException("TLS input buffer too small")
                when (r.handshakeStatus) {
                    HandshakeStatus.NEED_TASK -> runTasks()
                    HandshakeStatus.NEED_WRAP ->
                        while (engine.handshakeStatus == HandshakeStatus.NEED_WRAP) wrapOnce(Empty())
                    else -> {}
                }
                if (r.status == Status.CLOSED && appIn.position() == 0) return -1
            }
        }
    }

    private fun writePlain(b: ByteArray, off: Int, len: Int) {
        if (closed) throw IOException("TLS stream closed")
        val src = ByteBuffer.wrap(b, off, len)
        while (src.hasRemaining()) {
            if (wrapOnce(src).status == Status.CLOSED) throw IOException("TLS stream closed")
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        try {
            engine.closeOutbound()
            while (engine.handshakeStatus == HandshakeStatus.NEED_WRAP) wrapOnce(Empty())
        } catch (_: IOException) {
            // Best effort close_notify: the pipe may already be gone; it is closed below either way.
        }
        onClose()
    }

    private fun Empty(): ByteBuffer = ByteBuffer.allocate(0)
}
