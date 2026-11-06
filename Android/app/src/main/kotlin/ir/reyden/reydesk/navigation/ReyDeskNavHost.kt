package ir.reyden.reydesk.navigation

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.Alignment
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.graphics.luminance
import androidx.core.view.WindowCompat
import ir.reyden.reydesk.ui.settings.findActivity
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.navigation.NavDestination.Companion.hierarchy
import androidx.navigation.NavGraph.Companion.findStartDestination
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import ir.reyden.reydesk.R
import ir.reyden.reydesk.settings.AppSettingsRepository
import ir.reyden.reydesk.ui.connection.ConnectionScreen
import ir.reyden.reydesk.ui.devices.DevicesScreen
import ir.reyden.reydesk.ui.session.SessionScreen
import ir.reyden.reydesk.ui.settings.SettingsScreen
import ir.reyden.reydesk.ui.tutorial.GuidedTourOverlay
import ir.reyden.reydesk.ui.tutorial.TourController
import ir.reyden.reydesk.ui.tutorial.TutorialHighlight
import ir.reyden.reydesk.ui.tutorial.TutorialScreen
import androidx.navigation.NavType
import androidx.navigation.navArgument
import ir.reyden.reydesk.ui.components.AppGlyph
import ir.reyden.reydesk.ui.components.RdBottomNav
import ir.reyden.reydesk.ui.components.RdMotion
import ir.reyden.reydesk.ui.components.rememberAnimationsEnabled
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import ir.reyden.reydesk.ui.companion.PhoneHubScreen
import ir.reyden.reydesk.ui.companion.ControlScreen
import ir.reyden.reydesk.ui.companion.FilesScreen
import androidx.compose.runtime.LaunchedEffect
import kotlinx.coroutines.flow.first
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.platform.LocalContext
import ir.reyden.reydesk.ReyDeskApplication

private sealed class Destination(val route: String, val labelRes: Int) {
    data object Connection : Destination("connection", R.string.nav_connection)
    data object Devices : Destination("devices", R.string.nav_devices)
    data object Settings : Destination("settings", R.string.nav_settings)
}

private const val SessionRoute = "session"
private val bottomNavDestinations = listOf(Destination.Connection, Destination.Devices, Destination.Settings)

/** Three bottom-nav destinations, mirroring Windows/ReyDesk.Windows's
 * NavigationView (Connection/Devices/Settings) plus a fourth, non-bottom-nav "session" route pushed once
 * pairing succeeds (see Connection -> Session below). The bottom bar hides on
 * "session" so the remote screen-share view is fully immersive. */
@Composable
fun ReyDeskNavHost(settings: AppSettingsRepository) {
    val navController = rememberNavController()
    val session = (LocalContext.current.applicationContext as ReyDeskApplication).session
    val transfer by session.transferUi.collectAsState()
    LaunchedEffect(transfer.incoming?.transferId) {
        if (transfer.incoming != null) navController.navigate("files") { launchSingleTop = true }
    }
    val backStackEntry by navController.currentBackStackEntryAsState()
    val onSessionRoute = backStackEntry?.destination?.hierarchy?.any { it.route == SessionRoute } == true
    val currentRoute = backStackEntry?.destination?.route
    val showBottomBar = currentRoute in bottomNavDestinations.map { it.route }
    val animations = rememberAnimationsEnabled()
    val view = LocalView.current
    val darkSystemIcons = !onSessionRoute && MaterialTheme.colorScheme.background.luminance() > .5f
    SideEffect {
        view.context.findActivity()?.let { activity ->
            val orientation = if (onSessionRoute) android.content.pm.ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE else android.content.pm.ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
            if (activity.requestedOrientation != orientation) activity.requestedOrientation = orientation
        }
        view.context.findActivity()?.window?.let { window ->
            WindowCompat.getInsetsController(window, view).apply {
                isAppearanceLightStatusBars = darkSystemIcons
                isAppearanceLightNavigationBars = darkSystemIcons
            }
        }
    }

    fun openMain(route: String) {
        navController.navigate(route) {
            popUpTo(navController.graph.findStartDestination().id) { saveState = true }
            launchSingleTop = true
            restoreState = true
        }
    }
    val settingsLabel = stringResource(R.string.nav_settings)
    // First launch: start the guided tour by itself once, when the tutorial was never seen and no computer is paired yet.
    val appContext = LocalContext.current.applicationContext
    LaunchedEffect(Unit) {
        val prefs = ir.reyden.reydesk.settings.TutorialPrefs(appContext)
        val seen = prefs.seen.first()
        val noTrusted = session.trustedDevices.devices.first().none { !it.revoked }
        if (!seen && noTrusted) { prefs.markSeen(); TourController.start() }
    }
    Box(Modifier.fillMaxSize()) {
    Scaffold(
        contentWindowInsets = if (onSessionRoute) WindowInsets(0) else ScaffoldDefaults.contentWindowInsets,
        topBar = {
            if (!onSessionRoute) {
                Row(Modifier.fillMaxWidth().statusBarsPadding().height(60.dp).padding(horizontal = 20.dp),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Image(painterResource(R.drawable.reydesk_tile), contentDescription = null, modifier = Modifier.size(28.dp))
                    Text("ReyDesk", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                    IconButton(onClick = { openMain(Destination.Settings.route) }, modifier = Modifier.semantics { contentDescription = settingsLabel }) {
                        AppGlyph("settings", Modifier.size(23.dp))
                    }
                }
            }
        },
        bottomBar = {
            if (showBottomBar) {
                RdBottomNav(
                    selectedRoute = bottomNavDestinations.firstOrNull { d -> backStackEntry?.destination?.hierarchy?.any { it.route == d.route } == true }?.route,
                    items = bottomNavDestinations.map { it.route to stringResource(it.labelRes) },
                    onSelect = { route ->
                        navController.navigate(route) {
                            popUpTo(navController.graph.findStartDestination().id) { saveState = true }
                            launchSingleTop = true
                            restoreState = true
                        }
                    },
                )
            }
        },
    ) { padding ->
        NavHost(
            navController = navController,
            startDestination = Destination.Connection.route,
            modifier = Modifier.padding(padding),
            enterTransition = { if (animations) fadeIn(tween(RdMotion.Fast)) else EnterTransition.None },
            exitTransition = { if (animations) fadeOut(tween(RdMotion.Fast)) else ExitTransition.None },
            popEnterTransition = { if (animations) fadeIn(tween(RdMotion.Fast)) else EnterTransition.None },
            popExitTransition = { if (animations) fadeOut(tween(RdMotion.Fast)) else ExitTransition.None },
        ) {
            composable(Destination.Connection.route) {
                ConnectionScreen(onSessionStarted = { controlOnly -> navController.navigate(if (controlOnly) "control" else SessionRoute) }, onOpenTutorial = { TourController.start() })
            }
            composable(Destination.Devices.route) { DevicesScreen(onAddComputer = { openMain(Destination.Connection.route) }, onSessionStarted = { controlOnly -> navController.navigate(if (controlOnly) "control" else SessionRoute) }) }
            composable(Destination.Settings.route) { SettingsScreen(settings, onPhoneHub = { navController.navigate("phone_hub") }, onFiles = { navController.navigate("files") }, onTutorial = { TourController.start() }) }
            composable("tutorial?path={path}", arguments = listOf(navArgument("path") { type = NavType.StringType; nullable = true; defaultValue = null })) { entry ->
                TutorialScreen(
                    initialPath = entry.arguments?.getString("path"),
                    onBack = { navController.popBackStack() },
                    onStartTour = { path, wifi, internet -> navController.popBackStack(); TourController.start(path, wifi, internet) },
                    // The tutorial stays on the back stack so Back returns to the same step.
                    onShowMe = { show ->
                        navController.navigate(show.route) { launchSingleTop = true }
                        show.target?.let { TutorialHighlight.show(it) }
                    },
                )
            }
            composable("phone_hub") { PhoneHubScreen(onBack = { navController.popBackStack() }) }
            composable("control") { ControlScreen(onDesktop = { navController.navigate(SessionRoute) { popUpTo("control") { inclusive = true } } }, onBack = { navController.popBackStack(Destination.Connection.route, false) }, onFiles = { navController.navigate("files") }) }
            composable("files") { FilesScreen(onBack = { navController.popBackStack() }) }
            composable(SessionRoute) { SessionScreen(onBack = { navController.popBackStack(Destination.Connection.route, false) }, onControl = { navController.navigate("control") { popUpTo(SessionRoute) { inclusive = true } } }) }
        }
    }
    GuidedTourOverlay(currentRoute = currentRoute, onNavigate = { openMain(it) })
    }
}
