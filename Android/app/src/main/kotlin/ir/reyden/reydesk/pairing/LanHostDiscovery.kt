package ir.reyden.reydesk.pairing

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Handler
import android.os.Looper
import android.util.Log
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** A bounded LAN browse. NSD owns multicast permission/socket handling; no subnet
 * scanning and no PIN is published. Legacy resolve calls are serialized because
 * older Android releases allow only one outstanding resolve per listener. */
object LanHostDiscovery {
    suspend fun findHosts(context: Context): List<LanHostMetadata> = suspendCancellableCoroutine { continuation ->
        val manager = context.getSystemService(NsdManager::class.java)
        val handler = Handler(Looper.getMainLooper())
        val found = linkedMapOf<String, LanHostMetadata>()
        val pending = ArrayDeque<NsdServiceInfo>()
        val seen = mutableSetOf<String>()
        val resolvedIds = mutableMapOf<String, String>()
        var resolving = false
        var finished = false
        var started = false
        lateinit var listener: NsdManager.DiscoveryListener

        fun stop() {
            if (started) {
                started = false
                try { manager.stopServiceDiscovery(listener) }
                catch (e: IllegalArgumentException) { Log.d("LanHostDiscovery", "Discovery already stopped: ${e.message}") }
            }
        }
        fun complete(error: Exception? = null) {
            if (finished) return
            finished = true
            stop()
            if (continuation.isActive) {
                if (error != null) continuation.resumeWithException(error)
                else continuation.resume(found.values.toList())
            }
        }
        fun resolveNext() {
            if (finished || resolving || pending.isEmpty()) return
            resolving = true
            val service = pending.removeFirst()
            try {
                @Suppress("DEPRECATION")
                manager.resolveService(service, object : NsdManager.ResolveListener {
                override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                    handler.post { resolving = false; resolveNext() }
                }
                override fun onServiceResolved(info: NsdServiceInfo) {
                    handler.post {
                        if (finished) return@post
                        if (service.serviceName !in seen) {
                            resolving = false
                            resolveNext()
                            return@post
                        }
                        @Suppress("DEPRECATION") val address = info.host?.hostAddress
                        val properties = info.attributes.mapValues { (_, value) -> value.toString(Charsets.UTF_8) }
                        address?.let { LanHostMetadata.parse(properties, it, info.port) }?.let {
                            found[it.id] = it
                            resolvedIds[service.serviceName] = it.id
                        }
                        resolving = false
                        resolveNext()
                    }
                }
                })
            } catch (e: Exception) { resolving = false; complete(e) }
        }
        listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onServiceFound(info: NsdServiceInfo) {
                handler.post {
                    if (!finished && info.serviceType.trimEnd('.') == "_reydesk._tcp" && seen.size < 16 && seen.add(info.serviceName)) {
                        pending.addLast(info)
                        resolveNext()
                    }
                }
            }
            override fun onServiceLost(info: NsdServiceInfo) {
                handler.post {
                    seen.remove(info.serviceName)
                    pending.removeAll { it.serviceName == info.serviceName }
                    resolvedIds.remove(info.serviceName)?.let { found.remove(it) }
                }
            }
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                handler.post { complete(IllegalStateException("LAN discovery failed ($errorCode)")) }
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w("LanHostDiscovery", "Discovery stop failed ($errorCode)")
            }
        }
        val timeout = Runnable { complete() }
        continuation.invokeOnCancellation { handler.post { finished = true; handler.removeCallbacks(timeout); stop() } }
        handler.post {
            if (!continuation.isActive) return@post
            try {
                started = true
                manager.discoverServices("_reydesk._tcp.", NsdManager.PROTOCOL_DNS_SD, listener)
                handler.postDelayed(timeout, 6000)
            } catch (e: Exception) { complete(e) }
        }
    }
}
