package ir.reyden.reydesk.rendezvous

import java.net.URI

/**
 * One canonical spelling of the rendezvous URL, shared by Windows and Android (T18 H1 step 3). The client signs
 * this exact string, so "wss://Host/v1/ws/" and "wss://host/v1/ws" must not be two different identities:
 * trim, lower-case scheme and host, drop the default port and trailing slashes, no credentials/query/fragment.
 * wss:// only; ws:// is accepted for dev hosts when [allowCleartextForDev] is set (debug builds).
 */
object RendezvousUrl {
    fun isDevHost(host: String): Boolean =
        host == "127.0.0.1" || host == "localhost" || host == "10.0.2.2" || host.endsWith(".local")

    /** Returns the canonical URL, or null when the input is not acceptable. */
    fun normalize(input: String?, allowCleartextForDev: Boolean = false): String? {
        val text = input?.trim() ?: return null
        if (text.isEmpty() || text.any { it.isWhitespace() }) return null
        val uri = try { URI(text) } catch (_: Exception) { return null }
        val scheme = uri.scheme?.lowercase() ?: return null
        val host = uri.host?.lowercase()?.takeIf { it.isNotEmpty() } ?: return null
        if (uri.rawUserInfo != null || uri.rawQuery != null || uri.rawFragment != null) return null
        val secure = scheme == "wss"
        if (!secure && !(scheme == "ws" && allowCleartextForDev && isDevHost(host))) return null
        val defaultPort = if (secure) 443 else 80
        val port = if (uri.port == -1 || uri.port == defaultPort) "" else ":${uri.port}"
        val path = (uri.rawPath ?: "").trimEnd('/')
        return "$scheme://$host$port$path"
    }
}
