package ir.reyden.reydesk.identity

import java.security.MessageDigest

/**
 * ReyDesk v2 identifiers (docs/identity-and-pairing.md 1.2, 1.3, 1.6).
 * Mirrors the Windows codec; byte layouts follow Tests/Vectors/identity-v2.json "notes".
 */
object DeskIdCodec {
    const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    enum class ParseError { BAD_CHAR, BAD_LENGTH, BAD_CHECK }

    sealed class Result<out T> {
        data class Ok<T>(val value: T) : Result<T>()
        data class Err(val error: ParseError) : Result<Nothing>()
    }

    private fun isSeparator(c: Char) = c == '-' || c == ' ' ||
        c == '‎' || c == '‏' || c in '⁦'..'⁩'

    /** Persian U+06F0-06F9 and Arabic-Indic U+0660-0669 only; no other digit sets. */
    private fun mapDigit(c: Char): Char = when (c) {
        in '۰'..'۹' -> '0' + (c - '۰')
        in '٠'..'٩' -> '0' + (c - '٠')
        else -> c
    }

    /** Encodes 55 bits as 11 data chars plus the check char (12 chars). */
    fun fromBits(bits: Long): String {
        require(bits in 0 until (1L shl 55)) { "Desk ID data must be 55 bits" }
        val data = String(CharArray(11) { i -> ALPHABET[((bits ushr (50 - 5 * i)) and 31).toInt()] })
        return data + checkChar(data)
    }

    fun checkChar(data11: String): Char {
        var sum = 0
        for (i in 0 until 11) sum += (i + 1) * ALPHABET.indexOf(data11[i])
        return ALPHABET[sum % 31]
    }

    /** Normalizes and validates; error precedence BAD_CHAR, BAD_LENGTH, BAD_CHECK. */
    fun parse(input: String): Result<String> {
        val sb = StringBuilder()
        for (raw in input) {
            if (isSeparator(raw)) continue
            var c = mapDigit(raw)
            if (c in 'a'..'z') c = c - 32
            when (c) {
                'I', 'L' -> c = '1'
                'O' -> c = '0'
                'U' -> continue
            }
            if (ALPHABET.indexOf(c) < 0) return Result.Err(ParseError.BAD_CHAR)
            sb.append(c)
        }
        if (sb.length != 12) return Result.Err(ParseError.BAD_LENGTH)
        val s = sb.toString()
        if (checkChar(s.substring(0, 11)) != s[11]) return Result.Err(ParseError.BAD_CHECK)
        return Result.Ok(s)
    }

    fun check(input: String): Boolean = parse(input) is Result.Ok

    /** "XXXX-XXXX-XXXX" (callers wrap in LTR isolates for RTL UI). */
    fun display(canonical: String): String = canonical.chunked(4).joinToString("-")

    private fun base32(bits: ByteArray, chars: Int): String {
        val sb = StringBuilder(chars)
        for (i in 0 until chars) {
            var v = 0
            for (b in 0 until 5) {
                val pos = i * 5 + b
                val bit = if (pos / 8 < bits.size) (bits[pos / 8].toInt() shr (7 - pos % 8)) and 1 else 0
                v = (v shl 1) or bit
            }
            sb.append(ALPHABET[v])
        }
        return sb.toString()
    }

    fun fingerprint(spki: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(spki)

    /** Top 60 bits of the fingerprint as 12 chars. */
    fun keyId(fingerprint: ByteArray): String = base32(fingerprint, 12)

    fun keyIdDisplay(fingerprint: ByteArray): String = display(keyId(fingerprint))

    /** 256 bits + 4 zero bits = 52 chars in 13 groups of 4 separated by a space. */
    fun fingerprintDisplay(fingerprint: ByteArray): String =
        base32(fingerprint, 52).chunked(4).joinToString(" ")

    /** Remote pairing code: exactly 6 digits after normalization. */
    fun parseRemoteCode(input: String): Result<String> {
        val sb = StringBuilder()
        for (raw in input) {
            if (isSeparator(raw)) continue
            val c = mapDigit(raw)
            if (c !in '0'..'9') return Result.Err(ParseError.BAD_CHAR)
            sb.append(c)
        }
        return if (sb.length == 6) Result.Ok(sb.toString()) else Result.Err(ParseError.BAD_LENGTH)
    }
}
