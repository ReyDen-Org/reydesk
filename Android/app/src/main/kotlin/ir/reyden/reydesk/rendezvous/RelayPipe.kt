package ir.reyden.reydesk.rendezvous

import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean

/**
 * One server-relayed pipe as a duplex byte stream. Carries only TLS ciphertext once wrapped by
 * [TlsOverPipe]; [peerSpki] is routing metadata from the server and is never trusted.
 */
class RelayPipe internal constructor(
    val pipeId: String,
    val peerSpki: ByteArray,
    /** Sends one chunk (<= [MaxChunk]); may block for backpressure, throws IOException when dead. */
    private val sender: (ByteArray) -> Unit,
    private val onLocalClose: () -> Unit,
) {
    private val queue = LinkedBlockingQueue<ByteArray>()
    private val closed = AtomicBoolean(false)
    private val endOfStream = ByteArray(0)

    val input: InputStream = object : InputStream() {
        private var cur: ByteArray? = null
        private var pos = 0
        private var eof = false

        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) < 0) -1 else one[0].toInt() and 0xFF
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (len == 0) return 0
            if (eof) return -1
            var c = cur
            if (c == null || pos >= c.size) {
                val next = try {
                    queue.take()
                } catch (e: InterruptedException) {
                    Thread.currentThread().interrupt()
                    throw IOException("Interrupted", e)
                }
                if (next === endOfStream) { eof = true; queue.offer(endOfStream); return -1 }
                c = next; cur = c; pos = 0
            }
            val n = minOf(len, c.size - pos)
            System.arraycopy(c, pos, b, off, n)
            pos += n
            return n
        }
    }

    val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

        override fun write(b: ByteArray, off: Int, len: Int) {
            var o = off
            val end = off + len
            while (o < end) {
                if (closed.get()) throw IOException("Pipe closed")
                val n = minOf(MaxChunk, end - o)
                sender(b.copyOfRange(o, o + n))
                o += n
            }
        }
    }

    val isClosed: Boolean get() = closed.get()

    internal fun deliver(data: ByteArray) {
        if (!closed.get() && data.isNotEmpty()) queue.offer(data)
    }

    /** The other side or the server ended the pipe. */
    internal fun remoteClosed() {
        closed.set(true)
        queue.offer(endOfStream)
    }

    fun close() {
        if (closed.compareAndSet(false, true)) {
            queue.offer(endOfStream)
            onLocalClose()
        }
    }

    companion object {
        /** Server cap per PipeData frame is 16 KiB. */
        const val MaxChunk = 16 * 1024
    }
}
