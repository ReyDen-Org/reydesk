package ir.reyden.reydesk.companion

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import ir.reyden.reydesk.ReyDeskApplication

/** Bound only by Android after the owner explicitly grants notification access. */
class PhoneNotificationService : NotificationListenerService() {
    private val hub get() = (applicationContext as ReyDeskApplication).session.phoneHub
    private val lockReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) { hub.refresh() }
    }
    override fun onCreate() {
        super.onCreate()
        registerReceiver(lockReceiver, IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_OFF); addAction(Intent.ACTION_USER_PRESENT); addAction(Intent.ACTION_USER_UNLOCKED)
        })
    }
    override fun onListenerConnected() { hub.attach(this) }
    override fun onListenerDisconnected() { hub.detach(this) }
    override fun onNotificationPosted(sbn: StatusBarNotification?) { if (sbn != null) hub.posted(sbn) }
    override fun onNotificationRemoved(sbn: StatusBarNotification?) { if (sbn != null) hub.removed(sbn.key) }
    override fun onDestroy() { hub.detach(this); unregisterReceiver(lockReceiver); super.onDestroy() }
}
