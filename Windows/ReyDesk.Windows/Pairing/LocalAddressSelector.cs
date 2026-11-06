using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ReyDesk.Windows.Pairing;

internal sealed record LocalAddressCandidate(string Name, string Description,
    NetworkInterfaceType Type, bool IsUp, bool HasIpv4Gateway, IPAddress Address);

internal static class LocalAddressSelector
{
    private static readonly string[] VirtualAdapterNames =
    [
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker",
        "vpn", "tunnel", "wintun", "tap-", "wireguard", "tailscale", "zerotier", "nekoray",
    ];

    public static IPAddress? Select(IEnumerable<LocalAddressCandidate> candidates) => candidates
        .Where(candidate => candidate.IsUp && IsLan(candidate.Type) && IsUsableIPv4(candidate.Address) &&
            !candidate.Name.StartsWith("tun", StringComparison.OrdinalIgnoreCase) &&
            !candidate.Name.StartsWith("tap", StringComparison.OrdinalIgnoreCase) &&
            !VirtualAdapterNames.Any(name => candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                candidate.Description.Contains(name, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(candidate => candidate.HasIpv4Gateway)
        .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
        .FirstOrDefault()?.Address;

    private static bool IsLan(NetworkInterfaceType type) => type is NetworkInterfaceType.Ethernet or
        NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT;

    internal static bool IsUsableIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
}
