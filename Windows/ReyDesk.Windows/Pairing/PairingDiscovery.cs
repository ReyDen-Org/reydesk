using System;
using System.Net;
using System.Text;
using Makaretu.Dns;
using ReyDesk.Protocol.V1;
using ReyDesk.Windows.Identity;

namespace ReyDesk.Windows.Pairing;

/// <summary>Publishes only public bootstrap details (v, id, name, key, did, kid).
/// No code, token or secret is ever put in DNS records. All of it is unauthenticated: the phone
/// pins <c>key</c> only for the SAS flow, and never trusts it before the codes match and the PC approves.</summary>
internal sealed class PairingDiscovery : IDisposable
{
    public const string ServiceType = "_reydesk._tcp";
    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _discovery;
    private ServiceProfile? _profile;
    private string? _address;
    private string? _deskId;

    public PairingDiscovery()
    {
        _discovery = new ServiceDiscovery(_mdns);
        try { _mdns.Start(); }
        catch { _discovery.Dispose(); _mdns.Dispose(); throw; }
    }

    public void Publish(PairingOffer offer)
    {
        if (_address == offer.LocalIpHint && _deskId == offer.Host.DeskId) return;
        if (_profile is not null) _discovery.Unadvertise(_profile);
        _profile = CreateProfile(offer);
        _discovery.Advertise(_profile);
        _address = offer.LocalIpHint;
        _deskId = offer.Host.DeskId;
        Console.WriteLine("[PairingDiscovery] LAN host discovery available");
    }

    internal static ServiceProfile CreateProfile(PairingOffer offer)
    {
        var profile = new ServiceProfile("ReyDesk-" + offer.Host.DeviceId,
            ServiceType, checked((ushort)offer.LocalPortHint), new[] { IPAddress.Parse(offer.LocalIpHint) });
        profile.AddProperty("v", "1");
        profile.AddProperty("id", offer.Host.DeviceId);
        var name = offer.Host.DisplayName;
        while (Encoding.UTF8.GetByteCount(name) > 160) name = name[..^1];
        profile.AddProperty("name", name);
        var key = offer.HostPublicKey.ToByteArray();
        profile.AddProperty("key", Convert.ToBase64String(key));
        // Additive v2 fields: ReyDesk ID (routing hint, only when registered) and Key ID (derived from key, display hint).
        if (DeskId.Parse(offer.Host.DeskId) is { Ok: true } desk) profile.AddProperty("did", desk.Canonical!);
        profile.AddProperty("kid", KeyId.FromSpki(key));
        return profile;
    }

    public void Dispose()
    {
        try { if (_profile is not null) _discovery.Unadvertise(_profile); }
        finally { _discovery.Dispose(); _mdns.Dispose(); }
    }
}
