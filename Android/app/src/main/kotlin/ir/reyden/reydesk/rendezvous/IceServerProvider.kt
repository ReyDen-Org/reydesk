package ir.reyden.reydesk.rendezvous

import ir.reyden.reydesk.protocol.rendezvous.v1.IceConfig
import org.webrtc.PeerConnection

data class IceServerInfo(val urls: List<String>, val username: String, val credential: String)

data class IceConfigState(val servers: List<IceServerInfo>, val expiresAtMs: Long) {
    companion object {
        fun from(c: IceConfig) = IceConfigState(
            c.iceServersList.map { IceServerInfo(it.urlsList.toList(), it.username, it.credential) },
            c.expiresAtMs,
        )
    }

    private fun isTurn(u: String) = u.startsWith("turn:") || u.startsWith("turns:")

    /** True when a TURN server with credentials is present and not (nearly) expired. */
    fun hasUsableTurn(nowMs: Long = System.currentTimeMillis(), marginMs: Long = 30_000): Boolean =
        servers.any { s -> s.urls.any(::isTurn) && s.credential.isNotEmpty() } && !(expiresAtMs in 1..(nowMs + marginMs))

    /** STUN only: TURN servers (and so their credentials) are dropped. Used while relay is not allowed. */
    fun withoutTurn(): IceConfigState = copy(servers = servers.mapNotNull { s ->
        val urls = s.urls.filterNot(::isTurn)
        if (urls.isEmpty()) null else IceServerInfo(urls, "", "")
    })
}

/** ICE servers per connection medium (spec 3.9). */
object IceServerProvider {
    /** LAN medium: no ICE servers, so no external STUN traffic on the LAN. */
    fun forLan(): List<PeerConnection.IceServer> = emptyList()

    /**
     * Remote medium: STUN always, TURN only when the user allowed relay ("Allow relay through
     * ReyDesk server", default off). Expired credentials are dropped.
     */
    fun forRemote(config: IceConfigState?, allowRelay: Boolean, nowMs: Long = System.currentTimeMillis()): List<PeerConnection.IceServer> {
        if (config == null) return emptyList()
        return config.servers.mapNotNull { s ->
            val urls = s.urls.filter { u ->
                u.startsWith("stun:") || (allowRelay && (u.startsWith("turn:") || u.startsWith("turns:")))
            }
            if (urls.isEmpty()) return@mapNotNull null
            val isTurn = urls.any { !it.startsWith("stun:") }
            if (isTurn && config.expiresAtMs in 1..nowMs) return@mapNotNull null
            PeerConnection.IceServer.builder(urls).setUsername(s.username).setPassword(s.credential).createIceServer()
        }
    }
}
