package ir.reyden.reydesk.ui.session

import android.content.Context
import android.graphics.SurfaceTexture
import android.view.TextureView
import android.view.Gravity
import android.util.Log
import android.widget.FrameLayout
import org.webrtc.EglBase
import org.webrtc.EglRenderer
import org.webrtc.GlRectDrawer
import org.webrtc.VideoFrame
import org.webrtc.VideoSink
import kotlin.math.roundToInt

/** A real TextureView is transformed/clipped by Android's view compositor.
 * SurfaceView's separate surface must not be scaled using Compose graphicsLayer. */
internal class ZoomVideoRenderer(
    context: Context,
    eglContext: EglBase.Context,
    private val onAspectChanged: (Float) -> Unit,
) : FrameLayout(context), VideoSink {
    private val eglRenderer = EglRenderer("ReyDeskTexture")
    private val texture = TextureView(context)
    private val rendererId = Integer.toHexString(System.identityHashCode(this))
    @Volatile private var released = false
    private var lastWidth = 0
    private var lastHeight = 0

    init {
        clipChildren = true
        clipToPadding = true
        eglRenderer.init(eglContext, EglBase.CONFIG_PLAIN, GlRectDrawer())
        Log.d("ReyDeskTexture", "renderer=$rendererId initialized")
        texture.isOpaque = true
        texture.pivotX = 0f
        texture.pivotY = 0f
        texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(surface: SurfaceTexture, width: Int, height: Int) {
                if (!released) {
                    Log.d("ReyDeskTexture", "renderer=$rendererId surface available ${width}x$height")
                    eglRenderer.createEglSurface(surface)
                }
            }
            override fun onSurfaceTextureSizeChanged(surface: SurfaceTexture, width: Int, height: Int) = Unit
            override fun onSurfaceTextureUpdated(surface: SurfaceTexture) = Unit
            override fun onSurfaceTextureDestroyed(surface: SurfaceTexture): Boolean {
                Log.d("ReyDeskTexture", "renderer=$rendererId surface destroyed")
                // Let EGL finish before TextureView releases its surface texture.
                val done = java.util.concurrent.CountDownLatch(1)
                eglRenderer.releaseEglSurface { done.countDown() }
                org.webrtc.ThreadUtils.awaitUninterruptibly(done)
                return true
            }
        }
        addView(texture, LayoutParams(1, 1, Gravity.TOP or Gravity.LEFT))
    }

    fun show(viewport: VideoViewport) {
        val w = viewport.videoWidth.roundToInt().coerceAtLeast(1)
        val h = viewport.videoHeight.roundToInt().coerceAtLeast(1)
        if (texture.layoutParams.width != w || texture.layoutParams.height != h) {
            // Remote pixel coordinates stay left-to-right even in Persian UI.
            texture.layoutParams = LayoutParams(w, h, Gravity.TOP or Gravity.LEFT)
        }
        eglRenderer.setLayoutAspectRatio(w.toFloat() / h)
        texture.scaleX = viewport.zoom
        texture.scaleY = viewport.zoom
        texture.translationX = viewport.left
        texture.translationY = viewport.top
    }

    override fun onFrame(frame: VideoFrame) {
        if (released) return
        val w = frame.rotatedWidth
        val h = frame.rotatedHeight
        if (w != lastWidth || h != lastHeight) {
            Log.d("ReyDeskTexture", "renderer=$rendererId frame size ${w}x$h attached=$isAttachedToWindow")
            lastWidth = w
            lastHeight = h
            post { if (!released) onAspectChanged(w.toFloat() / h) }
        }
        eglRenderer.onFrame(frame)
    }

    fun release() {
        released = true
        Log.d("ReyDeskTexture", "renderer=$rendererId released")
        eglRenderer.release()
    }
}
