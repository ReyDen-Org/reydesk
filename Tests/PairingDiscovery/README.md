# Local PIN discovery regression checks

Run `dotnet run --project Tests/PairingDiscovery/PairingDiscovery.csproj --no-restore` after restoring from the existing NuGet cache. This uses the already-installed Makaretu.Dns.Multicast version0.27.0, not a new app dependency. Seven checks inspect actual DNS records: dedicated service type, host/key/endpoint, no PIN publication, and Unicode TXT limits.

Run `Tests/PairingDiscovery/run-pin.ps1` for twelve Kotlin checks using the existing Android Studio/Gradle compiler. They cover ASCII/Persian/Arabic PIN input, invalid PINs and malformed discovery metadata. The generated test JAR lives in TEMP.

Before implementation both new suites failed because their production discovery/PIN helpers did not exist. After implementation all19checks pass. These tests do not prove multicast delivery across the real LAN or physical PIN pairing. Build the apps and verify nearby-host discovery, wrong PIN rejection, successful current PIN, Disconnect and QR regression on the real phone.

Public metadata is discovered via `_reydesk._tcp.local`; PINs are never advertised. A6second browse collects at most16service instances, resolves serially and releases discovery on timeout/cancellation. Multiple computers require explicit selection; one discovered computer is selected automatically. Current server token/expiry/signature validation remains unchanged. This does not add a remote rendezvous service or authenticate mDNS against active LAN attackers; existing pairing security limitations remain.

API references checked: [Makaretu advertising](https://richardschneider.github.io/net-mdns/articles/advertising.html), [Android NsdManager](https://developer.android.com/reference/android/net/nsd/NsdManager).
