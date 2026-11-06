import ir.reyden.reydesk.ui.connection.deskIdDisplay
import ir.reyden.reydesk.ui.connection.deskIdRaw
import ir.reyden.reydesk.ui.connection.deskIdToDisplayOffset
import ir.reyden.reydesk.ui.connection.deskIdToRawOffset

private var failures = 0
private fun eq(name: String, expected: Any?, actual: Any?) {
    if (expected != actual) { failures++; println("FAIL $name: expected <$expected> got <$actual>") } else println("ok   $name")
}

fun main() {
    // type 5 chars: raw has no dash, display adds it, cursor lands after the 5th char
    val five = deskIdRaw("7KQ4S")
    eq("five raw", "7KQ4S", five)
    eq("five display", "7KQ4-S", deskIdDisplay(five))
    eq("cursor after 5th", 6, deskIdToDisplayOffset(5))
    eq("cursor after 4th (no dash yet)", "7KQ4", deskIdDisplay("7KQ4"))
    eq("cursor offset 4", 4, deskIdToDisplayOffset(4))
    eq("full length offset", 14, deskIdToDisplayOffset(12))

    // paste with dashes, spaces, lowercase, Persian digit, I/L/O, overflow
    eq("paste", "7KQ4MZ2PX9DA", deskIdRaw("7kq4-mz2p x9da"))
    eq("paste persian", "7KQ4MZ2PX9DA", deskIdRaw("۷kq4-mz2p-x9da"))
    eq("paste ilo", "110MZ2PX9DA", deskIdRaw("ilo-mz2p-x9da"))
    eq("paste overflow", "7KQ4MZ2PX9DA", deskIdRaw("7KQ4-MZ2P-X9DA-EXTRA"))

    // backspace across the separator: cursor after the dash (display 5) maps to raw 4, so backspace removes raw[3]
    eq("after dash maps before 5th", 4, deskIdToRawOffset(5))
    eq("before dash", 4, deskIdToRawOffset(4))
    val afterBackspace = deskIdRaw("7KQ4S".removeRange(3, 4))
    eq("backspace raw", "7KQS", afterBackspace)
    eq("backspace display", "7KQS", deskIdDisplay(afterBackspace))

    // mid-string insert: insert X at raw offset 2 of 7KQ4MZ2P, cursor follows
    val before = "7KQ4MZ2P"
    val inserted = deskIdRaw(before.substring(0, 2) + "X" + before.substring(2))
    eq("mid insert raw", "7KXQ4MZ2P", inserted)
    eq("mid insert display", "7KXQ-4MZ2-P", deskIdDisplay(inserted))
    eq("mid insert cursor", 3, deskIdToDisplayOffset(3))
    // insert at raw offset 4 of 8 chars pushes into the next group; cursor after it = display 6
    eq("insert at group boundary", "7KQ4-XMZ2-P", deskIdDisplay(deskIdRaw("7KQ4" + "X" + "MZ2P")))
    eq("cursor after boundary insert", 6, deskIdToDisplayOffset(5))

    // mapping round trip for every raw offset
    for (o in 0..12) eq("roundtrip $o", o, deskIdToRawOffset(deskIdToDisplayOffset(o)))
    if (failures > 0) { println("$failures failure(s)"); kotlin.system.exitProcess(1) }
    println("All DeskIdInput tests passed")
}
