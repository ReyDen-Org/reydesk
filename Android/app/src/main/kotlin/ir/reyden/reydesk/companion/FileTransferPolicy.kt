package ir.reyden.reydesk.companion

object FileTransferPolicy {
    const val maximumBytes = 100L * 1024 * 1024
    const val chunkBytes = 16384
    fun safeName(value: String) = value.isNotEmpty() && value.length <= 160 && value != "." && value != ".." && value.none { it.isISOControl() || it in "/\\:\u0000" }
    fun uniqueName(name: String, index: Int): String {
        require(safeName(name) && index in 0..999)
        if (index == 0) return name
        val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
        return "${name.substring(0, dot)} ($index)${name.substring(dot)}"
    }
    fun validOffer(id: String, name: String, size: Long, hash: String) = id.isNotEmpty() && id.length <= 64 && safeName(name) && size in 0..maximumBytes && hash.length == 64 && hash.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }
    fun validChunk(offset: Long, expected: Long, size: Int, total: Long, final: Boolean) = offset == expected && size in 0..chunkBytes && expected >= 0 && expected <= total && size <= total - expected && final == (expected + size == total) && (final || size > 0)
}
