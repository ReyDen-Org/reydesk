package ir.reyden.reydesk.companion

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import com.google.protobuf.ByteString
import ir.reyden.reydesk.protocol.v1.*
import kotlinx.coroutines.*
import java.io.File
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.util.UUID

data class FileTransferUi(val state: String = "idle", val bytes: Long = 0, val total: Long = 0, val incoming: FileTransferOffer? = null, val detail: String = "")

/** One explicitly selected transfer on one authenticated session. No public storage permission. */
class AndroidFileTransfer(
    private val context: Context,
    private val authorized: () -> Boolean,
    private val send: (Envelope) -> Boolean,
    private val report: (FileTransferUi) -> Unit,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lock = Any()
    @Volatile private var closed = false
    private val used = mutableSetOf<String>()
    private class Sending(val id: String) {
        val accepted = CompletableDeferred<Boolean>()
        var acknowledgement: CompletableDeferred<Boolean>? = null
        var expected = 0L
        var final = false
        var job: Job? = null
    }
    private class Receiving(val offer: FileTransferOffer) {
        @Volatile var ready = false
        val destination = CompletableDeferred<Uri?>()
        val chunks = kotlinx.coroutines.channels.Channel<FileTransferChunk>(1)
        var chosen: Uri? = null
        var rememberedTree: Uri? = null
        var job: Job? = null
    }
    private var sending: Sending? = null
    private var receiving: Receiving? = null
    private fun allowed() = !closed && authorized()
    private fun ensureAllowed() { if (!allowed()) throw CancellationException("Session ended") }
    private fun emit(envelope: Envelope) { ensureAllowed(); if (!send(envelope)) throw CancellationException("Control transport unavailable") }
    private fun response(id: String, accepted: Boolean) = Envelope.newBuilder().setFileTransferResponse(FileTransferResponse.newBuilder().setTransferId(id).setDecision(if (accepted) FileTransferDecision.FILE_TRANSFER_DECISION_ACCEPT else FileTransferDecision.FILE_TRANSFER_DECISION_REJECT)).build()
    private fun sendCancel(id: String) {
        if (allowed()) send(Envelope.newBuilder().setFileTransferControl(FileTransferControl.newBuilder().setTransferId(id).setRequestedState(FileTransferState.FILE_TRANSFER_STATE_CANCELED)).build())
    }

    fun sendFile(uri: Uri) {
        val state = Sending(UUID.randomUUID().toString().replace("-", ""))
        synchronized(lock) {
            if (!allowed() || sending != null || receiving != null || used.size >= 128) { report(FileTransferUi(detail = "Connect first and finish the current transfer.")); return }
            sending = state; used.add(state.id)
        }
        state.job = scope.launch {
            var temp: File? = null
            var total = 0L
            try {
                ensureAllowed()
                val displayName = context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { cursor -> if (cursor.moveToFirst()) cursor.getString(0) else null } ?: "shared-file"
                if (!FileTransferPolicy.safeName(displayName)) throw IllegalArgumentException()
                report(FileTransferUi("preparing", detail = "Preparing the selected file locally."))
                temp = File.createTempFile("reydesk-send-", ".part", context.cacheDir)
                val digest = MessageDigest.getInstance("SHA-256")
                context.contentResolver.openInputStream(uri)?.use { input ->
                    FileOutputStream(temp).use { output ->
                        val bytes = ByteArray(FileTransferPolicy.chunkBytes)
                        while (true) {
                            currentCoroutineContext().ensureActive(); ensureAllowed()
                            val count = input.read(bytes)
                            if (count < 0) break
                            if (count == 0) continue
                            total += count
                            if (total > FileTransferPolicy.maximumBytes) throw IllegalArgumentException()
                            output.write(bytes, 0, count); digest.update(bytes, 0, count)
                        }
                    }
                } ?: throw IllegalArgumentException()
                val hash = digest.digest().joinToString("") { "%02x".format(it) }
                emit(Envelope.newBuilder().setFileTransferOffer(FileTransferOffer.newBuilder().setTransferId(state.id).setFileName(displayName).setFileSizeBytes(total).setSha256Hex(hash)).build())
                report(FileTransferUi("waiting", total = total, detail = "Waiting for the computer to choose a destination."))
                if (!withTimeout(30000) { state.accepted.await() }) throw CancellationException("Declined")
                RandomAccessFile(temp, "r").use { input ->
                    var offset = 0L
                    val buffer = ByteArray(FileTransferPolicy.chunkBytes)
                    do {
                        currentCoroutineContext().ensureActive(); ensureAllowed()
                        val wanted = minOf(FileTransferPolicy.chunkBytes.toLong(), total - offset).toInt()
                        input.readFully(buffer, 0, wanted)
                        val final = offset + wanted == total
                        val acknowledgement = CompletableDeferred<Boolean>()
                        synchronized(lock) { state.expected = offset + wanted; state.final = final; state.acknowledgement = acknowledgement }
                        emit(Envelope.newBuilder().setFileTransferChunk(FileTransferChunk.newBuilder().setTransferId(state.id).setOffset(offset).setData(ByteString.copyFrom(buffer, 0, wanted)).setFinal(final)).build())
                        if (!withTimeout(30000) { acknowledgement.await() }) throw CancellationException("Transfer acknowledgement failed")
                        offset += wanted
                        report(FileTransferUi(if (final) "completed" else "sending", offset, total, detail = if (final) "Computer verified and saved the file." else ""))
                        if (final) break
                    } while (true)
                }
            } catch (_: Exception) {
                sendCancel(state.id)
                if (!closed) report(FileTransferUi("failed_or_canceled", total = total, detail = "Transfer stopped, was declined, or timed out. No delivery is claimed."))
            } finally {
                temp?.delete()
                synchronized(lock) { if (sending === state) sending = null }
            }
        }
    }

    fun handle(envelope: Envelope) {
        if (!allowed()) { cancel(); return }
        when {
            envelope.hasFileTransferResponse() -> synchronized(lock) {
                val response = envelope.fileTransferResponse
                sending?.takeIf { it.id == response.transferId }?.accepted?.complete(response.decision == FileTransferDecision.FILE_TRANSFER_DECISION_ACCEPT)
            }
            envelope.hasFileTransferProgress() -> synchronized(lock) {
                val progress = envelope.fileTransferProgress
                sending?.takeIf { it.id == progress.transferId }?.let { state ->
                    state.acknowledgement?.complete(progress.bytesTransferred == state.expected && progress.state == if (state.final) FileTransferState.FILE_TRANSFER_STATE_COMPLETED else FileTransferState.FILE_TRANSFER_STATE_IN_PROGRESS)
                }
            }
            envelope.hasFileTransferControl() -> {
                val id = envelope.fileTransferControl.transferId
                synchronized(lock) { sending?.takeIf { it.id == id }?.job?.cancel(); receiving?.takeIf { it.offer.transferId == id }?.job?.cancel() }
            }
            envelope.hasFileTransferOffer() -> receiveOffer(envelope.fileTransferOffer)
            envelope.hasFileTransferChunk() -> {
                val chunk = envelope.fileTransferChunk
                val receiver = synchronized(lock) { receiving?.takeIf { it.offer.transferId == chunk.transferId } }
                if (receiver == null || !receiver.ready || !receiver.chunks.trySend(chunk).isSuccess) {
                    sendCancel(chunk.transferId)
                    receiver?.destination?.complete(null)
                    receiver?.job?.cancel()
                }
            }
        }
    }

    private fun receiveOffer(offer: FileTransferOffer) {
        val state = Receiving(offer)
        synchronized(lock) {
            if (!FileTransferPolicy.validOffer(offer.transferId, offer.fileName, offer.fileSizeBytes, offer.sha256Hex) || sending != null || receiving != null || used.size >= 128 || !used.add(offer.transferId)) {
                if (offer.transferId.length <= 64) send(response(offer.transferId, false))
                return
            }
            receiving = state
        }
        state.job = scope.launch {
            var temp: File? = null
            var completed = false
            var offset = 0L
            var folderUnavailable = false
            try {
                val folders = ReceiveFolderStore(context)
                val remembered = folders.selected()
                val autoFolder = remembered?.takeIf { folders.available(it) }
                if (autoFolder != null) {
                    state.rememberedTree = autoFolder
                    state.destination.complete(autoFolder)
                } else report(FileTransferUi("offer", total = offer.fileSizeBytes, incoming = offer,
                    detail = if (remembered == null) "Choose a new destination to accept this file." else context.getString(ir.reyden.reydesk.R.string.receive_folder_unavailable)))
                var destination = withTimeout(30000) { state.destination.await() } ?: throw CancellationException("Declined")
                ensureAllowed()
                if (state.rememberedTree == null) state.chosen = destination
                temp = File.createTempFile("reydesk-receive-", ".part", context.cacheDir)
                val digest = MessageDigest.getInstance("SHA-256")
                state.ready = true
                emit(response(offer.transferId, true))
                report(FileTransferUi("receiving", total = offer.fileSizeBytes, detail = "Receiving into private staging; destination opens only after hash verification."))
                FileOutputStream(temp).use { staging ->
                    while (true) {
                        val chunk = withTimeout(30000) { state.chunks.receive() }
                        currentCoroutineContext().ensureActive(); ensureAllowed()
                        if (!FileTransferPolicy.validChunk(chunk.offset, offset, chunk.data.size(), offer.fileSizeBytes, chunk.final)) throw IllegalArgumentException()
                        val bytes = chunk.data.toByteArray()
                        staging.write(bytes); digest.update(bytes); offset += bytes.size
                        if (chunk.final) {
                            staging.flush()
                            val actual = digest.digest().joinToString("") { "%02x".format(it) }
                            if (!actual.equals(offer.sha256Hex, true)) throw IllegalArgumentException()
                            ensureAllowed()
                            state.rememberedTree?.let { tree ->
                                destination = try { folders.createNew(tree, offer.fileName) }
                                catch (error: Exception) { folderUnavailable = true; throw error }
                                state.chosen = destination
                            }
                            ensureAllowed()
                            // Only a new SAF document; open after integrity verification.
                            context.contentResolver.openOutputStream(destination, "w")?.use { output ->
                                temp.inputStream().use { input ->
                                    val buffer = ByteArray(FileTransferPolicy.chunkBytes)
                                    while (true) {
                                        currentCoroutineContext().ensureActive(); ensureAllowed()
                                        val count = input.read(buffer)
                                        if (count < 0) break
                                        output.write(buffer, 0, count)
                                    }
                                }
                                output.flush()
                            } ?: throw IllegalArgumentException()
                            ensureAllowed(); completed = true
                        }
                        emit(Envelope.newBuilder().setFileTransferProgress(FileTransferProgress.newBuilder().setTransferId(offer.transferId).setBytesTransferred(offset)
                            .setState(if (chunk.final) FileTransferState.FILE_TRANSFER_STATE_COMPLETED else FileTransferState.FILE_TRANSFER_STATE_IN_PROGRESS)).build())
                        report(FileTransferUi(if (chunk.final) "completed" else "receiving", offset, offer.fileSizeBytes, detail = if (chunk.final) "SHA-256 verified; file saved to your chosen destination." else ""))
                        if (chunk.final) break
                    }
                }
            } catch (_: Exception) {
                sendCancel(offer.transferId)
                if (!closed) report(FileTransferUi("failed_or_canceled", offset, offer.fileSizeBytes, detail = if (folderUnavailable) context.getString(ir.reyden.reydesk.R.string.receive_folder_unavailable) else "Transfer stopped or integrity verification failed."))
            } finally {
                temp?.delete(); state.chunks.close()
                if (!completed) state.chosen?.let { discardNewDocument(it) }
                synchronized(lock) { if (receiving === state) receiving = null }
            }
        }
    }

    fun chooseDestination(transferId: String, uri: Uri?) {
        val accepted = synchronized(lock) {
            val state = receiving?.takeIf { it.offer.transferId == transferId }
            if (state != null && allowed() && !state.destination.isCompleted) {
                state.chosen = uri
                state.destination.complete(uri)
            } else false
        }
        if (!accepted) {
            // A late result came from our own CreateDocument picker, never a remote path.
            if (uri != null) java.util.concurrent.CompletableFuture.runAsync { discardNewDocument(uri) }
        }
    }
    private fun discardNewDocument(uri: Uri) {
        try { if (!DocumentsContract.deleteDocument(context.contentResolver, uri) && !closed) report(FileTransferUi("cleanup_failed", detail = "Remove the empty or partial file at the destination you selected.")) }
        catch (_: Exception) { if (!closed) report(FileTransferUi("cleanup_failed", detail = "Remove the empty or partial file at the destination you selected.")) }
    }
    fun cancel() = synchronized(lock) {
        sending?.let { sendCancel(it.id); it.job?.cancel() }
        receiving?.let { sendCancel(it.offer.transferId); it.job?.cancel() }
    }
    fun close() { closed = true; cancel(); scope.cancel(); report(FileTransferUi(detail = "Disconnected; transfer handles cleared.")) }
}
