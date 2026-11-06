package ir.reyden.reydesk

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalContext
import ir.reyden.reydesk.navigation.ReyDeskNavHost
import ir.reyden.reydesk.settings.AppSettingsRepository
import ir.reyden.reydesk.ui.theme.ReyDeskTheme

/** The scanner uses a ComponentActivity; locale resources are applied explicitly. */
class MainActivity : ComponentActivity() {
    override fun attachBaseContext(newBase: android.content.Context) {
        val language = newBase.getSharedPreferences("reydesk_locale", android.content.Context.MODE_PRIVATE).getString("language", "en")?.takeIf { it == "fa" } ?: "en"
        val configuration = android.content.res.Configuration(newBase.resources.configuration).apply {
            setLocales(android.os.LocaleList.forLanguageTags(language))
            setLayoutDirection(java.util.Locale.forLanguageTag(language))
        }
        super.attachBaseContext(newBase.createConfigurationContext(configuration))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Compose inherits direction from its host view, whose configuration can
        // still reflect the system locale after wrapping the activity context.
        window.decorView.layoutDirection = resources.configuration.layoutDirection
        enableEdgeToEdge()
        setContent {
            val context = LocalContext.current
            val settings = remember { AppSettingsRepository(context.applicationContext) }

            ReyDeskTheme {
                ReyDeskNavHost(settings = settings)
            }
        }
    }
}
