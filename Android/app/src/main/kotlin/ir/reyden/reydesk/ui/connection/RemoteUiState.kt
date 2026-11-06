package ir.reyden.reydesk.ui.connection

import ir.reyden.reydesk.ui.components.RdPath

enum class RemoteServerState { Off, Connecting, Online, Offline }

/**
 * What the internet (Desk ID) UI shows. The identity v2 code fills it in later; screens never invent values.
 * [sas] is non-null only while a SAS comparison is pending (nearby or internet pairing).
 */
data class RemoteUiState(
    val deskId: String?,
    val keyId: String?,
    val serverState: RemoteServerState,
    val path: RdPath?,
    val remoteAllowed: Boolean,
    val relayAllowed: Boolean = false,
    val sas: String? = null,
)
