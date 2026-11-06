namespace ReyDesk.Windows.Rendezvous;

/// <summary>What the rendezvous client needs from the device identity store.
/// DeviceIdentityService satisfies PublicKey/Sign already; its owner adds DeskId/SetDeskId
/// (spec 4.2: nullable persisted field, absent = none) and declares this interface.</summary>
internal interface IRendezvousIdentity
{
    /// <summary>P-256 SubjectPublicKeyInfo DER.</summary>
    byte[] PublicKey { get; }

    /// <summary>ECDSA P-256 / SHA-256 signature, DER.</summary>
    byte[] Sign(byte[] data);

    /// <summary>Canonical (12 chars, no separators) server-allocated Desk ID, or null.</summary>
    string? DeskId { get; }

    void SetDeskId(string? deskId);
}
