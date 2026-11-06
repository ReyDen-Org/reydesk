package ir.reyden.reydesk.ui.connection

import ir.reyden.reydesk.identity.DeskIdCodec
import ir.reyden.reydesk.ui.text.Digits

/** Desk ID: 11 data chars plus the check char (docs/identity-and-pairing.md 1.2). */
internal const val DESK_ID_LENGTH = 12

/**
 * Raw field value: Crockford characters only (alphabet from DeskIdCodec), uppercase, Latin digits,
 * I/L -> 1 and O -> 0 as DeskIdCodec.parse does. Dashes, spaces and anything else are dropped,
 * so a pasted "7kq4-mz2p x9da" becomes "7KQ4MZ2PX9DA". Limited to [DESK_ID_LENGTH].
 */
internal fun deskIdRaw(input: String): String {
    val sb = StringBuilder()
    for (ch in Digits.toLatin(input)) {
        var c = ch.uppercaseChar()
        when (c) { 'I', 'L' -> c = '1'; 'O' -> c = '0' }
        if (DeskIdCodec.ALPHABET.indexOf(c) >= 0) sb.append(c)
        if (sb.length == DESK_ID_LENGTH) break
    }
    return sb.toString()
}

/** "XXXX-XXXX-XXXX" from a raw value; a dash only appears once a character follows it. */
internal fun deskIdDisplay(raw: String): String = raw.chunked(4).joinToString("-")

/** Raw cursor offset -> displayed offset (a dash sits before raw index 4 and 8). */
internal fun deskIdToDisplayOffset(offset: Int): Int =
    if (offset <= 0) 0 else offset + (offset - 1) / 4

/** Displayed cursor offset (dashes at display index 4 and 9) -> raw offset. */
internal fun deskIdToRawOffset(offset: Int): Int =
    offset - (if (offset >= 5) 1 else 0) - (if (offset >= 10) 1 else 0)
