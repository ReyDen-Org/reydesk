package ir.reyden.reydesk.pairing

import ir.reyden.reydesk.protocol.v1.Envelope
import java.io.InputStream
import java.io.OutputStream
import java.io.EOFException
import java.io.IOException

/** Bound allocation before parsing peer data. Mirrors the Windows frame limit. */
object EnvelopeFraming {
    const val MaximumBytes = 262144

    fun read(input: InputStream): Envelope? {
        var size = 0L
        for (shift in 0..28 step 7) {
            val value = input.read()
            if (value < 0) {
                if (shift == 0) return null
                throw EOFException("Truncated frame length")
            }
            if (shift == 28 && value and 0xf0 != 0) throw IOException("Invalid frame length")
            size = size or ((value and 127).toLong() shl shift)
            if (value and 128 == 0) {
                if (size !in 1..MaximumBytes.toLong()) throw IOException("Signaling frame size rejected")
                val bytes = ByteArray(size.toInt())
                var offset = 0
                while (offset < bytes.size) {
                    val count = input.read(bytes, offset, bytes.size - offset)
                    if (count <= 0) throw EOFException("Truncated signaling frame")
                    offset += count
                }
                val envelope = Envelope.parseFrom(bytes)
                if (envelope.protocolVersion != 1) throw IOException("Unsupported ReyDesk protocol version")
                return envelope
            }
        }
        throw IOException("Invalid frame length")
    }

    fun write(output: OutputStream, envelope: Envelope) {
        if (envelope.serializedSize > MaximumBytes) throw IOException("Signaling frame too large")
        envelope.writeDelimitedTo(output)
        output.flush()
    }
}
