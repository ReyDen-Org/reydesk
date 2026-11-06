import ir.reyden.reydesk.rendezvous.Backoff
import ir.reyden.reydesk.rendezvous.RelayPipe
import ir.reyden.reydesk.rendezvous.TlsOverPipe
import ir.reyden.reydesk.rendezvous.TlsPipeStream
import java.io.File
import java.io.IOException
import java.security.KeyStore
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLException

private var failures = 0
private fun check(name: String, ok: Boolean) {
    println((if (ok) "PASS " else "FAIL ") + name)
    if (!ok) failures++
}

private class Host(val keystore: KeyStore, val spki: ByteArray)

private fun makeHost(dir: File, name: String, keytool: String): Host {
    val file = File(dir, "$name.p12")
    file.delete()
    val p = ProcessBuilder(keytool, "-genkeypair", "-alias", "h", "-keyalg", "EC", "-groupname", "secp256r1",
        "-dname", "CN=reydesk-$name", "-validity", "2", "-storetype", "PKCS12", "-keystore", file.path,
        "-storepass", "changeit", "-keypass", "changeit").redirectErrorStream(true).start()
    val out = p.inputStream.readBytes().toString(Charsets.UTF_8)
    check("keytool $name ${out.take(0)}", p.waitFor() == 0)
    val ks = KeyStore.getInstance("PKCS12").apply { file.inputStream().use { load(it, "changeit".toCharArray()) } }
    return Host(ks, ks.getCertificate("h").publicKey.encoded)
}

/** In-memory duplex pipe: bytes written on one end appear on the other, via RelayPipe on both sides. */
private fun pipePair(): Pair<RelayPipe, RelayPipe> {
    lateinit var a: RelayPipe
    lateinit var b: RelayPipe
    a = RelayPipe("p", ByteArray(0), { d -> b.deliver(d) }, { b.remoteClosed() })
    b = RelayPipe("p", ByteArray(0), { d -> a.deliver(d) }, { a.remoteClosed() })
    return a to b
}

fun main(args: Array<String>) {
    val keytool = args[0]
    val dir = File(args[1]).apply { mkdirs() }
    val right = makeHost(dir, "right", keytool)
    val other = makeHost(dir, "other", keytool)

    // Backoff bounds
    check("backoff first 800..1200", Backoff.delayMs(0) in 800..1200)
    check("backoff capped 48000..60000", Backoff.delayMs(20) in 48_000..60_000)
    policyTests(::check)

    // 1. Right key: handshake ok, data both ways incl. 200 KiB.
    run {
        val (cp, sp) = pipePair()
        val srvErr = AtomicReference<Throwable?>()
        val payload = ByteArray(200 * 1024) { (it * 31).toByte() }
        val srvGot = AtomicReference<ByteArray>()
        val t = Thread {
            try {
                val s = ServerSide.create(right.keystore, sp)
                val got = s.input.readNBytes(payload.size)
                srvGot.set(got)
                s.output.write("pong".toByteArray()); s.output.flush()
                s.close()
            } catch (e: Throwable) { srvErr.set(e) }
        }
        t.start()
        val c = TlsOverPipe.connect(cp, right.spki)
        c.output.write(payload); c.output.flush()
        val reply = c.input.readNBytes(4).toString(Charsets.UTF_8)
        t.join(15000)
        check("right key accepted", true)
        check("pong reply over TLS", reply == "pong")
        check("200 KiB intact server side", srvGot.get()?.contentEquals(payload) == true)
        check("no server error", srvErr.get() == null)
        c.close()
    }

    // 2. Wrong key: client must reject the handshake.
    run {
        val (cp, sp) = pipePair()
        val t = Thread {
            try { ServerSide.create(other.keystore, sp) } catch (_: Throwable) { /* server sees the alert */ }
        }
        t.start()
        var rejected: Throwable? = null
        try { TlsOverPipe.connect(cp, right.spki) } catch (e: Throwable) { rejected = e }
        t.join(15000)
        check("wrong key rejected (SSLException)", rejected is SSLException)
        check("rejection names identity mismatch", generateSequence(rejected) { it.cause }.any { it.message?.contains("does not match") == true })
        check("pipe closed after rejection", cp.isClosed)
    }

    // 3. Peer hangs up before handshake completes.
    run {
        val (cp, sp) = pipePair()
        sp.close()
        var err: Throwable? = null
        try { TlsOverPipe.connect(cp, right.spki) } catch (e: Throwable) { err = e }
        check("closed pipe fails handshake", err is IOException)
    }

    if (failures > 0) { println("$failures FAILED"); System.exit(1) }
    println("All rendezvous pipe tests passed")
}

private object ServerSide {
    fun create(ks: KeyStore, pipe: RelayPipe): TlsPipeStream {
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            .apply { init(ks, "changeit".toCharArray()) }
        val ctx = SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, null) }
        val engine = ctx.createSSLEngine().apply { useClientMode = false }
        val stream = TlsPipeStream(engine, pipe.input, pipe.output) { pipe.close() }
        stream.handshake()
        return stream
    }
}
