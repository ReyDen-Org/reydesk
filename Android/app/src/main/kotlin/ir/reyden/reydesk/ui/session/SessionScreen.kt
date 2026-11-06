package ir.reyden.reydesk.ui.session

import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import ir.reyden.reydesk.R
import ir.reyden.reydesk.ReyDeskApplication
import ir.reyden.reydesk.ui.components.RdButton
import ir.reyden.reydesk.ui.components.RdButtonKind
import ir.reyden.reydesk.ui.components.RdDialog
import ir.reyden.reydesk.ui.companion.rdFieldColors
import ir.reyden.reydesk.ui.theme.ReyDeskColors
import ir.reyden.reydesk.ui.theme.ReyDeskShapes
import ir.reyden.reydesk.protocol.v1.MouseButton
import org.webrtc.EglBase
import org.webrtc.VideoTrack

/** Remote desktop with direct taps, move-only single-finger drags and local pinch/pan. */
@Composable
fun SessionScreen(onBack: () -> Unit, onControl: () -> Unit = {}) {
    val context = LocalContext.current
    val session = (context.applicationContext as ReyDeskApplication).session
    var videoTrack by remember { mutableStateOf<VideoTrack?>(null) }
    var showKeyboard by remember { mutableStateOf(false) }
    var typedText by remember { mutableStateOf("") }
    val focusRequester = remember { FocusRequester() }
    val keyboardController = LocalSoftwareKeyboardController.current
    val focusManager = LocalFocusManager.current
    LaunchedEffect(showKeyboard) {
        if (!showKeyboard) {
            focusManager.clearFocus()
            keyboardController?.hide()
        }
    }
    var showTouchHelp by remember { mutableStateOf(false) }
    var chromeVisible by remember { mutableStateOf(true) }
    var revealChrome by remember { mutableStateOf(0) }
    val hostName by session.connectedHostName.collectAsState()
    val startedAt by session.sessionStartedAtMs.collectAsState()
    val metrics by session.streamStats.collectAsState()
    val negotiated by session.negotiatedConfig.collectAsState()
    var zoomPercent by remember { mutableStateOf(100) }
    var resetZoom by remember { mutableStateOf(0) }
    var zoomAction by remember { mutableStateOf(0) }
    val companionResult by session.companionResult.collectAsState()
    var controlRequested by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(companionResult) {
        if (controlRequested != null && companionResult?.requestId == controlRequested) {
            controlRequested = null
            if (companionResult?.success == true) onControl()
        }
    }

    LaunchedEffect(Unit) {
        session.remoteVideoTrack.collect { videoTrack = it }
    }
    // SessionCoordinator resets connectionState to null when the peer connection
    // closes (see SessionCoordinator.kt) - navigate back automatically rather than
    // leaving the user stuck on a permanently black/loading screen after a real
    // disconnect. hadConnected guards against firing on the initial null before a
    // connection is even attempted.
    val relayHint by session.relayHint.collectAsState()
    val hintContext = androidx.compose.ui.platform.LocalContext.current
    LaunchedEffect(Unit) {
        var hadConnected = false
        session.connectionState.collect { state ->
            if (state != null) {
                hadConnected = true
            } else if (hadConnected) {
                if (session.relayHint.value) android.widget.Toast.makeText(hintContext, R.string.session_relay_hint, android.widget.Toast.LENGTH_LONG).show()
                onBack()
            }
        }
    }

    if (showTouchHelp) {
        RdDialog(onDismiss = { showTouchHelp = false }, title = stringResource(R.string.settings_gestures),
            confirmText = stringResource(android.R.string.ok), onConfirm = { showTouchHelp = false }) {
            Text(stringResource(R.string.session_gesture_hint) + "\n\n" + stringResource(R.string.settings_control_help),
                style = MaterialTheme.typography.bodyMedium, color = ReyDeskColors.Muted)
        }
    }
    Box(modifier = Modifier.fillMaxSize().background(ReyDeskColors.Ink0)) {
        val track = videoTrack
        if (track != null) {
            RemoteVideoView(
                track = track,
                eglContext = session.eglBase.eglBaseContext,
                resetZoom = resetZoom,
                zoomAction = zoomAction,
                onRevealControls = { chromeVisible = true; revealChrome++ },
                onZoomChanged = { zoomPercent = it },
                onNormalizedGesture = { x, y, isDown, isMove ->
                    val webRtcSession = session.activeSessionOrNull() ?: return@RemoteVideoView
                    if (isMove) {
                        webRtcSession.sendMouseMove(x, y)
                    } else {
                        webRtcSession.sendMouseButton(MouseButton.MOUSE_BUTTON_LEFT, isDown, x, y)
                    }
                },
            )
        } else {
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                androidx.compose.material3.CircularProgressIndicator(color = ReyDeskColors.Accent, trackColor = ReyDeskColors.RingTrack)
                Text(
                    text = stringResource(R.string.session_waiting_for_video),
                    style = MaterialTheme.typography.bodyMedium,
                    color = ReyDeskColors.Muted,
                    modifier = Modifier.padding(top = 64.dp),
                )
                if (relayHint) Text(
                    text = stringResource(R.string.session_relay_hint),
                    style = MaterialTheme.typography.bodyMedium,
                    color = ReyDeskColors.Fg,
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                    modifier = Modifier.padding(start = 32.dp, end = 32.dp, top = 120.dp),
                )
            }
        }

        SessionChrome(
            hostName = hostName,
            startedAt = startedAt,
            metrics = metrics,
            negotiated = negotiated,
            visible = chromeVisible,
            revealToken = revealChrome,
            zoomPercent = zoomPercent,
            keyboardVisible = showKeyboard,
            onKeyboard = { showKeyboard = !showKeyboard },
            onTouchHelp = { showTouchHelp = true },
            onResetView = { resetZoom++ },
            onZoom = { zoomAction++ },
            onDisconnect = { session.disconnectActiveSession(); onBack() },
            onHideAll = { showKeyboard = false; chromeVisible = false },
        ) {
            if (showKeyboard) {
                OutlinedTextField(
                    value = typedText,
                    onValueChange = { new ->
                        val webRtcSession = session.activeSessionOrNull()
                        if (new.length > typedText.length && new.startsWith(typedText)) {
                            // Plain append - the common case while typing.
                            webRtcSession?.sendTextInput(new.substring(typedText.length))
                        } else if (new.length < typedText.length && typedText.startsWith(new)) {
                            // Backspace(es) - TextInput has no delete semantics (see
                            // input.proto), so this was previously silently dropped: it
                            // erased the local textbox but never reached the PC at all.
                            repeat(typedText.length - new.length) { webRtcSession?.sendBackspace() }
                        }
                        // Anything else (autocorrect replacing a whole word, cursor
                        // moved and inserted mid-string, etc.) isn't a simple
                        // append/delete - not sent, rather than guessing wrong.
                        typedText = new
                    },
                    label = { Text(stringResource(R.string.session_type_hint), style = MaterialTheme.typography.labelMedium) },
                    textStyle = MaterialTheme.typography.bodyLarge,
                    shape = ReyDeskShapes.Control,
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text, imeAction = ImeAction.Send),
                    keyboardActions = KeyboardActions(onSend = {
                        // A real Enter keypress, not committed text - confirmed on real
                        // hardware that sending "\n" as TextInput doesn't submit anything.
                        session.activeSessionOrNull()?.sendEnter()
                    }),
                    colors = rdFieldColors(),
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 8.dp, vertical = 6.dp)
                        .heightIn(min = 60.dp)
                        .focusRequester(focusRequester),
                )
                LaunchedEffect(showKeyboard) { focusRequester.requestFocus() }
            }
        }
        if (chromeVisible && !showKeyboard) {
            RdButton(
                stringResource(R.string.companion_control), { controlRequested = session.requestCompanion("control") },
                Modifier.align(Alignment.TopEnd).systemBarsPadding().padding(top = 58.dp, end = 12.dp),
                kind = RdButtonKind.Tonal, enabled = controlRequested == null,
            )
            if (companionResult?.success == false) Text(companionResult?.detail.orEmpty(), color = ReyDeskColors.Fg, style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.align(Alignment.TopCenter).systemBarsPadding().padding(top = 110.dp)
                    .background(ReyDeskColors.DangerBg, ReyDeskShapes.Control).padding(8.dp))
        }
    }
}

@Composable
private fun RemoteVideoView(
    track: VideoTrack,
    eglContext: EglBase.Context,
    resetZoom: Int,
    zoomAction: Int,
    onRevealControls: () -> Unit,
    onZoomChanged: (Int) -> Unit,
    onNormalizedGesture: (x: Float, y: Float, isDown: Boolean, isMove: Boolean) -> Unit,
) {
    val context = LocalContext.current
    var viewport by remember { mutableStateOf(VideoViewport(0f, 0f, 16f / 9f)) }
    // EglBase.getEglBaseContext() returns a fresh Java wrapper on every call.
    // The underlying shared context is app-owned and stable: keying this view by
    // the wrapper replaces its sink on recomposition, while AndroidView's factory
    // keeps the first attached view. Keep one renderer for this composition.
    val renderer = remember {
        ZoomVideoRenderer(context, eglContext) { aspect ->
            viewport = viewport.copy(aspect = aspect).bounded()
        }
    }
    DisposableEffect(renderer) { onDispose { renderer.release() } }
    DisposableEffect(track, renderer) {
        track.addSink(renderer)
        onDispose { track.removeSink(renderer) }
    }
    LaunchedEffect(resetZoom) { viewport = viewport.copy(zoom = 1f, panX = 0f, panY = 0f) }
    LaunchedEffect(zoomAction) {
        if (zoomAction > 0) {
            val nextZoom = when { viewport.zoom < 1.9f -> 2f; viewport.zoom < 2.9f -> 3f; else -> 1f }
            viewport = viewport.copy(zoom = nextZoom).bounded()
        }
    }
    LaunchedEffect(viewport.zoom) { onZoomChanged((viewport.zoom * 100).toInt()) }
    val gestureDescription = stringResource(R.string.session_gesture_hint)
    Box(
        modifier = Modifier.fillMaxSize()
            .onSizeChanged { viewport = viewport.copy(width = it.width.toFloat(), height = it.height.toFloat()).bounded() }
            .semantics { contentDescription = gestureDescription }
            .pointerInput(track) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    var moved = false
                    var multiple = false
                    var threeFingerGesture = false
                    var cancelled = false
                    do {
                        val event = awaitPointerEvent()
                        if (event.changes.any { it.isConsumed }) cancelled = true
                        val pressed = event.changes.count { it.pressed }
                        if (pressed >= 2) multiple = true
                        if (pressed >= 3) threeFingerGesture = true
                        if (!cancelled && !threeFingerGesture && pressed >= 2 && event.changes.count { it.pressed && it.previousPressed } >= 2) {
                            val center = event.calculateCentroid(useCurrent = false)
                            val pan = event.calculatePan()
                            viewport = viewport.transform(center.x, center.y, event.calculateZoom(), pan.x, pan.y)
                        } else if (!cancelled && !multiple) {
                            val pointer = event.changes.firstOrNull { it.id == down.id }
                            if (pointer != null) {
                                if ((pointer.position - down.position).getDistance() > viewConfiguration.touchSlop) moved = true
                                val point = viewport.normalized(pointer.position.x, pointer.position.y)
                                if (moved && pointer.pressed && point != null) {
                                    onNormalizedGesture(point.first, point.second, false, true)
                                } else if (!pointer.pressed && !moved && point != null) {
                                    onNormalizedGesture(point.first, point.second, true, false)
                                    onNormalizedGesture(point.first, point.second, false, false)
                                }
                            }
                        }
                        event.changes.forEach { it.consume() }
                    } while (event.changes.any { it.pressed })
                    if (threeFingerGesture) onRevealControls()
                }
            },
    ) {
        AndroidView(
            factory = { renderer },
            update = { it.show(viewport) },
            modifier = Modifier.fillMaxSize(),
        )
    }
}
