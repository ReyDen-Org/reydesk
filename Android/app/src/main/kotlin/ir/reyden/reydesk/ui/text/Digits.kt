package ir.reyden.reydesk.ui.text

/**
 * Digit localization for counts, times, percentages and sizes inside sentences.
 * FA -> Persian digits, everything else stays Latin.
 * Desk ID, Key ID, SAS, remote code, IP, port, version and build must NOT go through this;
 * show them Latin in a mono LTR run (see [ltr]).
 */
object Digits {
    private const val PERSIAN_ZERO = '۰'

    /** [language] is a language code such as "fa" or "en". */
    fun localize(text: CharSequence, language: String): String {
        val zero = when (language.lowercase().substringBefore('-')) {
            "fa" -> PERSIAN_ZERO
            else -> return text.toString()
        }
        val sb = StringBuilder(text.length)
        for (ch in text) sb.append(if (ch in '0'..'9') zero + (ch - '0') else ch)
        return sb.toString()
    }

    fun localize(value: Long, language: String): String = localize(value.toString(), language)

    /** Normalizes Persian and Arabic-Indic digits to Latin (for input fields). */
    fun toLatin(text: CharSequence): String {
        val sb = StringBuilder(text.length)
        for (ch in text) {
            sb.append(
                when (ch) {
                    in '۰'..'۹' -> '0' + (ch - '۰')
                    in '٠'..'٩' -> '0' + (ch - '٠')
                    else -> ch
                }
            )
        }
        return sb.toString()
    }

    /** Wraps a value in LRI..PDI so it keeps LTR order inside RTL text. */
    fun ltr(text: String): String = "⁦$text⁩"
}
