using Google.Protobuf;
using Makaretu.Dns;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Pairing;

var count = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); count++; Console.WriteLine("PASS: " + label); }
var offer = new PairingOffer {
    Host = new DeviceIdentity { DeviceId = "test-host", DisplayName = "Desktop" },
    HostPublicKey = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
    LocalIpHint = "192.168.1.42", LocalPortHint = 47821,
    PairingToken = "123456", ExpiresAtUnixMs = 1234,
};
var profile = PairingDiscovery.CreateProfile(offer);
var txt = profile.Resources.OfType<TXTRecord>().Single().Strings;
Check(profile.ServiceName.ToString() == "_reydesk._tcp", "discovery has a dedicated local service type");
Check(txt.Any(value => value == "id=test-host"), "host identity is discoverable without QR");
Check(txt.Any(value => value == "key=AQID"), "existing host verification key is carried intact");
Check(!txt.Any(value => value.Contains("123456") || value.StartsWith("pin=") || value.StartsWith("token=")), "advertisement never contains manual PIN or token");
Check(profile.Resources.OfType<ARecord>().Single().Address.ToString() == "192.168.1.42", "only selected LAN address is advertised");
Check(profile.Resources.OfType<SRVRecord>().Single().Port == 47821, "advertised port reaches existing pairing listener");
offer.Host.DisplayName = new string('ژ', 300);
Check(PairingDiscovery.CreateProfile(offer).Resources.OfType<TXTRecord>().Single().Strings.All(value => System.Text.Encoding.UTF8.GetByteCount(value) < 255), "long Unicode display names fit DNS TXT record limits");
Console.WriteLine($"{count} discovery checks passed");
