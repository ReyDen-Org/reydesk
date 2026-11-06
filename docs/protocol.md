# ReyDesk Protocol

Status: **v1.** The schema is implemented by the Windows and Android apps and the rendezvous server. It may still be refined; the `.proto` files are authoritative.

Schema source: [`Protocol/reydesk/v1/*.proto`](../Protocol/reydesk/v1/). This document explains
*why* it's shaped this way; the `.proto` files are the source of truth for the exact fields.

## Transport model

ReyDesk has three logically separate channels between two paired devices, all running over one
WebRTC `PeerConnection` per session:

| Channel | Carries | Primitive | Reliability |
|---|---|---|---|
| Pairing & signaling | `Envelope` (`PairingRequest`/`Response`, `SessionOffer`/`Answer`, `IceCandidate`) | plain TCP, length-delimited protobuf (`WriteDelimitedTo`/`ParseDelimitedFrom`) on the port advertised in the `PairingOffer` | reliable, ordered — established before any `RTCPeerConnection` exists, so it can't itself ride a data channel; stays open post-pairing and carries SDP/ICE for session setup |
| Control | `Envelope` (capabilities, clipboard, file-transfer metadata, diagnostics) | ordered, reliable `RTCDataChannel` | reliable, ordered |
| Input | `Envelope` (`MouseMove`, `MouseScroll`, `GamepadAxisEvent` — high frequency, stale values are worthless) | unordered, unreliable `RTCDataChannel` (`maxRetransmits: 0`) | best-effort |
| Input (discrete) | `Envelope` (`MouseButtonEvent`, `KeyEvent`, `GamepadButtonEvent`, `TextInput` — a dropped click/keystroke is a real bug) | ordered, reliable `RTCDataChannel` | reliable, ordered |
| Media | video (H.264/HEVC) + audio (Opus) | native `RTCRtpSender`/`RTCRtpReceiver` tracks | codec/RTP-level, not protobuf |
| File bytes | raw file chunks, correlated to a `FileTransferOffer.transfer_id` | dedicated ordered, reliable `RTCDataChannel` | reliable, ordered — kept separate from Control so a large file transfer can't head-of-line-block clipboard/input messages |

Everything except media and raw file bytes is a `reydesk.v1.Envelope` — one wrapper message with
a `oneof payload`, `protocol_version`, and `session_id`. This keeps parsing on both ends to one
`Envelope.parseFrom(bytes)` call regardless of which data channel it arrived on.

## Why protobuf, not JSON

- Binary, compact — matters on the input channel, which can be sent at 60+ Hz per pointer move.
- Schema-first: one `.proto` source generates C# (Windows, Backend), Kotlin (Android), and later
  Swift/C++ (iOS/Linux) without hand-written parallel DTOs drifting out of sync.
- Forward/backward compatible by construction (unknown fields are preserved, not rejected) —
  needed since Windows, Android, and the backend will not all update in lockstep.

## Platform neutrality

`Platform` (in `common.proto`) already enumerates iOS/Linux/macOS even though only Windows and
Android ship in v1 — per the master spec's constraint that the protocol must not encode
Windows/Android-specific assumptions. Concretely this means:

- Key events use USB HID usage codes, not `VK_*` (Windows) or `KeyEvent` (Android) constants.
- Pointer coordinates are normalized `[0,1]` against the negotiated stream resolution, never raw
  pixels, so no side needs another's screen-space conventions.
- `DeviceCapabilities` is an open capability list (codecs, max resolution, feature flags), not a
  fixed platform check — a future client just advertises what it supports.

## Pairing & trust model (summary)

Being on the same LAN is **not** treated as trust (spec §8). Each device holds a long-term EC P-256
keypair generated on first run — Windows via DPAPI-protected exported PKCS8
(`Identity/DeviceIdentityService.cs`), Android via a non-exportable Android Keystore key
(`identity/DeviceIdentityService.kt`); the master spec named Ed25519, but neither platform's
built-in crypto stack has confirmed, unproblematic support for it, so P-256/ECDSA was substituted. Pairing (QR or PIN) exchanges public keys out-of-band — the QR code or
the human-read PIN is the authenticated channel that defeats a network-only man-in-the-middle,
since an attacker on the LAN can't forge what's shown on the Windows screen or typed in by the
user. Once paired, `TrustedDevice` records persist locally on both sides; nothing about this
exchange is uploaded to a server in local mode. Session-level encryption is WebRTC's built-in
DTLS-SRTP; the pairing keypair is used to authenticate *which* device is on the other end of a
given session, not to encrypt the media itself.

**Signature format**: `PairingRequest.signature` is a DER-encoded ECDSA signature (RFC 3279
`SEQUENCE{r,s}`) — Java/Android's `Signature.getInstance("SHA256withECDSA")` default, which Windows
verifies via `ECDsa.VerifyData(..., DSASignatureFormat.Rfc3279DerSequence)` rather than .NET's own
default IEEE P1363 concatenation. It is computed over
`UTF8(pairing_token) ++ host_public_key ++ UTF8(requester.device_id)` (`PairingSignature` on both
platforms) — binding the token, the specific host being paired with, and the requester's own
identity together so the signature can't be replayed against a different offer or claimed by a
different device.

This is a summary of the design, not an independent security review.

## Versioning

See [`Protocol/README.md`](../Protocol/README.md#versioning-rules) for the concrete rules. Summary:
additive changes are free; anything wire-breaking bumps `Envelope.protocol_version` and both sides
must be able to detect and reject/degrade against a version they don't understand rather than
misparse it.

## Message catalog

| File | Messages | Purpose |
|---|---|---|
| `common.proto` | `Platform`, `DeviceIdentity`, `SessionIdentity`, `ConnectionMedium` | Shared identity types every other file depends on |
| `capabilities.proto` | `VideoCodec`, `DisplayInfo`, `DeviceCapabilities`, `NegotiatedConfig` | Capability advertisement + negotiation result (spec §40) |
| `pairing.proto` | `PairingOffer`, `PairingRequest`, `PairingResponse`, `TrustedDevice` | QR/PIN pairing handshake, device trust records |
| `input.proto` | `MouseMove/ButtonEvent/Scroll`, `KeyEvent`, `TextInput`, `Gamepad*Event` | Remote input (spec §12–§15) |
| `clipboard.proto` | `ClipboardUpdate` | Chunked clipboard sync (spec §16) |
| `file_transfer.proto` | `FileTransferOffer/Response/Progress/Control` | File-transfer metadata/lifecycle (spec §17) |
| `diagnostics.proto` | `ConnectionState`, `ConnectionStatus`, `StreamStats` | Connection/streaming diagnostics overlay (spec §10, §27) |
| `signaling.proto` | `SessionOffer`, `SessionAnswer`, `IceCandidate` | WebRTC SDP/ICE exchange, relayed over the pairing TCP connection (no signaling server in local mode) |
| `envelope.proto` | `Envelope` | The one message actually sent on the wire |

## Explicitly out of scope for this schema

- Video/audio codec bitstreams — native WebRTC media tracks.
- Raw file bytes — a dedicated data channel, correlated by `transfer_id`.
- Account/auth (email, password, tokens) — that's an HTTP/WebSocket API against the Backend
  service in remote mode, not a peer-to-peer data channel message; see `docs/architecture.md`.
