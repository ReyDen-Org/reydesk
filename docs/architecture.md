# ReyDesk Architecture

Status: current architecture of ReyDesk 1.0.

## System overview

```
Windows PC  <-- ReyDesk.Windows -->  Local Wi-Fi / LAN  <-- ReyDesk (Android) --> Android phone
                       |
                       |  (remote mode only)
                       v
              Backend (ASP.NET Core): auth, device registry, WebSocket signaling, TURN credentials
                       |
                       v
                 Internet  <-->  ReyDesk (Android), elsewhere
```

Local mode (spec §4/§56) never touches the Backend — discovery, pairing, video, audio, input,
clipboard, and file transfer are all direct WebRTC peer-to-peer over the LAN. The Backend exists
only to make remote-mode connections possible (account, signaling, TURN); it does not sit in the
media or control-message path even in remote mode — see "Control plane vs. data plane" below.

## Client: Windows (`Windows/ReyDesk.Windows`)

- **UI shell**: WinUI 3 (.NET 8, Windows App SDK), unpackaged (`dotnet build`/`dotnet run`, no
  MSIX/Store dependency required for local dev). Chosen over Electron/other cross-platform shells
  because Windows.Graphics.Capture, Media Foundation hardware encode, and system tray integration
  are all first-class here without an extra native-interop layer for the UI itself.
- **Screen capture**: Windows.Graphics.Capture (WinRT) → DXGI/Direct3D11 texture, GPU-resident,
  copied to a CPU staging texture for the encoder (see next bullet). `Media/ScreenCapture.cs` +
  `Media/Direct3D11Interop.cs`. **Implemented.**.
- **Hardware encode**: Media Foundation H.264 encoder MFT, driven via a small hand-written
  `[ComImport]` interop layer (`MediaFoundation/`), **not** `Vortice.MediaFoundation` — that
  package turned out not to expose `IMFTransform`/`IMFSample` at all.
  `Vortice.Direct3D11`/`Vortice.DXGI` are still used, for the D3D11 device/texture side only.
  Frames are handed to the encoder as CPU memory (BGRA→NV12 conversion in
  `Media/Nv12Conversion.cs`), not GPU-resident DXGI buffers — a deliberate simplification, not an oversight. **Implemented, build-verified — NOT
  hardware-tested.**
- **Transport**: `SIPSorcery` (pure C#/.NET WebRTC — ICE, DTLS-SRTP, SCTP data channels), given
  pre-encoded H.264 samples via `RTCPeerConnection.SendVideo` — confirmed against SIPSorcery's own
  source. **Implemented.** `Media/WebRtcSession.cs`.
- **Remote input injection**: Win32 `SendInput` (`Input/InputInjector.cs`) — absolute mouse via the
  normalized 0–65535 coordinate space, `KEYEVENTF_UNICODE` for typed text (keyboard-layout
  independent). **Implemented.**
- **Pairing/signaling transport**: plain TCP, length-delimited protobuf (`Envelope.WriteDelimitedTo`
  /`ParseDelimitedFrom`), on the port advertised in the `PairingOffer` — see docs/protocol.md
  "Pairing & signaling transport". **Pairing exchange implemented and verified** (real handshake
  against a synthetic client) via `Pairing/PairingListener.cs` +
  `Pairing/TrustedDeviceStore.cs`; the same connection now also carries WebRTC SDP/ICE (see above).
- **Tray**: `Windows/ReyDesk.Windows/Platform/TrayIcon.cs` — Win32 `Shell_NotifyIcon` via
  P/Invoke through a dedicated hidden message-only window (WinUI3 has no built-in tray API).
  Closing the main window hides it instead of exiting; only the tray "Quit" item actually
  terminates the process (spec §20). **Implemented and build-verified.**
- **Theming**: `Windows/ReyDesk.Windows/Theme/Tokens.Dark.xaml` — WinUI resource dictionary (dark theme), described in `docs/design-system.md`. **Implemented and build-verified.**

## Client: Android (`Android/`)

- Kotlin + Jetpack Compose, Material3.
- **WebRTC**: official `org.webrtc` stack via the actively-maintained `io.github.webrtc-sdk`
  Maven mirror (Google stopped publishing prebuilt AARs directly). Hardware decode is automatic
  through libwebrtc's Android `MediaCodec` integration — no separate encode/decode plumbing needed
  on this side, unlike Windows. `webrtc/WebRtcSession.kt` renders the remote track via a
  `SurfaceViewRenderer` on the new Session screen (`ui/session/SessionScreen.kt`), and sends
  touch-derived mouse input + typed text back over the input data channels.
  **Implemented.**
- **Local discovery**: Android's built-in `NsdManager` (native mDNS/DNS-SD). Implemented in `pairing/LanHostDiscovery.kt`.
- **Device identity**: `identity/DeviceIdentityService.kt` — EC P-256 keypair held in the Android
  Keystore (never exported, unlike Windows's DPAPI-protected blob), device ID persisted via
  DataStore. **Implemented.**.
- **Pairing exchange**: `pairing/PairingClient.kt` + `pairing/TrustedDeviceStore.kt` — connects to
  the Windows `PairingListener` over the TCP transport described above and persists the result.
  **Implemented and tested on a real phone**: a real Android phone (Galaxy A32) scanned a QR
  code from a running Windows instance, completed the signed handshake, and both sides' Devices
  lists show each other.
- **UI**: navigation shell (Connection/Devices/Settings bottom nav, plus a non-bottom-nav
  full-screen Session route pushed once pairing succeeds), Material3 theme from the same design
  tokens as Windows, 2-language strings (English/Persian).
  **Implemented.**.

## Protocol (`Protocol/`)

See [`docs/protocol.md`](protocol.md) for the full message catalog. Summary: one versioned
protobuf schema (`reydesk.v1`) as the single maintained copy, compiled to C# and Kotlin for both
clients (Android syncs it into its build directory at build time rather than maintaining a second
copy — see `Android/README.md`). Video/audio are native WebRTC media tracks, not protobuf. **Schema
compiles cleanly to C#/Java/Kotlin and is now consumed by real client code**: pairing/signaling
(`pairing.proto`, `signaling.proto`) and remote input (`input.proto`) are both wired end-to-end.
`capabilities.proto`/`diagnostics.proto`'s negotiation and stats messages are defined but not yet
used to actually negotiate anything (screen-share quality is still static) or drive a diagnostics overlay.

## Control plane vs. data plane (spec §29)

| | Local mode | Remote mode |
|---|---|---|
| Discovery | mDNS/NSD, LAN broadcast | N/A — devices connect via account, not discovery |
| Pairing / trust | QR or PIN, direct device-to-device (see `docs/protocol.md` "Pairing & trust model") | Same trust model, offer/answer relayed through Backend's signaling channel instead of exchanged in person |
| Signaling (SDP/ICE exchange) | Not needed — devices are already on the same network, discovery doubles as the rendezvous | Backend WebSocket endpoint |
| Media (video/audio) | Direct WebRTC P2P over LAN | WebRTC P2P where a direct/STUN path exists; TURN relay (credentials issued by Backend) only when NAT prevents direct connectivity |
| Input/clipboard/file metadata | Direct WebRTC data channel | Same — never routed through Backend |
| Account/auth | N/A | Backend (email/username/password, JWT + refresh tokens) |

The Backend is deliberately kept out of the media and control-message path in both modes — it
issues credentials and helps two devices find each other, then gets out of the way. It never
receives frames, audio, input events, clipboard contents, or file bytes (spec §6/§30).

## Backend (`Backend/rendezvous`)

A small Go rendezvous server used only for remote mode: account-less device registration,
signaling relay and TURN credentials. It never sees media, input, clipboard or file bytes.
See `Backend/rendezvous/README.md`.

## Repository layout

```
ReyDesk/
├── docs/            This file, protocol.md, design-system.md, BUILD.md
├── Protocol/         reydesk/v1/*.proto - versioned wire schema, consumed by both clients
├── Windows/          ReyDesk.Windows/ - WinUI3 .NET 8 app
├── Android/          Kotlin/Compose app
├── Backend/          rendezvous/ - Go rendezvous server
├── Installer/        Inno Setup script
├── Tests/            standalone test projects
├── assets/brand/     Brand notes and mark
```
