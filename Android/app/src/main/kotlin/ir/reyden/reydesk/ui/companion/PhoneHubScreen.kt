package ir.reyden.reydesk.ui.companion

import android.content.Intent
import android.content.ActivityNotFoundException
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.widget.Toast
import android.provider.Settings
import android.content.pm.PackageManager
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.background
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.companion.PhoneNotificationService
import ir.reyden.reydesk.ui.components.ConsolePanel
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdIconTile
import ir.reyden.reydesk.ui.components.ScreenHeading
import ir.reyden.reydesk.ui.components.SectionLabel
import ir.reyden.reydesk.ui.settings.SwitchRow
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

fun requestNotificationAccess(context: Context) {
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
        try {
            context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS).putExtra(
                Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                ComponentName(context, PhoneNotificationService::class.java).flattenToString(),
            ))
            return
        } catch (_: ActivityNotFoundException) {
            // Some devices expose only the notification access list.
        }
    }
    try {
        context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
    } catch (_: ActivityNotFoundException) {
        Toast.makeText(context, R.string.phone_hub_settings_unavailable, Toast.LENGTH_LONG).show()
    }
}

@Composable
fun PhoneHubScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val hub = (context.applicationContext as ReyDeskApplication).session.phoneHub
    val preferences by hub.preferences.collectAsState()
    val access by hub.access.collectAsState()
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event -> if (event == Lifecycle.Event.ON_RESUME) hub.refresh() }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    var apps by remember { mutableStateOf<List<Pair<String, String>>>(emptyList()) }
    LaunchedEffect(Unit) {
        apps = withContext(Dispatchers.IO) {
            context.packageManager.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), PackageManager.MATCH_ALL)
                .map { it.activityInfo.packageName to it.loadLabel(context.packageManager).toString() }.distinctBy { it.first }.sortedBy { it.second.lowercase() }
        }
    }
    Column(Modifier.fillMaxSize().background(ReyDeskColors.Bg).verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        RdButton(stringResource(R.string.companion_back), onBack, kind = RdButtonKind.Ghost)
        ScreenHeading(stringResource(R.string.phone_hub_title), stringResource(R.string.phone_hub_subtitle))
        ConsolePanel(Modifier.fillMaxWidth()) {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                RdIconTile("shield", small = true)
                Text(stringResource(if (access) R.string.phone_hub_granted else R.string.phone_hub_denied), style = MaterialTheme.typography.titleMedium, color = if (access) ReyDeskColors.Success else ReyDeskColors.Warning)
            }
            Text(stringResource(R.string.phone_hub_access_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            RdButton(stringResource(R.string.phone_hub_grant), { requestNotificationAccess(context) }, Modifier.fillMaxWidth(), kind = RdButtonKind.Secondary, glyph = "bell")
            SwitchRow(stringResource(R.string.phone_hub_enable), preferences.enabled, { hub.update(enabled = it) }, glyph = "phone_hub", enabled = access)
            SwitchRow(stringResource(R.string.phone_hub_previews), preferences.previews, { hub.update(previews = it) }, glyph = "eye")
            SwitchRow(stringResource(R.string.phone_hub_sensitive), preferences.sensitive, { hub.update(sensitive = it) }, glyph = "shield", enabled = preferences.previews)
            Text(stringResource(R.string.phone_hub_sensitive_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            Text(stringResource(R.string.phone_hub_privacy), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
        }
        ConsolePanel(Modifier.fillMaxWidth()) {
            SectionLabel(stringResource(R.string.phone_hub_apps), "bell")
            Text(stringResource(R.string.phone_hub_apps_hint), style = MaterialTheme.typography.bodySmall, color = ReyDeskColors.Muted)
            if (apps.isEmpty()) Text(stringResource(R.string.phone_hub_apps_empty), style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
            apps.forEach { (packageName, label) ->
                val checked = packageName in preferences.allowed
                Row(
                    Modifier.fillMaxWidth().heightIn(min = 56.dp)
                        .toggleable(value = checked, role = Role.Checkbox) { on ->
                            hub.update(allowed = if (on) preferences.allowed + packageName else preferences.allowed - packageName)
                        },
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    RdCheck(checked)
                    Column(Modifier.weight(1f)) {
                        Text(label, style = MaterialTheme.typography.bodyLarge, color = ReyDeskColors.Fg)
                        Text(packageName, style = ReyDeskType.Mono, color = ReyDeskColors.Faint, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
        }
    }
}
