package ir.reyden.reydesk.companion

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.DocumentsContract

/** A folder granted by the owner through OpenDocumentTree, never a remote path. */
class ReceiveFolderStore(private val context: Context) {
    private val prefs = context.getSharedPreferences("reydesk_receive_folder", Context.MODE_PRIVATE)
    fun selected(): Uri? = prefs.getString("tree", null)?.let { runCatching { Uri.parse(it) }.getOrNull() }
    fun remember(uri: Uri) {
        require(DocumentsContract.isTreeUri(uri))
        val previous = selected()
        context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        if (!available(uri)) {
            if (uri != previous) release(uri)
            throw java.io.IOException("Receive folder unavailable")
        }
        prefs.edit().putString("tree", uri.toString()).apply()
        if (previous != null && previous != uri) release(previous)
    }
    fun forget() { val uri = selected(); prefs.edit().remove("tree").apply(); if (uri != null) release(uri) }
    private fun release(uri: Uri) { runCatching { context.contentResolver.releasePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION) } }
    fun available(uri: Uri): Boolean = runCatching {
        if (context.contentResolver.persistedUriPermissions.none { it.uri == uri && it.isReadPermission && it.isWritePermission }) false
        else {
        val document = DocumentsContract.buildDocumentUriUsingTree(uri, DocumentsContract.getTreeDocumentId(uri))
        context.contentResolver.query(document, arrayOf(DocumentsContract.Document.COLUMN_MIME_TYPE, DocumentsContract.Document.COLUMN_FLAGS), null, null, null)?.use { c ->
            c.moveToFirst() && c.getString(0) == DocumentsContract.Document.MIME_TYPE_DIR && (c.getLong(1) and DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE.toLong()) != 0L
        } ?: false
        }
    }.getOrDefault(false)
    fun label(uri: Uri): String = runCatching {
        val document = DocumentsContract.buildDocumentUriUsingTree(uri, DocumentsContract.getTreeDocumentId(uri))
        context.contentResolver.query(document, arrayOf(DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else null } ?: uri.toString()
    }.getOrDefault(uri.toString())

    fun createNew(uri: Uri, name: String): Uri {
        require(FileTransferPolicy.safeName(name) && available(uri))
        val parentId = DocumentsContract.getTreeDocumentId(uri)
        val parent = DocumentsContract.buildDocumentUriUsingTree(uri, parentId)
        val children = DocumentsContract.buildChildDocumentsUriUsingTree(uri, parentId)
        val names = mutableSetOf<String>(); val ids = mutableSetOf<String>()
        context.contentResolver.query(children, arrayOf(DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME), null, null, null)?.use { c ->
            while (c.moveToNext()) { ids.add(c.getString(0)); names.add(c.getString(1)) }
        } ?: throw java.io.IOException("Receive folder unavailable")
        val candidate = (0..999).asSequence().map { FileTransferPolicy.uniqueName(name, it) }.firstOrNull { it !in names }
            ?: throw java.io.IOException("No unused receive filename")
        val created = DocumentsContract.createDocument(context.contentResolver, parent, "application/octet-stream", candidate)
            ?: throw java.io.IOException("Cannot create receive file")
        // A provider must create a new document. Never write/delete a pre-existing child.
        require(DocumentsContract.getDocumentId(created) !in ids)
        return created
    }
}
