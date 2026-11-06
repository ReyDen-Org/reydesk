# ReyDesk usage

Windows and Android must both be updated: old plaintext pairing clients are intentionally refused.

## Connect

Start the Windows app. For first enrollment scan its QR from the Android Connection tab and approve the new phone on the PC. QR transfers the exact host public key before TLS pairing; a first-time PIN alone is refused. Saved host PIN pairing remains available. A used/expired challenge requires regeneration. LAN discovery requires a common reachable LAN and multicast support; QR remains the fallback.

For PIN-free reconnect, deliberately enable unattended access for that exact trusted phone in the Windows device card, then use the saved-key-matching nearby host's Reconnect action on Android and confirm it in the foreground. This is a per-user app after Windows sign-in, not a pre-login service. Turning the opt-in off or removing trust terminates the corresponding access. No biometric or automatic background reconnect is claimed.

## Phone Hub / مرکز گوشی

Inside the ReyDesk Android app: Settings → Allow notification access. This opens the Android permission page for ReyDesk directly on supported devices. On devices showing the notification access list, select ReyDesk. The system service is labelled ReyDesk, not Phone Hub.
To configure sharing inside ReyDesk: Settings → Phone Hub → Manage notification sharing.
Persian: تنظیمات ← مرکز گوشی ← مدیریت اشتراک اعلان‌ها.
Manually enable Allow notification access on the Android page, then return to ReyDesk. Enable sharing and select the apps you want; the initial app allowlist is empty. Previews start off. Windows Phone Hub shows only current eligible notifications from the connected phone. The phone must remain connected; this is not full SMS history.

With notification previews enabled on Android and on the PC, ordinary eligible notifications can remain readable while the phone is locked. Secret, system-withheld, OTP and banking-sensitive content remains redacted. Reply and dismiss actions require an unlocked phone and an unredacted notification. Reply appears only when the original app supplies a legitimate reply action; Android can reject expired actions. The app does not become the default SMS handler and does not read SMS databases. Pause, filters, permission removal, trust removal and disconnect clear/restrict current data. Notification content stays in RAM, with no persistent message history.

On Windows, scroll down in Phone Hub to enable **Generic Windows notifications** if wanted. This separate option starts off every time ReyDesk launches. Alerts are silent and contain only a fixed notice to open ReyDesk; sender, app name and message content never enter the shell notification. Clicking the notice opens Phone Hub. Focus silences these alerts too. There is at most one alert per 30 seconds, reconnect snapshots do not alert, and Windows can suppress the popup. This uses standard Windows App SDK notifications with fixed generic text, a one-minute expiry and cleanup on disconnect. The Test Windows alert button sends only a local synthetic notice and does not enable automatic phone alerts; Focus also disables the test. Windows controls banner display.

## Desktop, Control and files

Choose Desktop or Control before connecting, or switch during a session. Control stops continuous desktop capture. Fixed media/volume/lock controls report transport/OS acceptance, not a guarantee that another application responded. Clipboard transfer is manual text only and requires PC approval; no background Android clipboard monitoring.

In File transfers on Windows or Files on Android, use **Choose receive folder** once to remember a destination on that receiving device. Future files from an authorized connected session save there automatically after SHA-256 verification. Android asks for a system folder grant. The selection survives app restarts; **Forget** returns to choosing a destination for each file. Existing names receive a numeric suffix. An unavailable or revoked folder displays recovery guidance. With no remembered folder, native destination pickers remain available. One transfer per session, maximum 100 MiB. Cancel/revoke/disconnect abort the operation. Resume, transfer queues and cross-network reachability are not claimed.

## Approved applications and Developer Mode

Register executable paths, fixed argument lists, application profiles or fixed shortcuts locally on the PC. Android gets only IDs/labels/profile names. Each remote invocation requires PC confirmation and a still-authorized unlocked session. There is no raw remote command shell. Shortcuts target the captured foreground window and fail if its identity/focus cannot be restored.

Developer Mode is optional and initially disabled. Its tasks are separately registered executable/working-directory/fixed-argument definitions; start/stop require PC confirmation. Only processes started by this feature are tracked. No discovery of arbitrary existing dev servers or official Claude integration is claimed. Output sharing starts off per task; if enabled, bounded output removes common credential-bearing lines and control sequences. Arbitrary task output may still be private: register suitable tasks deliberately. Closing ReyDesk does not silently kill local tasks; runtime tracking is not restored on app restart.

## Networking and limits

LAN is direct. TLS pins the saved/QR host key; WebRTC carries encrypted media and data. No Reyden server stores the stream/files. Away from your local network, each PC has a unique ReyDesk ID and the two devices find each other through the rendezvous server `rdv.reyden.ir`, which only introduces them and makes no trust decisions; the connection stays peer-to-peer. Relayed traffic is an optional setting and is off by default. Design details are in identity-and-pairing.md and protocol.md. Diagnostics distinguishes measured local checks from unknown NAT/firewall/internet conditions; a user-managed reachable VPN can be assessed separately.

System audio streaming was not implemented in the pre-existing source and remains unavailable. Cellular-call audio, full SMS composition/history, Android screen mirroring/control, pre-login Windows service and Wake-on-LAN are unavailable. Paid SMS recovery has a service interface with an explicitly unconfigured implementation; no account/backend/provider/billing or SMS delivery is fabricated.
