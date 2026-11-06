package ir.reyden.reydesk.settings

import android.content.Context
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

private val Context.tutorialDataStore by preferencesDataStore(name = "reydesk_tutorial")

/** Tutorial state kept on this phone only: whether the first-run card was opened or dismissed, and the last guide path. */
class TutorialPrefs(private val context: Context) {
    private object Keys {
        val Seen = booleanPreferencesKey("tutorial_seen")
        val Path = stringPreferencesKey("tutorial_path")
    }

    val seen: Flow<Boolean> = context.tutorialDataStore.data.map { it[Keys.Seen] ?: false }
    val path: Flow<String> = context.tutorialDataStore.data.map { it[Keys.Path] ?: "wifi" }

    suspend fun markSeen() { context.tutorialDataStore.edit { it[Keys.Seen] = true } }
    suspend fun setPath(value: String) { context.tutorialDataStore.edit { it[Keys.Path] = value } }
}
