package ir.reyden.reydesk.companion

/** Conservative, local-only policy; previews are opt-in and sensitive content never leaves. */
object NotificationPrivacy {
    fun redact(previews: Boolean, packageName: String, category: String, title: String, text: String, systemHidden: Boolean = false, allowSensitive: Boolean = false): Boolean {
        return reason(previews, packageName, category, title, text, systemHidden, allowSensitive) != null
    }

    /** Fixed reason codes only: no matched term or message content is returned. */
    fun reason(previews: Boolean, packageName: String, category: String, title: String, text: String, systemHidden: Boolean = false, allowSensitive: Boolean = false): String? {
        if (!previews) return "previews_off"
        if (systemHidden) return "system_secret"
        // Opt-in: the user chose to also see codes and bank messages on their own PC.
        if (allowSensitive) return null
        val source = "$packageName $category $title $text".lowercase()
        if (listOf("bank", "banking", "wallet", "auth", "otp", "password", "passcode", "verification", "verify", "security", "one-time", "one time", "2fa", "بانک", "رمز", "تأیید", "تایید", "امنیت", "کد ورود", "پرداخت", "تراکنش", "برداشت", "واریز", "موجودی", "transaction", "payment", "credit", "debit", "paypal", "revolut", "mellat", "pasargad", "samanbank", "tejarat", "sepah").any { source.contains(it) }) return "sensitive_term"
        // Only the message text is checked for code-like digits: a sender title such as an 8-digit SMS short code is not a secret.
        val body = text.lowercase()
        if (
            Regex("(?<![0-9])[0-9]{4,8}(?![0-9])").containsMatchIn(body) ||
            Regex("[۰-۹٠-٩]{4,8}").containsMatchIn(body)
        ) return "numeric_code"
        return null
    }

    // Reading opted-in ordinary content does not require unlocking the phone.
    // Invoking its actions still does, including handles issued before the phone locked.
    fun allowsActions(locked: Boolean, redacted: Boolean): Boolean = !locked && !redacted

    fun acceptsReply(text: String, freeForm: Boolean, choices: List<String>): Boolean =
        text.isNotBlank() && text.length <= 2000 && (freeForm || choices.contains(text))
}
