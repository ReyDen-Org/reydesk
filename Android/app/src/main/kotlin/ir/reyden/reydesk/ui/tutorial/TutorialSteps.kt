package ir.reyden.reydesk.ui.tutorial

import ir.reyden.reydesk.R

/** The four step lists. [After] is the shared d1 to d4 list on its own. */
enum class TutPath(val key: String) {
    Wifi("wifi"), Internet("internet"), Mobile("mobile"), After("after");

    companion object {
        fun from(key: String?): TutPath = entries.firstOrNull { it.key == key } ?: Wifi
    }
}

/** The pairing method picked at a fork step (a3: Qr or Nearby, b3: Qr or Code). */
enum class TutMethod(val key: String) {
    Qr("qr"), Nearby("nearby"), Code("code");

    companion object {
        fun from(key: String?, fallback: TutMethod = Qr): TutMethod = entries.firstOrNull { it.key == key } ?: fallback
    }
}

/** "Show me": navigate to [route] and highlight the anchor named [target] (null = just navigate). */
data class TutShow(val route: String, val target: String? = null)

/** A ghost button inside the card that switches the guide to another path. */
data class TutPathLink(val path: TutPath, val labelRes: Int)

class TutStep(
    val id: String,
    val titleRes: Int,
    val pcRes: Int,
    val phoneRes: Int,
    val show: TutShow? = null,
    val pathLink: TutPathLink? = null,
    /** Non-null on the fork steps a3 and b3: the two methods offered. */
    val fork: List<TutMethod>? = null,
)

private val a1 = TutStep("a1", R.string.tut_a1_title, R.string.tut_a1_pc, R.string.tut_a1_phone, TutShow("connection"))
private val a2 = TutStep("a2", R.string.tut_a2_title, R.string.tut_a2_pc, R.string.tut_a2_phone, TutShow("connection", "mode"))
private val a3 = TutStep("a3", R.string.tut_a3_title, R.string.tut_a3_pc, R.string.tut_a3_phone, fork = listOf(TutMethod.Qr, TutMethod.Nearby))
private val a4q = TutStep("a4q", R.string.tut_a4q_title, R.string.tut_a4q_pc, R.string.tut_a4q_phone, TutShow("connection", "ring"))
private val a4n = TutStep("a4n", R.string.tut_a4n_title, R.string.tut_a4n_pc, R.string.tut_a4n_phone, TutShow("connection", "nearby"))
private val a5n = TutStep("a5n", R.string.tut_a5n_title, R.string.tut_a5n_pc, R.string.tut_a5n_phone)
private val a6 = TutStep("a6", R.string.tut_a6_title, R.string.tut_a6_pc, R.string.tut_a6_phone)

private val b1 = TutStep("b1", R.string.tut_b1_title, R.string.tut_b1_pc, R.string.tut_b1_phone, TutShow("settings", "settings_internet"))
private val b2 = TutStep("b2", R.string.tut_b2_title, R.string.tut_b2_pc, R.string.tut_b2_phone)
private val b3 = TutStep("b3", R.string.tut_b3_title, R.string.tut_b3_pc, R.string.tut_b3_phone, fork = listOf(TutMethod.Code, TutMethod.Qr))
private val b4c = TutStep("b4c", R.string.tut_b4c_title, R.string.tut_b4c_pc, R.string.tut_b4c_phone, TutShow("connection", "internet"))
private val b5c = TutStep("b5c", R.string.tut_b5c_title, R.string.tut_b5c_pc, R.string.tut_b5c_phone, TutShow("connection", "internet"))
private val b6c = TutStep("b6c", R.string.tut_b6c_title, R.string.tut_b6c_pc, R.string.tut_b6c_phone)
private val b4q = TutStep("b4q", R.string.tut_b4q_title, R.string.tut_b4q_pc, R.string.tut_b4q_phone, TutShow("connection", "ring"))
private val b7 = TutStep("b7", R.string.tut_b7_title, R.string.tut_b7_pc, R.string.tut_b7_phone, pathLink = TutPathLink(TutPath.Mobile, R.string.tut_openMobile))

private val c1 = TutStep("c1", R.string.tut_c1_title, R.string.tut_c1_pc, R.string.tut_c1_phone)
private val c2 = TutStep("c2", R.string.tut_c2_title, R.string.tut_c2_pc, R.string.tut_c2_phone, TutShow("settings", "settings_relay"))
private val c3 = TutStep("c3", R.string.tut_c3_title, R.string.tut_c3_pc, R.string.tut_c3_phone)
private val c4 = TutStep("c4", R.string.tut_c4_title, R.string.tut_c4_pc, R.string.tut_c4_phone, pathLink = TutPathLink(TutPath.Internet, R.string.tut_openInternet))

private val d1 = TutStep("d1", R.string.tut_d1_title, R.string.tut_d1_pc, R.string.tut_d1_phone)
private val d2 = TutStep("d2", R.string.tut_d2_title, R.string.tut_d2_pc, R.string.tut_d2_phone, TutShow("devices"))
private val d3 = TutStep("d3", R.string.tut_d3_title, R.string.tut_d3_pc, R.string.tut_d3_phone, TutShow("connection", "nearby"))
private val d4 = TutStep("d4", R.string.tut_d4_title, R.string.tut_d4_pc, R.string.tut_d4_phone, TutShow("devices"))
private val shared = listOf(d1, d2, d3, d4)

/** wifi: 9 (QR) or 10 (Nearby); internet: 11 (Code) or 9 (QR); mobile: 4; after: 4. */
fun tutorialSteps(path: TutPath, wifiMethod: TutMethod, internetMethod: TutMethod): List<TutStep> = when (path) {
    TutPath.Wifi -> listOf(a1, a2, a3) +
        (if (wifiMethod == TutMethod.Nearby) listOf(a4n, a5n) else listOf(a4q)) + a6 + shared
    TutPath.Internet -> listOf(b1, b2, b3) +
        (if (internetMethod == TutMethod.Code) listOf(b4c, b5c, b6c) else listOf(b4q)) + b7 + shared
    TutPath.Mobile -> listOf(c1, c2, c3, c4)
    TutPath.After -> shared
}
