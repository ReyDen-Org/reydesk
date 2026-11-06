package ir.reyden.reydesk.ui.connection

import android.content.Context
import ir.reyden.reydesk.R
import ir.reyden.reydesk.pairing.LanOnlyUnreachableException
import ir.reyden.reydesk.pairing.PairingCancelledException
import ir.reyden.reydesk.pairing.PairingRejectedException
import ir.reyden.reydesk.pairing.RemoteUnavailableException
import ir.reyden.reydesk.protocol.rendezvous.v1.PipeFailReason
import ir.reyden.reydesk.protocol.v1.PairingResponse
import ir.reyden.reydesk.protocol.v1.PairingResult
import ir.reyden.reydesk.rendezvous.PipeFailedException

/**
 * Turns a pairing failure into a localized sentence that says what failed: wrong code, PC unavailable, no answer, declined
 * on the PC, codes not confirmed, and so on. The server answers UNAVAILABLE for offline, internet off, no code and declined
 * alike (on purpose, so a stranger cannot probe a Desk ID), so the phone can only say "not available" for those.
 * Host reasons are the PC's fixed strings from Pairing/PairingListener.cs; unknown ones are shown as they are.
 */
internal fun pairingFailureText(context: Context, error: Throwable?, response: PairingResponse? = null): String {
    val id = when (error) {
        null -> response?.let { hostReasonRes(it.reason, it.result) }
        is PipeFailedException -> when (error.reason) {
            PipeFailReason.PIPE_FAIL_REASON_BAD_CODE -> R.string.pair_err_wrong_code
            PipeFailReason.PIPE_FAIL_REASON_TIMEOUT -> R.string.pair_err_no_answer
            PipeFailReason.PIPE_FAIL_REASON_RATE_LIMITED -> R.string.pair_err_rate_limited
            else -> R.string.pair_err_pc_unavailable
        }
        is PairingCancelledException -> R.string.pair_err_not_confirmed
        is LanOnlyUnreachableException -> R.string.pair_err_lan_unreachable
        is RemoteUnavailableException -> R.string.pair_err_phone_offline
        is PairingRejectedException -> hostReasonRes(error.message.orEmpty(), PairingResult.PAIRING_RESULT_REJECTED)
        else -> when (error.message) {
            "The PC did not respond in time.", "The PC never answered the pairing request." -> R.string.pair_err_no_answer
            "Not connected to the rendezvous server." -> R.string.pair_err_phone_offline
            "Pairing offer expired. Regenerate it on the PC." -> R.string.pair_err_offer_expired
            else -> null
        }
    }
    if (id != null) return context.getString(id)
    return error?.message ?: error?.javaClass?.simpleName ?: response?.reason?.ifEmpty { null } ?: response?.result?.name.orEmpty()
}

private fun hostReasonRes(reason: String, result: PairingResult): Int? = when {
    result == PairingResult.PAIRING_RESULT_EXPIRED_TOKEN -> R.string.pair_err_offer_expired
    reason == "Pairing was not approved on the PC." || reason == "Pairing was cancelled on the PC." ||
        reason == "The connection was declined on the PC." || reason == "Pairing was closed on the PC during approval." ->
        R.string.pair_err_declined
    reason == "The internet pairing code is not active. Create a new one on the PC." -> R.string.pair_err_code_expired
    reason == "Codes were not confirmed." || reason == "Code comparison failed." -> R.string.pair_err_not_confirmed
    reason.contains("paused") -> R.string.pair_err_paused
    // Single-use QR token already spent (the first two are the pre-2026-11 PC wording).
    reason == "This QR code was already used. Regenerate it on the PC." ||
        reason.startsWith("Pairing attempts exhausted") || reason.startsWith("Pairing code has already been used") -> R.string.pair_err_qr_used
    reason.startsWith("Too many attempts with this QR code") ||
        reason == "Pairing code changed or expired during approval." -> R.string.pair_err_offer_expired
    reason == "Invalid pairing request or code." -> R.string.pair_err_wrong_code
    reason == "Internet access is not set up on this PC." -> R.string.pair_err_pc_unavailable
    else -> null
}
