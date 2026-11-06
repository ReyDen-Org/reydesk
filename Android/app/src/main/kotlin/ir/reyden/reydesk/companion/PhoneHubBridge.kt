package ir.reyden.reydesk.companion

import android.app.KeyguardManager
import android.app.Notification
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.BatteryManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.Build
import android.service.notification.StatusBarNotification
import androidx.core.app.NotificationManagerCompat
import com.google.protobuf.ByteString
import ir.reyden.reydesk.protocol.v1.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.util.UUID

data class PhoneHubPreferences(val enabled: Boolean = false, val previews: Boolean = false, val sensitive: Boolean = false, val allowed: Set<String> = emptySet())

/** All state and PendingIntent handles are main-thread confined, in memory, and session owned. */
class PhoneHubBridge(private val context: Context) {
    private val handler = Handler(Looper.getMainLooper())
    private val prefs = context.getSharedPreferences("reydesk_phone_hub", Context.MODE_PRIVATE)
    private val _preferences = MutableStateFlow(readPreferences())
    val preferences = _preferences.asStateFlow()
    private val _access = MutableStateFlow(hasAccess())
    val access = _access.asStateFlow()
    private var listener: PhoneNotificationService? = null
    private var owner: String? = null
    private var sink: ((PhoneHubEvent) -> Unit)? = null
    private var paused = false
    private val records = linkedMapOf<String, StatusBarNotification>()
    private data class Handle(val notificationId: String, val revision: String, val pendingIntent: PendingIntent, val remoteInputs: Array<RemoteInput>?, val open: Boolean)
    private val handles = mutableMapOf<String, Handle>()
    private val revisions = mutableMapOf<String, String>()
    private var expiryGeneration = 0
    private val completedRequests = linkedMapOf<String, CompanionResult>()
    private var requestWindowAt = 0L
    private var requestWindowCount = 0
    private var lastLocked = false

    private fun readPreferences() = PhoneHubPreferences(prefs.getBoolean("enabled", false), prefs.getBoolean("previews", false), prefs.getBoolean("sensitive", false), prefs.getStringSet("allowed", emptySet())?.toSet() ?: emptySet())
    private fun onMain(block: () -> Unit) { if (Looper.myLooper() == Looper.getMainLooper()) block() else handler.post(block) }
    fun hasAccess(): Boolean = NotificationManagerCompat.getEnabledListenerPackages(context).contains(context.packageName)
    fun update(enabled: Boolean = _preferences.value.enabled, previews: Boolean = _preferences.value.previews, sensitive: Boolean = _preferences.value.sensitive, allowed: Set<String> = _preferences.value.allowed) = onMain {
        prefs.edit().putBoolean("enabled", enabled).putBoolean("previews", previews).putBoolean("sensitive", sensitive).putStringSet("allowed", allowed.toSet()).apply()
        _preferences.value = readPreferences()
        snapshot()
    }
    fun attach(service: PhoneNotificationService) = onMain { listener = service; refresh() }
    fun detach(service: PhoneNotificationService) = onMain {
        if (listener !== service) return@onMain
        listener = null; clear(); _access.value = hasAccess(); status("Notification listener disconnected; open Phone Hub on the phone.")
    }
    fun refresh() = onMain { _access.value = hasAccess(); snapshot() }
    fun beginSession(identity: String, send: (PhoneHubEvent) -> Unit) = onMain {
        clear(); owner = identity; sink = send; paused = false; completedRequests.clear(); requestWindowCount = 0; requestWindowAt = android.os.SystemClock.elapsedRealtime(); lastLocked = locked(); snapshot()
        // Periodic access/lock checks also revoke stale handles when OS callbacks are delayed.
        val generation = ++expiryGeneration
        fun watch() {
            if (generation != expiryGeneration || owner != identity) return
            val granted = hasAccess()
            val nowLocked = locked()
            if (granted != _access.value || nowLocked != lastLocked) { _access.value = granted; lastLocked = nowLocked; snapshot() }
            handler.postDelayed({ watch() }, 2000)
        }
        watch()
    }
    fun endSession(identity: String) = onMain {
        if (owner != identity) return@onMain
        clear(); owner = null; sink = null; paused = false; completedRequests.clear(); ++expiryGeneration
    }
    private fun emit(event: PhoneHubEvent) { sink?.invoke(event) }
    private fun clear() {
        records.clear(); handles.clear(); revisions.clear()
        emit(PhoneHubEvent.newBuilder().setKind("clear").build())
    }
    private fun locked() = (context.getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager).isDeviceLocked
    private fun eligible() = owner != null && _preferences.value.enabled && !paused && hasAccess() && listener != null
    private fun status(detail: String = "") {
        val battery = context.getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        emit(PhoneHubEvent.newBuilder().setKind("status").setState(PhoneHubState.newBuilder()
            .setAccessGranted(hasAccess()).setEnabled(_preferences.value.enabled && !paused).setPreviews(_preferences.value.previews)
            .setPhoneName(Build.MODEL ?: "Android").setBatteryPercent(battery.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY).takeIf { it in 0..100 } ?: -1)
            .setCharging(battery.isCharging).setDetail(detail.take(200))).build())
    }
    private fun snapshot() {
        clear(); status(if (locked()) "Phone locked: opted-in previews remain available; notification actions require unlocking." else if (listener == null && hasAccess()) "Waiting for Android notification listener." else "")
        if (!eligible()) return
        emit(PhoneHubEvent.newBuilder().setKind("snapshot_start").build())
        try { listener?.activeNotifications?.sortedByDescending { it.postTime }?.take(100)?.forEach { upsert(it) } }
        catch (_: SecurityException) { clear(); status("Android denied notification access.") }
        emit(PhoneHubEvent.newBuilder().setKind("snapshot_end").build())
    }
    fun posted(notification: StatusBarNotification) = onMain {
        if (eligible()) upsert(notification)
    }
    fun removed(key: String) = onMain {
        val id = notificationId(key)
        if (records.remove(id) != null) {
            revisions.remove(id); handles.entries.removeAll { it.value.notificationId == id }
            emit(PhoneHubEvent.newBuilder().setKind("removed").setRemovedId(id).build())
        }
    }
    private fun notificationId(key: String): String = MessageDigest.getInstance("SHA-256").digest("$owner:$key".toByteArray()).joinToString("") { "%02x".format(it) }
    private fun upsert(sbn: StatusBarNotification) {
        if (!eligible()) return
        if (sbn.packageName !in _preferences.value.allowed) { removed(sbn.key); return }
        val id = notificationId(sbn.key)
        if (!records.containsKey(id) && records.size >= 100) return
        handles.entries.removeAll { it.value.notificationId == id }
        val revision = UUID.randomUUID().toString()
        revisions[id] = revision; records[id] = sbn
        val n = sbn.notification
        val title = n.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty().take(500)
        val body = (n.extras.getCharSequence(Notification.EXTRA_BIG_TEXT) ?: n.extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty().take(2000)
        val appName = try { context.packageManager.getApplicationLabel(context.packageManager.getApplicationInfo(sbn.packageName, 0)).toString() } catch (_: Exception) { sbn.packageName }
        val reason = NotificationPrivacy.reason(_preferences.value.previews, "${sbn.packageName} $appName", n.category.orEmpty(), title, body, systemHidden = n.visibility == Notification.VISIBILITY_SECRET, allowSensitive = _preferences.value.sensitive)
        val redacted = reason != null
        if (ir.reyden.reydesk.BuildConfig.DEBUG) android.util.Log.d("ReyDeskHub", "preview=${reason ?: "allowed"}; locked=${locked()}; titleLength=${title.length}; bodyLength=${body.length}; summary=${n.flags and Notification.FLAG_GROUP_SUMMARY != 0}")
        val wire = PhoneNotification.newBuilder().setId(id).setRevision(revision).setPackageName(sbn.packageName).setAppName(appName.take(100))
            .setTitle(if (redacted) "Notification" else title).setText(if (redacted) "Content hidden on this phone." else body)
            .setPostedAtUnixMs(sbn.postTime).setCategory(n.category.orEmpty()).setGroupKey(notificationId(sbn.groupKey)).setRedacted(redacted)
        icon(sbn.packageName)?.let { wire.iconPng = ByteString.copyFrom(it) }
        if (NotificationPrivacy.allowsActions(locked(), redacted)) {
            n.contentIntent?.let { pending ->
                val actionId = "$revision:${UUID.randomUUID()}"
                handles[actionId] = Handle(id, revision, pending, null, true)
                wire.addActions(PhoneAction.newBuilder().setId(actionId).setLabel("Open on phone"))
            }
            // No third-party content actions for hidden previews; preserves OTP and bank privacy.
            if (!redacted) n.actions?.take(5)?.forEach { action ->
                val inputs = action.remoteInputs
                if (inputs != null && inputs.size > 1) return@forEach // No ambiguous multi-field replies.
                val input = inputs?.singleOrNull()
                if (input != null && !input.allowFreeFormInput && input.choices.isNullOrEmpty()) return@forEach
                val choices = input?.choices?.map { it.toString() }?.filter { it.length <= 256 }?.take(5) ?: emptyList()
                if (input != null && !input.allowFreeFormInput && choices.isEmpty()) return@forEach
                val actionId = "$revision:${UUID.randomUUID()}"
                handles[actionId] = Handle(id, revision, action.actionIntent, inputs, false)
                wire.addActions(PhoneAction.newBuilder().setId(actionId).setLabel(action.title?.toString().orEmpty().take(100))
                    .setAcceptsReply(input != null).setAllowsFreeForm(input?.allowFreeFormInput ?: false)
                    .addAllReplyChoices(choices))
            }
        }
        emit(PhoneHubEvent.newBuilder().setKind("upsert").setNotification(wire).build())
    }
    private fun icon(packageName: String): ByteArray? = try {
        val drawable = context.packageManager.getApplicationIcon(packageName)
        val bitmap = Bitmap.createBitmap(48, 48, Bitmap.Config.ARGB_8888)
        drawable.setBounds(0, 0, 48, 48); drawable.draw(Canvas(bitmap))
        val bytes = ByteArrayOutputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it); it.toByteArray() }
        bitmap.recycle(); bytes.takeIf { it.size <= 16384 }
    } catch (_: Exception) { null }

    fun request(identity: String, request: CompanionRequest, reply: (CompanionResult) -> Unit) = onMain {
        fun result(ok: Boolean, detail: String) {
            val value = CompanionResult.newBuilder().setRequestId(request.requestId).setSuccess(ok).setDetail(detail).build()
            if (completedRequests.size < 512) completedRequests[request.requestId] = value
            reply(value)
        }
        if (owner != identity || sink == null) {
            reply(CompanionResult.newBuilder().setRequestId(request.requestId.take(128)).setSuccess(false).setDetail("This session is no longer active.").build())
            return@onMain
        }
        if (request.requestId.isBlank() || request.requestId.length > 128) { result(false, "Invalid request identity."); return@onMain }
        completedRequests[request.requestId]?.let { reply(it); return@onMain }
        // Never evict an executed identity within a session: old replays remain harmless.
        if (completedRequests.size >= 512) { result(false, "Session command budget exhausted; reconnect."); return@onMain }
        val now = android.os.SystemClock.elapsedRealtime()
        if (now - requestWindowAt >= 10000) { requestWindowAt = now; requestWindowCount = 0 }
        if (++requestWindowCount > 30) { result(false, "Too many phone requests; try again shortly."); return@onMain }
        when (request.operation) {
            "phone_sync" -> { snapshot(); result(true, "Phone Hub synchronized."); return@onMain }
            "phone_pause" -> { paused = true; clear(); status("Mirroring paused for this session."); result(true, "Mirroring paused."); return@onMain }
            "phone_resume" -> { paused = false; snapshot(); result(_preferences.value.enabled && hasAccess(), "Phone settings and app allowlist remain authoritative."); return@onMain }
        }
        if (!eligible()) { clear(); status(); result(false, "Enable Phone Hub and notification access on the phone."); return@onMain }
        if (!NotificationPrivacy.allowsActions(locked(), redacted = false)) { snapshot(); result(false, "Unlock the phone before using notification actions."); return@onMain }
        val sbn = records[request.notificationId]
        if (sbn == null || sbn.packageName !in _preferences.value.allowed) { result(false, "Notification is no longer available."); return@onMain }
        if (request.operation == "phone_dismiss") {
            if (!sbn.isClearable) { result(false, "Android does not allow dismissing this notification."); return@onMain }
            try { listener?.cancelNotification(sbn.key); removed(sbn.key); result(true, "Dismissal requested from Android.") }
            catch (_: Exception) { result(false, "Android denied dismissal.") }
            return@onMain
        }
        if (request.operation != "phone_action") { result(false, "Unknown phone operation."); return@onMain }
        val handle = handles[request.actionId]
        if (handle == null || handle.notificationId != request.notificationId || handle.revision != revisions[request.notificationId]) { result(false, "This action expired; refresh the notification."); return@onMain }
        try {
            val input = handle.remoteInputs?.singleOrNull()
            val intent = Intent()
            if (input != null) {
                if (!NotificationPrivacy.acceptsReply(request.text, input.allowFreeFormInput, input.choices?.map { it.toString() } ?: emptyList())) { result(false, "Reply does not meet this app's input constraints."); return@onMain }
                RemoteInput.addResultsToIntent(handle.remoteInputs, intent, Bundle().apply { putCharSequence(input.resultKey, request.text) })
                RemoteInput.setResultsSource(intent, if (input.choices?.any { it.toString() == request.text } == true) RemoteInput.SOURCE_CHOICE else RemoteInput.SOURCE_FREE_FORM_INPUT)
            } else if (request.text.isNotEmpty()) { result(false, "This action does not accept text."); return@onMain }
            handle.pendingIntent.send(context, 0, intent)
            // Android may restrict background activities; dispatch does not prove foreground launch or remote delivery.
            result(true, if (handle.open) "Open request dispatched; Android may require interaction on the phone." else "Action dispatched to the app; delivery is controlled by that app.")
        } catch (_: PendingIntent.CanceledException) { handles.remove(request.actionId); result(false, "The app revoked this action.") }
        catch (_: Exception) { result(false, "Android did not allow this action.") }
    }
}
