package ir.reyden.reydesk

import android.app.Application

/** No Application subclass existed before this - added so long-lived services
 * (SessionCoordinator) have one clear owner for the app's whole lifetime, the same
 * role App.xaml.cs plays on Windows. */
class ReyDeskApplication : Application() {
    lateinit var session: SessionCoordinator
        private set

    override fun onCreate() {
        super.onCreate()
        session = SessionCoordinator(applicationContext)
    }
}
