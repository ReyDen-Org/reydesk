package ir.reyden.reydesk.ui.session

import kotlin.math.max
import kotlin.math.min

/** Pixel geometry shared by the displayed texture and inverse touch mapping. */
internal data class VideoViewport(
    val width: Float,
    val height: Float,
    val aspect: Float,
    val zoom: Float = 1f,
    val panX: Float = 0f,
    val panY: Float = 0f,
) {
    val videoWidth: Float get() = min(width, height * aspect)
    val videoHeight: Float get() = videoWidth / aspect
    val left: Float get() = (width - videoWidth * zoom) / 2f + panX
    val top: Float get() = (height - videoHeight * zoom) / 2f + panY

    fun normalized(x: Float, y: Float): Pair<Float, Float>? {
        if (width <= 0 || height <= 0) return null
        val nx = (x - left) / (videoWidth * zoom)
        val ny = (y - top) / (videoHeight * zoom)
        return if (nx in 0f..1f && ny in 0f..1f) nx to ny else null
    }

    fun transform(focalX: Float, focalY: Float, factor: Float, dx: Float, dy: Float): VideoViewport {
        val newZoom = (zoom * factor).coerceIn(1f, 4f)
        val ratio = newZoom / zoom
        return copy(
            zoom = newZoom,
            panX = (focalX - width / 2f) * (1f - ratio) + panX * ratio + dx,
            panY = (focalY - height / 2f) * (1f - ratio) + panY * ratio + dy,
        ).bounded()
    }

    fun bounded(): VideoViewport {
        val limitX = max(0f, (videoWidth * zoom - width) / 2f)
        val limitY = max(0f, (videoHeight * zoom - height) / 2f)
        return copy(panX = panX.coerceIn(-limitX, limitX), panY = panY.coerceIn(-limitY, limitY))
    }
}
