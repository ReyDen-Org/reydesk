using ReyDesk.Windows.Pairing;

// All mutations are isolated from the real user's saved trust.
var directory = Path.Combine(Path.GetTempPath(), "ReyDesk-TrustTest-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "trusted_devices.json");
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; }
try
{
    var store = TrustedDeviceStore.LoadOrCreate(path);
    store.AddOrUpdate("phone-1", "SM-A325F", new byte[] { 1, 2 });
    store.AddOrUpdate("phone-2", "Pixel 8 Pro", new byte[] { 3, 4 });
    var before = store.GetAll();
    int changes = 0;
    store.Changed += () => changes++;
    store.Remove("phone-1");
    Check(!store.IsTrusted("phone-1"), "Forgotten identity no longer trusted");
    Check(store.IsTrusted("phone-2"), "Other identity retains trust");
    Check(before.Count == 2, "Earlier UI snapshot remains stable");
    Check(changes == 1, "Removal refreshes UI once");
    var reopened = TrustedDeviceStore.LoadOrCreate(path);
    Check(!reopened.IsTrusted("phone-1") && reopened.IsTrusted("phone-2"), "Revocation persists across restart");
    store.Remove("unknown");
    Check(changes == 1, "Unknown identity is a no-op");
    Parallel.For(0, 32, i => store.AddOrUpdate("parallel-" + i, "Phone", new byte[] { 5 }));
    Check(store.GetAll().Count == 33, "Concurrent pairings retain records");
    Parallel.For(0, 32, i => { store.GetAll(); store.Remove("parallel-" + i); });
    Check(TrustedDeviceStore.LoadOrCreate(path).GetAll().Single().DeviceId == "phone-2", "Concurrent forgets persist safely");
    var original = store.GetAll().Single();
    store.Rename("phone-2", "  Office phone  ");
    var renamed = TrustedDeviceStore.LoadOrCreate(path).GetAll().Single();
    Check(renamed.Alias == "Office phone", "Local name trims and persists");
    Check(renamed.DeviceId == original.DeviceId && renamed.PublicKeyBase64 == original.PublicKeyBase64 && renamed.DisplayName == original.DisplayName, "Rename preserves identity key and reported model");
    store.AddOrUpdate("phone-2", "Pixel 8 Pro", new byte[] { 3, 4 });
    Check(store.GetAll().Single().Alias == "Office phone", "Reconnect retains local label");
    store.Rename("phone-2", " ");
    Check(TrustedDeviceStore.LoadOrCreate(path).GetAll().Single().Alias is null, "Empty alias restores model name");
    try { store.Rename("phone-2", new string('x', 61)); throw new Exception("Long alias accepted"); }
    catch (ArgumentException) { Check(true, "Long alias rejected"); }
    File.WriteAllText(path, "[{\"DeviceId\":\"legacy\",\"DisplayName\":\"SM-A325F\",\"PublicKeyBase64\":\"AQI=\",\"PairedAtUnixMs\":1,\"LastSeenUnixMs\":2}]");
    Check(TrustedDeviceStore.LoadOrCreate(path).GetAll().Single().Alias is null, "Legacy record without alias loads");
    // ---- v2: Desk ID, per-device remote flag, enrollment facts ----
    var legacy = TrustedDeviceStore.LoadOrCreate(path).GetAll().Single();
    Check(!legacy.RemoteAllowed && legacy.DeskId is null && legacy.PairedMethod == 0, "Legacy record loads with remote off, no Desk ID, unknown method");
    var legacyStore = TrustedDeviceStore.LoadOrCreate(path);
    Check(!legacyStore.IsRemoteAllowed("legacy", new byte[] { 1, 2 }) && !legacyStore.IsUnattendedAllowed("legacy", new byte[] { 1, 2 }), "Existing trust defaults remote and unattended off");
    legacyStore.AddOrUpdate("remote-1", "Pixel 8", new byte[] { 9, 9 }, new TrustEnrollment("7KQ4MZ2PX9DF", true, 4));
    var enrolled = TrustedDeviceStore.LoadOrCreate(path).GetAll().Single(d => d.DeviceId == "remote-1");
    Check(enrolled.RemoteAllowed && enrolled.DeskId == "7KQ4MZ2PX9DF" && enrolled.PairedMethod == 4, "Remote enrollment persists Desk ID, flag and method");
    Check(!enrolled.UnattendedAllowed, "Remote enrollment never enables unattended");
    Check(legacyStore.IsRemoteAllowed("remote-1", new byte[] { 9, 9 }) && !legacyStore.IsRemoteAllowed("remote-1", new byte[] { 9, 8 }), "Remote flag needs the exact key");
    Check(legacyStore.HasRemoteAllowedFingerprint(System.Security.Cryptography.SHA256.HashData(new byte[] { 9, 9 })), "Pipe pre-check finds a remote-allowed key fingerprint");
    Check(!legacyStore.HasRemoteAllowedFingerprint(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2 })), "Pipe pre-check ignores a trusted key without the remote flag");
    legacyStore.SetUnattendedAllowed("remote-1", new byte[] { 9, 9 }, true);
    legacyStore.AddOrUpdate("remote-1", "Pixel 8", new byte[] { 9, 9 }, new TrustEnrollment(null, null, 2));
    var kept = legacyStore.GetAll().Single(d => d.DeviceId == "remote-1");
    Check(kept.UnattendedAllowed && kept.RemoteAllowed && kept.DeskId == "7KQ4MZ2PX9DF" && kept.PairedMethod == 4, "Reconnect refresh keeps flags, Desk ID and the original method");
    legacyStore.SetRemoteAllowed("remote-1", new byte[] { 9, 9 }, false);
    Check(TrustedDeviceStore.LoadOrCreate(path).GetAll().Single(d => d.DeviceId == "remote-1").RemoteAllowed == false, "Remote flag toggles off and persists");
    try { legacyStore.SetRemoteAllowed("remote-1", new byte[] { 7 }, true); throw new Exception("Substituted key enabled remote"); }
    catch (InvalidOperationException) { Check(true, "Remote opt-in requires the exact stored key"); }
    try { legacyStore.AddOrUpdate("remote-1", "Pixel 8", new byte[] { 7 }, new TrustEnrollment(null, true, 4)); throw new Exception("Key overwritten"); }
    catch (InvalidOperationException) { Check(true, "Enrollment cannot replace a stored key or turn remote on for it"); }
    Check(!legacyStore.IsRemoteAllowed("remote-1", new byte[] { 9, 9 }), "Rejected enrollment left the remote flag unchanged");
    legacyStore.AddOrUpdate("remote-1", "Pixel 8", new byte[] { 9, 9 }, new TrustEnrollment(null, true, 4));
    legacyStore.Remove("remote-1");
    legacyStore.AddOrUpdate("remote-1", "Pixel 8", new byte[] { 9, 9 });
    var fresh = legacyStore.GetAll().Single(d => d.DeviceId == "remote-1");
    Check(!fresh.RemoteAllowed && !fresh.UnattendedAllowed && fresh.DeskId is null, "Forget and re-pair resets remote, unattended and Desk ID");
    Console.WriteLine($"PASS {passed} trusted-device persistence/revocation checks");
}
finally
{
    // Explicitly created test directory, never a production store.
    File.Delete(path);
    Directory.Delete(directory);
}
