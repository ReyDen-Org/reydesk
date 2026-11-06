package ir.reyden.reydesk.settings

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map

private val Context.remoteDataStore by preferencesDataStore(name = "reydesk_remote")

/** Built-in rendezvous service, used whenever no Advanced override is stored. */
const val DEFAULT_RENDEZVOUS_URL = "wss://rdv.reyden.ir/v1/ws"

/** Persisted internet-access choices. Both switches default to off; an empty URL means "use the built-in default". */
data class RemoteSettings(
    val remoteAllowed: Boolean = false,
    val relayAllowed: Boolean = false,
    /** Canonical (unformatted) Desk ID assigned by the rendezvous service, or empty. */
    val deskId: String = "",
    /** Advanced override (wss:// URL), or empty to use [DEFAULT_RENDEZVOUS_URL]. */
    val rendezvousUrl: String = "",
) {
    /** The override if set, else the built-in default. */
    val effectiveRendezvousUrl: String get() = rendezvousUrl.ifEmpty { DEFAULT_RENDEZVOUS_URL }
}

class RemoteSettingsRepository(private val context: Context) {
    private object Keys {
        val Remote = booleanPreferencesKey("remote_allowed")
        val Relay = booleanPreferencesKey("relay_allowed")
        val DeskId = stringPreferencesKey("desk_id")
        val Url = stringPreferencesKey("rendezvous_url")
    }

    val settings: Flow<RemoteSettings> = context.remoteDataStore.data.map { p ->
        RemoteSettings(p[Keys.Remote] ?: false, p[Keys.Relay] ?: false, p[Keys.DeskId] ?: "", p[Keys.Url] ?: "")
    }

    suspend fun load(): RemoteSettings = settings.first()
    suspend fun setRemoteAllowed(value: Boolean) { context.remoteDataStore.edit { it[Keys.Remote] = value } }
    suspend fun setRelayAllowed(value: Boolean) { context.remoteDataStore.edit { it[Keys.Relay] = value } }
    suspend fun setDeskId(value: String) { context.remoteDataStore.edit { it[Keys.DeskId] = value } }
    suspend fun setRendezvousUrl(value: String) { context.remoteDataStore.edit { it[Keys.Url] = value.trim().let { v -> if (v == DEFAULT_RENDEZVOUS_URL) "" else v } } }
}
