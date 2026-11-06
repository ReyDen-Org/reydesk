using System.Net;
using System.Net.NetworkInformation;
using ReyDesk.Windows.Pairing;

var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
LocalAddressCandidate Adapter(string name, string ip, bool gateway = true, string description = "Realtek PCIe GbE Family Controller", NetworkInterfaceType type = NetworkInterfaceType.Ethernet, bool up = true)
    => new(name, description, type, up, gateway, IPAddress.Parse(ip));
var lan = Adapter("Ethernet 3", "192.168.100.8");
var hyperV = Adapter("vEthernet (Default Switch)", "172.19.0.1", false, "Hyper-V Virtual Ethernet Adapter");
Check(LocalAddressSelector.Select(new[] { hyperV, lan })?.Equals(lan.Address) == true, "virtual-first enumeration advertises physical LAN address");
Check(LocalAddressSelector.Select(new[] { lan, hyperV })?.Equals(lan.Address) == true, "physical-first ordering produces same address");
var noGateway = Adapter("Ethernet 1", "10.10.1.2", false);
Check(LocalAddressSelector.Select(new[] { noGateway, lan })?.Equals(lan.Address) == true, "gateway LAN outranks isolated Ethernet");
Check(LocalAddressSelector.Select(new[] { noGateway })?.Equals(noGateway.Address) == true, "direct LAN without gateway remains usable");
Check(LocalAddressSelector.Select(new[] { Adapter("Wi-Fi", "192.168.5.2", true, "Intel Wi-Fi", NetworkInterfaceType.Wireless80211) })?.ToString() == "192.168.5.2", "WiFi gateway accepted");
foreach (var ip in new[] { "127.0.0.1", "169.254.5.1", "0.0.0.0", "224.0.0.1", "255.255.255.255", "::1", "fe80::1" })
    Check(LocalAddressSelector.Select(new[] { Adapter("Ethernet", ip) }) is null, $"unusable address excluded: {ip}");
foreach (var description in new[] { "Hyper-V Virtual Ethernet Adapter", "WireGuard Tunnel", "TAP-Windows Adapter V9", "Tailscale", "VMware Virtual Ethernet Adapter", "WSL", "Nekoray TUN", "Docker", "VPN Adapter" })
    Check(LocalAddressSelector.Select(new[] { Adapter("Ethernet", "172.20.1.1", true, description) }) is null, $"virtual/VPN adapter excluded: {description}");
Check(LocalAddressSelector.Select(new[] { Adapter("Ethernet", "192.168.1.2", up: false) }) is null, "down interface excluded");
Check(LocalAddressSelector.Select(new[] { Adapter("Tunnel", "10.0.0.2", type: NetworkInterfaceType.Tunnel) }) is null, "tunnel interface type excluded");
Check(LocalAddressSelector.Select(new[] { Adapter("tun0", "10.0.0.2") }) is null, "TUN name excluded even when reported as Ethernet");
Check(LocalAddressSelector.Select(new[] { Adapter("tap0", "10.0.0.2") }) is null, "TAP name excluded even when reported as Ethernet");
Check(LocalAddressSelector.Select(Array.Empty<LocalAddressCandidate>()) is null, "no usable LAN returns no address");
var otherLan = Adapter("Ethernet 2", "10.20.1.2");
Check(Equals(LocalAddressSelector.Select(new[] { otherLan, lan }), LocalAddressSelector.Select(new[] { lan, otherLan })), "equal LAN candidates select deterministically regardless enumeration");
if (args.Contains("--live"))
{
    var candidates = NetworkInterface.GetAllNetworkInterfaces().SelectMany(network =>
    {
        var properties = network.GetIPProperties();
        var gateway = properties.GatewayAddresses.Any(item => LocalAddressSelector.IsUsableIPv4(item.Address));
        return properties.UnicastAddresses.Select(item => new LocalAddressCandidate(network.Name, network.Description, network.NetworkInterfaceType, network.OperationalStatus == OperationalStatus.Up, gateway, item.Address));
    }).ToArray();
    Console.WriteLine($"LIVE selected IPv4: {LocalAddressSelector.Select(candidates)?.ToString() ?? "none"}");
}
Console.WriteLine($"{passed} pairing address regression checks passed");
