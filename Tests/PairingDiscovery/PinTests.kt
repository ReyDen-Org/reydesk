import ir.reyden.reydesk.pairing.LanHostMetadata
import ir.reyden.reydesk.pairing.RemoteCode
import java.util.Base64

fun main() {
    var passed = 0
    fun check(value: Boolean, label: String) { require(value) { label }; passed++; println("PASS: $label") }
    check(RemoteCode.normalize("123-456") == "123456", "displayed PIN hyphen is removed")
    check(RemoteCode.normalize("۱۲۳ ۴۵۶") == "123456", "Persian keyboard digits map to wire ASCII")
    check(RemoteCode.normalize("١٢٣٤٥٦") == "123456", "Arabic keyboard digits map to wire ASCII")
    check(RemoteCode.normalize("12345") == null, "incomplete PIN cannot connect")
    check(RemoteCode.normalize("1234567") == null, "extra PIN digits cannot connect")
    check(RemoteCode.normalize("123a456") == null, "letters are rejected instead of silently removed")
    val properties = mapOf("v" to "1", "id" to "desktop", "name" to "My PC", "key" to Base64.getEncoder().encodeToString(ByteArray(91) { 3 }))
    val metadata = LanHostMetadata.parse(properties, "192.168.1.42", 47821)
    check(metadata?.name == "My PC" && metadata.address == "192.168.1.42", "valid discovery supplies selected host and endpoint")
    check(metadata?.publicKey?.size == 91, "host public key is decoded without PIN publication")
    check(LanHostMetadata.parse(properties - "key", "192.168.1.42", 47821) == null, "missing host key is rejected")
    check(LanHostMetadata.parse(properties + ("v" to "2"), "192.168.1.42", 47821) == null, "unsupported discovery protocol is rejected")
    check(LanHostMetadata.parse(properties, "192.168.1.42", 0) == null, "invalid resolved port is rejected")
    check(LanHostMetadata.parse(properties + ("key" to "%%%"), "192.168.1.42", 47821) == null, "malformed public key metadata is ignored")
    println("$passed PIN/discovery checks passed")
}
