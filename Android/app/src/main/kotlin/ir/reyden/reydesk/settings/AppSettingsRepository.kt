package ir.reyden.reydesk.settings

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

private val Context.dataStore by preferencesDataStore(name = "reydesk_settings")

/** Settings use DataStore. The activity applies its persisted language before inflation. */
class AppSettingsRepository(private val context: Context) {
    private object Keys {
        val MaxHeight = intPreferencesKey("stream_max_height")
        val MaxFps = intPreferencesKey("stream_max_fps")
        val BitrateKbps = intPreferencesKey("stream_bitrate_kbps")
        val QualityPreset = stringPreferencesKey("stream_quality_preset")
    }

    val streaming: Flow<StreamPreferences> = context.dataStore.data.map { prefs ->
        StreamPreferences.normalized(prefs[Keys.MaxHeight] ?: 0, prefs[Keys.MaxFps] ?: 0, prefs[Keys.BitrateKbps] ?: 0, prefs[Keys.QualityPreset] ?: "Custom")
    }

    /** Applied to the next connection; zero inherits the host preference. */
    suspend fun setStreaming(preferences: StreamPreferences) {
        val bounded = StreamPreferences.normalized(preferences.maxHeight, preferences.maxFps, preferences.bitrateKbps, preferences.preset)
        context.dataStore.edit {
            it[Keys.MaxHeight] = bounded.maxHeight
            it[Keys.MaxFps] = bounded.maxFps
            it[Keys.BitrateKbps] = bounded.bitrateKbps
            it[Keys.QualityPreset] = bounded.preset
        }
    }
}
