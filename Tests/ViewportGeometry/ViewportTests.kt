import ir.reyden.reydesk.ui.session.VideoViewport
import kotlin.math.abs

fun main() {
    var checks = 0
    fun check(ok: Boolean, name: String) {
        if (!ok) error(name)
        checks++
        println("PASS: $name")
    }
    fun near(a: Float, b: Float) = abs(a - b) < 0.001f
    val wide = VideoViewport(2400f, 1080f, 16f / 9f)
    check(near(wide.videoWidth, 1920f) && near(wide.videoHeight, 1080f), "fit uses full height without crop on wide phone")
    check(wide.normalized(100f, 400f) == null, "left letterbox cannot send remote click")
    check(wide.normalized(2300f, 400f) == null, "right letterbox cannot send remote click")
    check(wide.normalized(240f, 0f) == (0f to 0f), "visible top-left maps to remote origin")
    check(wide.normalized(2160f, 1080f) == (1f to 1f), "visible bottom-right maps to remote endpoint")
    check(wide.normalized(1200f, 540f) == (0.5f to 0.5f), "center remains center")
    val portrait = VideoViewport(1080f, 2400f, 16f / 9f)
    check(portrait.normalized(540f, 100f) == null, "top letterbox rejected in narrow viewport")
    check(portrait.normalized(540f, 2300f) == null, "bottom letterbox rejected in narrow viewport")
    check(portrait.normalized(540f, 1200f) == (0.5f to 0.5f), "narrow viewport center mapping")
    val zoomed = wide.transform(1600f, 650f, 2f, 0f, 0f)
    val before = wide.normalized(1600f, 650f)!!
    val after = zoomed.normalized(1600f, 650f)!!
    check(near(before.first, after.first) && near(before.second, after.second), "pinch preserves focal remote point")
    val panned = zoomed.transform(1200f, 540f, 1f, 160f, -80f)
    val moved = panned.normalized(1760f, 570f)!!
    check(near(before.first, moved.first) && near(before.second, moved.second), "two-finger pan moves same pixel with fingers")
    val maxPan = zoomed.transform(1200f, 540f, 1f, 100000f, 100000f)
    check(near(maxPan.left, 0f) && near(maxPan.top, 0f), "pan stops at desktop top-left instead of exposing blank space")
    val minPan = zoomed.transform(1200f, 540f, 1f, -100000f, -100000f)
    check(near(minPan.left + minPan.videoWidth * minPan.zoom, wide.width), "pan stops at desktop right edge")
    check(near(minPan.top + minPan.videoHeight * minPan.zoom, wide.height), "pan stops at desktop bottom edge")
    check(near(minPan.normalized(wide.width, wide.height)!!.first, 1f), "zoomed right edge click targets remote right edge")
    check(near(minPan.normalized(wide.width, wide.height)!!.second, 1f), "zoomed bottom edge click targets remote bottom edge")
    check(wide.transform(1200f, 540f, 100f, 0f, 0f).zoom == 4f, "zoom capped at4x")
    val reset = maxPan.transform(1200f, 540f, 0.001f, 500f, 500f)
    check(reset.zoom == 1f && reset.panX == 0f && reset.panY == 0f, "zoom-out returns to centered fit")
    val square = wide.copy(aspect = 1f).bounded()
    check(square.videoWidth == 1080f && square.videoHeight == 1080f, "actual incoming aspect controls fit")
    check(square.normalized(1200f, 540f) == (0.5f to 0.5f), "non16:9 incoming image maps correctly")
    check(VideoViewport(0f, 0f, 1f).normalized(0f, 0f) == null, "unmeasured viewport cannot send invalid coordinates")
    println("$checks viewport geometry checks passed")
}
