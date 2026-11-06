# ReyDesk Protocol

Versioned, platform-neutral wire schema for the ReyDesk control plane (pairing, capability
negotiation, input, clipboard, file-transfer metadata, diagnostics/connection state).

Video and audio are **not** defined here — they travel as native WebRTC media tracks (RTP), not
protobuf. See [`docs/protocol.md`](../docs/protocol.md) for the full message catalog and the
rationale for what is/isn't in this schema.

## Layout

```
Protocol/
└── reydesk/v1/
    ├── common.proto        DeviceIdentity, SessionIdentity, Platform, ConnectionMedium
    ├── capabilities.proto  DeviceCapabilities, NegotiatedConfig, DisplayInfo
    ├── pairing.proto       PairingOffer/Request/Response, TrustedDevice
    ├── input.proto         Mouse, keyboard (HID usage codes), gamepad
    ├── clipboard.proto     ClipboardUpdate (chunked)
    ├── file_transfer.proto Offer/Response/Progress/Control (metadata only, no file bytes)
    ├── diagnostics.proto   ConnectionStatus, StreamStats
    ├── signaling.proto     SessionOffer/Answer (SDP), IceCandidate — relayed over the pairing TCP connection
    └── envelope.proto      Envelope — the one message type actually sent on the wire
```

Every message on a ReyDesk data channel is a `reydesk.v1.Envelope`, whose `oneof payload`
selects which of the above it's carrying, versioned by `Envelope.protocol_version`.

## Generating code

Requires `protoc` (verified against **protoc 35.1**, installed here via `winget install
Google.Protobuf`).

### C# (Windows client, Backend)

For day-to-day development, add the `Grpc.Tools` NuGet package to a project and reference the
`.proto` files directly — it runs `protoc` automatically at build time via MSBuild targets, so no
manual step is needed there. To generate standalone for inspection:

```sh
protoc -I Protocol --csharp_out=<out-dir> Protocol/reydesk/v1/*.proto
```

### Kotlin (Android client)

Use the `com.google.protobuf` Gradle plugin (configured in `Android/app/build.gradle.kts`) with
the `kotlin` codegen option enabled — it runs at Gradle build time. To generate standalone:

```sh
protoc -I Protocol --java_out=<out-dir> --kotlin_out=<out-dir> Protocol/reydesk/v1/*.proto
```

## Versioning rules

- Never renumber or reuse a field number or a `oneof` tag once shipped.
- Adding a new field, message, or `oneof` case is backward compatible — do this freely.
- A wire-incompatible change (removing/retyping a field, renumbering) requires bumping
  `Envelope.protocol_version` and a documented migration in `docs/protocol.md`.
- Unknown `oneof` cases must be ignored by older clients, not treated as an error, so newer
  optional features degrade gracefully on older peers.
