# ReyDesk identity, pairing and rendezvous

Design and security notes for device identity, the pairing methods, the rendezvous
(signaling) service and connection diagnostics. The wire schema is in
`Protocol/reydesk/v1/*.proto`; the transport overview is in `protocol.md`.

## 0. Fixed constraints and what stays exactly as is

Design decisions: device ID + keys, no account; optional ReyDen link later; no mandatory relay; free core; P2P first; honest diagnostics with a TCP/443 path; Go + PostgreSQL + Docker.
Must not weaken (current code, cited):
- Pinned TLS on LAN: Android `PairingClient.kt:55-68` compares leaf SPKI to the expected host key, no CA.
- Exact-key trust: `TrustedDeviceStore.cs:75-88` and `TrustedDeviceStore.kt:34-44` (FixedTimeEquals on stored key).
- Single-use challenge and bounded attempts: `PairingGate` TryAttempt/TryConsume, `PairingListener.cs:99-118`.
- On-PC approval for new devices: `PairingListener.cs:102` (60 s).
- Per-device opt-in unattended reconnect with mutual domain-separated signatures: `PairingSignature.BuildReconnectPayload`, `PairingListener.ValidateReconnectAsync`, default off.
- Phone-side reconnect only from an explicit foreground action (`PairingClient.reconnect`).

Core idea (one sentence): the signaling server only routes bytes; every trust decision still happens inside the same pinned TLS + signature protocol that runs on LAN today, now carried either over a LAN TCP socket or over a server-relayed byte pipe.

## 1. Identity model

### 1.1 Key pair: keep EC P-256 (ECDSA, SHA-256, DER signatures)
- Android Keystore generates non-exportable P-256 keys on every API level the app supports (minSdk 29), in TEE and in StrongBox. Ed25519 in Keystore needs Android 13+ and is not StrongBox-backed.
- .NET 8 has native ECDsa P-256; it has no built-in Ed25519 (would need BouncyCastle, a new dependency).
- Go stdlib `crypto/ecdsa` verifies the same DER signatures; existing signatures and SPKI pinning already use it (`DeviceIdentityService.cs:68`, `DeviceIdentityService.kt:59`).
- No migration of existing keys: every installed identity stays valid.
Rejected: Ed25519 (no .NET built-in, Keystore API 33+ only, would break all trust records). RSA (big keys and QR payloads).

### 1.2 Three identifiers, one root of trust
| Name | Value | Who makes it | Used for | Secret? |
|---|---|---|---|---|
| Key fingerprint | SHA-256(SPKI DER), 32 bytes | local | the real identity; trust stores keep the full SPKI and compare exactly | no |
| Key ID | first 60 bits of fingerprint, 12 Crockford chars, shown `KEY 4F7Q-2M9X-HC3D` | local, offline | display, LAN-only installs, SAS fallback reference | no |
| ReyDesk ID (Desk ID) | 55 random bits + 1 check char, 12 Crockford chars, shown `7KQ4-MZ2P-X9DF` | server, on first registration | routing handle for internet connections, typed by users | no |
- The existing `DeviceIdentity.device_id` (random UUID, `DeviceIdentityService.cs:72`, `.kt:71`) stays unchanged as the local record key, so existing trust records keep working. It is never shown or typed.
- Security never depends on the Desk ID or Key ID being secret or unguessable. Anyone may know them. Access always needs a key the peer already trusts, or a fresh first-time enrollment approved on the PC.
- LAN-only users never contact the server: Key ID is locally unique for display (60 bits; trust uses the full key), and every LAN flow works offline.

### 1.3 Desk ID format
- Alphabet: Crockford base32 `0123456789ABCDEFGHJKMNPQRSTVWXYZ`. Input normalization: uppercase, `I`/`L` -> `1`, `O` -> `0`, drop `-`, spaces and `U`; Persian/Arabic digits via `Character.digit` / `char.GetNumericValue`. Exactly 12 chars after normalization.
- Data: 11 chars from 55 bits of CSPRNG. Check char: `c = (sum over i=0..10 of (i+1) * v_i) mod 31`, encoded as the char for value c. Detects all single-char typos except value differences of 31 (0 vs Z) and almost all adjacent swaps. Clients reject a bad check char before any network call (no server load from typos).
- Display: three groups of 4 with `-`; LTR isolated (`⁦...⁩`) inside Persian/Arabic UI.
- Uniqueness: `desk_id` is the PRIMARY KEY. Allocation = `INSERT ... ON CONFLICT DO NOTHING RETURNING desk_id`; on no row, draw again, max 5 tries, then 503. Revoked IDs keep their row forever (tombstone), so an ID is never reissued.
Rejected: 9-digit numeric IDs (enumerable, TeamViewer-style), IDs derived from hardware (privacy, spoofable), Desk ID = hash of key (IDs are server-allocated; hashing the key would also make key rotation impossible).

### 1.4 Registration and key binding (rendezvous service)
- First connect with no stored Desk ID: client proves key possession (section 3.2); server inserts `(desk_id, key_fp, spki)`. `key_fp` is UNIQUE: a key that is already registered gets its existing Desk ID back (idempotent; fixes lost local state).
- Connect with a stored Desk ID: row must exist, not revoked, and `key_fp` must equal the presented key. Different key -> `AUTH_KEY_MISMATCH`, connection closed, nothing changes. There is no API that rebinds an ID to a new key in v2.
- Reinstall / lost Keystore key = new key = new Desk ID. Trusted peers must re-pair (exact-key trust). Old ID becomes stale; rows with no auth for 365 days are marked `revoked_at` (tombstone, not deleted).
- Self revocation: Settings > Internet access > "Reset ReyDesk ID" sends a signed `RevokeSelf`; server sets `revoked_at`; client registers again and gets a new ID. Peer trust revocation is local only (trust stores), the server never decides trust.
- Recovery / key rotation onto the same ID: NOT in v2. Later via optional ReyDen account link.
- DB lost: clients reconnect with Desk ID + key, row missing. Default: server answers `AUTH_UNKNOWN_ID`, client discards the old ID and registers a new one (peers re-pair only if they relied on the ID; trusted peers store the key, so a trusted phone just looks up the host's new ID through LAN or a fresh QR). Optional: if env `RECLAIM_UNTIL` (RFC 3339) is set after a restore, an unknown well-formed ID may be re-bound to the presenting key until that time. Hijacking an ID during that window only enables a fake "new PC" (first pairing still needs SAS + PC approval) and cannot touch trusted reconnects (key-pinned). DB goes into the server backup script at deploy time.

### 1.5 Enumeration and abuse limits (server, in-memory token buckets, single instance)
- Registrations: 20/hour and 100/day per client IP (CGNAT-friendly), 5 auth failures/min per IP then 10 min block.
- Pipe opens per authenticated device: 10 distinct target IDs per 10 min, 60 per day; a target that is unknown, offline, revoked or rejects counts 2. Exceed -> 1 h block for that device key.
- Pipe opens per target: 20 per minute total, max 4 waiting for host decision.
- Unknown, offline, revoked and "host declined" all return the same `PIPE_UNAVAILABLE` after a fixed 300 ms floor, so the server is not an existence oracle beyond "reachable now".
- Remote pairing code: 5 wrong attempts per code (server) then the slot is destroyed; host also counts.
- 55-bit space: with 1 million registered IDs a random guess hits about 3e-11; with the device limits above enumeration is not practical, and a hit gives nothing without a pairing code or trusted key.

### 1.6 Fingerprint display
- Device cards (both apps) show Key ID; an "Advanced" expander shows the full fingerprint as 13 groups of 4 Crockford chars.
- SAS (section 2.2) is what users actually compare at enrollment; full fingerprints are for support.

## 2. Pairing flows

### 2.1 Methods and where the 6-digit code survives
| Method | Host key learned from | Proof of human presence | Code/token | SAS |
|---|---|---|---|---|
| M1 LAN QR | QR (authenticated channel) | PC approval | 128-bit random token in QR, never shown | no |
| M2 LAN nearby (mDNS) | mDNS TXT (unauthenticated) | PC approval showing SAS | none | yes |
| M3 Remote QR | QR | PC approval | 128-bit token in QR | no |
| M4 Remote typed | rendezvous server (untrusted) | PC approval showing SAS | Desk ID + 6-digit code | yes |
| M5 Trusted attended reconnect | trust store | PC "Accept" prompt | none | no |
| M6 Trusted unattended reconnect | trust store | none (opt-in per device) | none | no |
- The 6-digit code exists only in M4. It is safe now because it is never an identity: it is namespaced under one Desk ID, created only when the owner presses "Add phone over internet" on the PC, single use, 5 min TTL, 5 attempts (server) + 5 (host), and success still requires SAS match plus on-PC approval. Its job is anti-spam (no approval prompts from strangers who know the Desk ID), not authentication against the server: a malicious server could brute-force the hash, which is why SAS + key pinning carry the security.
- The LAN 6-digit PIN UI is removed. The original "keep PIN flow" requirement is satisfied by M2 (no typing) and M4 (typed code). Two PCs showing the same code cannot collide: the code is checked only inside one Desk ID's slot.
- QR token: `PairingOffer.pairing_token` becomes 22-char base64url of 16 random bytes (QR only). Validation accepts length 22 for QR methods and 6 for M4.

### 2.2 SAS (short authentication string), commit-then-reveal
Inside the established TLS stream, before the host accepts:
1. Phone: `PairingRequest{method, sas_commitment = SHA256("ReyDesk/sas-commit/v1" || n_c)}` with n_c = 32 random bytes.
2. Host: `PairingResponse{stage=SAS_HOST_NONCE, sas_host_nonce = n_h}` (32 random bytes, sent only after the commitment arrived).
3. Phone: `PairingRequest{stage=SAS_REVEAL, sas_client_nonce = n_c}`; host checks the commitment (FixedTimeEquals).
4. Both: `h = SHA256(LP("ReyDesk/sas/v1") LP(host_spki) LP(client_spki) LP(n_h) LP(n_c))`, `SAS = (big-endian uint32 of h[0..3]) mod 1_000_000`, shown `482 913` in a distinct "Compare" card (localized digits). LP = 4-byte big-endian length prefix, the same encoding as `BuildReconnectPayload`.
5. Phone user taps "Codes match" -> `PairingRequest{stage=SAS_CONFIRM}`. Host approval dialog shows SAS + phone name + Key ID; owner approval = host-side confirmation.
- host_spki on the phone is the key of the TLS certificate it pinned; client_spki on the host is the key the request was signed with. A relaying MITM has different keys on each leg and, because of commit-then-reveal, wins with probability about 1e-6 per attempt; host rate limit: 3 rejected or mismatched SAS -> SAS methods paused 5 min.

### 2.3 Signed request v2 (replaces `BuildSignedPayload`)
`sig = ECDSA(client_key, LP("ReyDesk/pair/v2") LP(method) LP(token or empty) LP(host_spki) LP(client_spki) LP(requester_device_id) LP(host_desk_id or empty) LP(n_h or empty) LP(n_c or empty))`. Sent in the final request (after reveal for SAS methods). Host verifies with the presented key exactly as `PairingListener.Validate` does now. v1 requests (`method` unspecified) get `REJECTED "Update ReyDesk on the phone"`; both apps update together (existing migration policy).
Exception: M5 (TRUSTED_ATTENDED) no longer uses this payload. It signs the attended v3 payload of section 2.8 over a fresh host nonce. A host never builds or accepts a `ReyDesk/pair/v2` payload with method 5, so every M5 signature made before this change is dead.

### 2.4 Message flows
Transport T = LAN TCP 47821 + TLS (today) or a relay pipe + TLS (section 3.4). In both cases Windows is TLS server with its identity certificate and the phone pins.
- M1 LAN QR: QR `reydesk://pair/<b64url PairingOffer>` now also carries `host.desk_id` (if any) and `rendezvous_url`. Phone pins `offer.host_public_key`, TLS, PairingRequest(method=LAN_QR, token, sig) -> host Validate -> TryConsume -> approval dialog -> trust added -> PairingResponse(ACCEPTED) -> same socket becomes SignalingConnection. Identical to today plus fields.
- M2 LAN nearby: phone browses mDNS; TXT adds `did` (Desk ID) and `kid` (Key ID) beside existing `v,id,name,key` (additive; old parsers ignore). Phone pins TXT key, TLS, SAS steps 1-3, both show SAS, phone confirms, host dialog shows SAS, owner approves, signed request v2, trust added. Host accepts M2 only while the Connection panel is open (an offer is current) and shows at most one approval dialog at a time.
- M3 Remote QR: same QR as M1. If the phone cannot reach `local_ip_hint:47821` within 3 s, it opens a relay pipe to `host.desk_id` with purpose PAIR_QR; then M1 steps over the pipe. QR token still single-use.
- M4 Remote typed: PC "Add phone over internet" -> host makes code, sends `PairSlotCreate{code_hash = SHA256(LP(desk_id) LP(code) LP(salt)), salt, ttl=300}`. Phone enters Desk ID + code -> `OpenPipe{target, purpose=PAIR_CODE, code}` -> server checks slot (constant-time), decrements attempts -> host gets `IncomingPipe{pipe_id, requester_desk_id, requester_fp, purpose}` -> host `PipeDecision{accept}` -> phone gets `PipeOpened{pipe_id, host_spki}` -> phone pins host_spki (untrusted until SAS) -> TLS -> SAS -> signed request v2 with token=code (host re-checks code) -> approval -> trust added with `remote_allowed = true` -> server deletes the slot on first success.
- M5 Trusted attended reconnect: phone (foreground tap) connects by LAN if mDNS shows the trusted key, else pipe purpose=RECONNECT. Pinned TLS to stored key. Phone sends `PairingRequest{method=TRUSTED_ATTENDED, attended_client_nonce=n_c}` (unsigned); host checks trust and issues a fresh `PairingResponse{stage=ATTENDED_CHALLENGE, attended_host_nonce=n_h}`; phone answers `PairingRequest{stage=ATTENDED_PROOF, sig}` over the attended v3 payload; host verifies (single use, 30 s) and only then shows "Pixel 8 wants to connect" Accept/Decline (30 s). Full rules in section 2.8. Replaces typing the PIN for a trusted PC.
- M6 Trusted unattended reconnect: as today (`unattended_reconnect=true`, nonce, host challenge + signature, client proof), carried over LAN or pipe. Over a pipe it additionally requires the device's `remote_allowed` flag. Change: the challenge expiry is `now + 60 s` per connection instead of the displayed offer's expiry, and the gate counts per connection, so reconnect no longer depends on (or consumes) the QR offer.
- Session start after any method: unchanged. Windows offers, Android answers, SDP/ICE ride the same TLS stream as Envelopes, so DTLS fingerprints are authenticated end to end even when the server relays the pipe.

### 2.5 Upgrade / downgrade
- Path choice is ICE's job: LAN host candidates win when both are on the same network, even if signaling came over the pipe.
- Downgrade: if the PeerConnection is Disconnected for 5 s, Windows performs one ICE restart over the existing signaling stream. If the signaling stream itself died (LAN socket gone because the phone left Wi-Fi), the session ends and the phone offers "Reconnect over internet" (M5/M6 over pipe). No seamless handover in v2.
- Next connect always tries LAN first when mDNS shows the trusted key, then the pipe.

### 2.6 Trust record changes and migration
- `TrustedDevice` gains `desk_id`, `remote_allowed`, `paired_method`. Windows JSON record gains `DeskId`, `RemoteAllowed` (missing = false).
- Existing records: kept, `RemoteAllowed = false`. The device card offers a toggle "Allow over internet" (Windows) and the phone shows "Internet: off, enable on the PC". Unattended over internet needs both flags.
- New LAN pairings: approval dialog checkbox "Also allow over the internet" (default off). New remote pairings: true.
- Phone learns the host's Desk ID from QR, TXT or `PipeOpened`, and from a new `HostInfo{desk_id}` Envelope the host sends after each pairing (only updates a record whose key matches exactly).

### 2.7 Threat model
| Threat | Mitigation |
|---|---|
| Malicious or hacked signaling server | Server only sees routing metadata and TLS ciphertext; host cert pinned from QR/trust store, or SAS-verified for M2/M4; SDP inside TLS; server cannot create trust, cannot pass approval, cannot sign reconnects. It can deny service and see who connects to whom and IP addresses (stated in privacy text). |
| ID enumeration | 55-bit random IDs, per-device and per-IP limits (1.5), uniform `PIPE_UNAVAILABLE`, knowing an ID grants nothing. |
| Code brute force | Code only valid inside one Desk ID slot opened by the owner, 5 min, 5 server + 5 host attempts, single use, plus SAS + approval. |
| Replay | TLS per connection; single-use QR token (M1/M3); fresh host nonces in SAS (M2/M4), M5 attended reconnect (section 2.8) and M6 unattended reconnect; every host nonce or challenge is single use per connection with a short expiry (M5 30 s, M6 60 s); auth challenge single use 30 s; signatures domain-separated and bound to both keys and IDs. Residual: a copied M5 proof (from a log, a memory dump, or a phone that was removed later) is useless because it covers an n_h the host already discarded, and a replayed request never reaches the Accept prompt. What remains is live control of a still-trusted phone key: that attacker can make a fresh request and still needs the owner's Accept click; revoking the phone on the PC ends it. M1/M3 carry no host nonce in the signature, but the host-issued QR token is single use and expires with the offer, so a copied request is dead once the real one is consumed. |
| MITM on LAN or via server | Pinned TLS; SAS commit-reveal (about 1e-6 per try) when the key is not from QR/trust; exact-key trust comparison. |
| Rogue TURN relay | Relay carries DTLS-SRTP/SCTP-DTLS only; fingerprints come via authenticated signaling; relay is opt-in and off by default. |
| Stolen phone | Keystore key is non-exportable; reconnect requires a foreground tap; v2 adds Settings option "Require phone unlock to reconnect" using `BiometricPrompt` with device credential fallback (Keystore key `setUserAuthenticationRequired` is NOT used because it would break existing keys); owner revokes the device on the PC, which kills sessions (`OnTrustChanged`) and fails the next exact-key check. |
| Stolen PC identity file | DPAPI CurrentUser protection as today; attacker with the user's Windows session already owns the PC. Out of scope. |
| Approval fatigue / prompt spam | Prompts only during an owner-opened code slot (M4), open Connection panel (M2), or from already trusted keys (M5); one dialog at a time; SAS cooldown. |
| Server DB loss / ID hijack | 1.4: IDs are routing only; reclaim window opt-in; trusted peers pin keys. |

### 2.8 M5 attended reconnect challenge (attended v3)
Before this change the M5 request was `ReyDesk/pair/v2` with no nonces, so it was identical every time and replayable forever. M5 now gets one extra round trip on the same pinned TLS stream. No server change; the challenge comes from the host, never from the rendezvous server.

Flow (one challenge and one proof per TLS connection; a retry is a new connection and a new nonce):
1. Phone -> `PairingRequest{method=TRUSTED_ATTENDED, stage=UNSPECIFIED, requester, requester_public_key, attended_client_nonce=n_c}`. n_c = 32 fresh random bytes. `signature`, `pairing_token`, `sas_*`, `reconnect_client_nonce` empty, `unattended_reconnect=false`.
2. Host checks, in order, before issuing anything: request shape (above; a request that carries a signature and no n_c is the old one-shot format and gets `REJECTED "Update ReyDesk on the phone"`), exact-key trust, `remote_allowed` when the medium is Remote, and over a pipe both Desk IDs present and canonical (below). Any failure: `REJECTED`, no nonce issued, no prompt.
3. Host -> `PairingResponse{stage=ATTENDED_CHALLENGE, result=UNSPECIFIED, attended_host_nonce=n_h}`. n_h = 32 bytes from the OS CSPRNG (`RandomNumberGenerator`), drawn fresh for this connection. The host keeps n_h, n_c, the client key, device ID and both Desk IDs in connection-local state and sets `deadline = monotonic_now + 30 s` (monotonic clock, not wall time; nothing time-related is sent or signed).
4. Phone checks the reply: `stage == ATTENDED_CHALLENGE`, `result == UNSPECIFIED`, `attended_host_nonce` exactly 32 bytes; anything else (including `AWAITING_APPROVAL` or a success at this point) is a downgrade: throw, never sign. Then sign at once (any "Require phone unlock" prompt happens before the link is opened, never between challenge and proof) and send `PairingRequest{method=TRUSTED_ATTENDED, stage=ATTENDED_PROOF, requester, requester_public_key, signature}` with `attended_client_nonce` empty (the host uses its stored copy).
5. Host reads exactly one proof with a read timeout equal to the time left before `deadline`. Before verifying, it takes n_h out of the state (copy to a local, clear the state), so any outcome consumes it. Checks: `monotonic_now < deadline`, `stage == ATTENDED_PROOF`, same method, same device ID, `requester_public_key` equal to the stored key (FixedTimeEquals), token, nonce and SAS fields empty, signature 8..144 bytes, then ECDSA verify over the attended v3 payload. Only after a valid proof does the host send `AWAITING_APPROVAL` and show the Accept/Decline prompt (unchanged: 30 s, one dialog at a time, re-check trust and `remote_allowed` after the click). n_h and n_c are zeroed in a `finally`.

Signed message (ECDSA P-256 over SHA-256, DER, client key; LP as in 2.2):
`attended_v3 = LP("ReyDesk/attended/client/v3") LP(host_spki) LP(client_spki) LP(requester_device_id) LP(host_desk_id) LP(requester_desk_id) LP(n_h) LP(n_c)`
- Domain tag `ReyDesk/attended/client/v3` (26 bytes) names the method, the direction (client proof to host) and the version; v3 follows `pair/v2` so an old M5 signature can never verify. There is no host-signed counterpart: the pinned TLS already authenticates the host.
- host_spki = the pinned host key (phone: stored trust key; host: its identity SPKI). client_spki = the key in `requester_public_key`. Full SPKIs are signed, which is stronger than Key IDs (Key IDs are derived from them), so Key IDs are not added.
- requester_device_id = `DeviceIdentity.device_id` exactly as sent.
- host_desk_id and requester_desk_id: both empty on the LAN medium; both the canonical 12-char Desk ID over a rendezvous pipe. Host side: host_desk_id = its own Desk ID (the pipe target), requester_desk_id = the canonical form of `IncomingPipe.requester_desk_id`; missing or unparsable -> reject before step 3. Phone side: host_desk_id = the pipe target it opened, requester_desk_id = its own Desk ID from the same authenticated rendezvous connection that opened the pipe. A server that misreports either ID only causes a failed verify (denial of service, which it can do anyway).
- n_h, n_c: exactly 32 bytes each, never empty.
- No timestamp is signed; freshness comes only from the single-use n_h and the host-side 30 s deadline.

Host rules: one n_h per connection and per attempt; never reissued or reused; discarded on use, on any failure, on timeout and on connection close; never logged (I8 rule); never compared by value (the signature binds it, and the only byte comparisons, key equality, use FixedTimeEquals); state lives only in the connection's flow, so there is no global nonce table to grow or race. The existing 180 s connection lifetime and the 4 pending-connection cap bound outstanding challenges.

Failure modes:
| Condition | Host | Phone |
|---|---|---|
| Untrusted key, or Remote with `remote_allowed` off | `REJECTED` before a nonce, no prompt | shows reason |
| Old one-shot signed request (no n_c) | `REJECTED "Update ReyDesk on the phone"` | n/a |
| Bad shape (n_c not 32 bytes, token, SAS fields, unattended flag) | `REJECTED "Invalid pairing request or code."` | n/a |
| Pipe without both canonical Desk IDs | `REJECTED`, no nonce | shows reason |
| No proof within 30 s of issue | n_h discarded, `REJECTED "Reconnect timed out. Tap Connect again."` or connection closed | "PC did not respond" / retry with a new connection |
| Proof with wrong stage, method, device, key, extra fields | n_h discarded, `REJECTED`, no prompt | n/a |
| Signature invalid (includes any replayed or copied proof) | n_h discarded, `REJECTED "Device identity proof failed."`, no prompt | shows reason |
| Old host (one-shot M5) receives the unsigned first message | rejects it (signature length check) | surfaces "Update ReyDesk on the PC" |
| Host answers with anything but `ATTENDED_CHALLENGE` + 32-byte n_h | n/a | throws, never signs |
| Success response that still carries `attended_host_nonce` or stage `ATTENDED_CHALLENGE` | n/a | rejects as unexpected |
| Owner declines, times out, or trust/remote revoked during the prompt | `REJECTED` (unchanged) | shows reason |

Other methods, same weakness? M2/M4: no, fresh n_h per connection via SAS. M6: no, fresh 32-byte host challenge, 60 s expiry, consumed per connection. M1/M3: no host nonce in the signature, but the host-issued QR token is single use (consumed before approval) and dies with the offer, so it works as the nonce; a copied request is dead once the real one lands. Narrow residual, no change: a request copied from the phone before it is delivered could race the real one inside the offer window and, for an already trusted key on LAN, pass without a prompt; that needs live access to the phone, which can sign anyway. Rendezvous auth and RevokeSelf sign a server nonce and are not affected.

## 3. Rendezvous (signaling) service, Go

### 3.1 Shape
- Folder `Backend/rendezvous/` (Go 1.25, stdlib net/http). Single binary, single instance; presence, pipes, slots and rate buckets are in memory; PostgreSQL holds devices only.
- Endpoints: `GET /v1/ws` (WebSocket, binary frames), `GET /api/health` (200 `{"status":"ok","db":"ok"}` after `SELECT 1`, 503 otherwise). Nothing else; no REST lookup of IDs.
- Wire: protobuf `reydesk.v1.RendezvousFrame` in `Protocol/reydesk/v1/rendezvous.proto`, one frame per WS binary message. Chosen because the schema is shared with both apps, pipe payloads are raw bytes (JSON would need base64 and a second schema). Rejected: JSON (duplicate DTOs, +33% for pipe bytes), gRPC (HTTP/2 framing harder through CDNs and Iranian middleboxes).
- New Go dependencies (`github.com/coder/websocket`, `github.com/jackc/pgx/v5`, `google.golang.org/protobuf`. Migrations: embedded SQL files run in order at startup with a `schema_migrations` table (ReyDen precedent, no framework).

### 3.2 Authentication (no passwords)
1. Server -> `ServerHello{nonce(32), server_time_ms, protocol=1}` immediately on upgrade.
2. Client -> `ClientAuth{spki, desk_id (may be empty), platform, app_version, sig}` where `sig = ECDSA(LP("ReyDesk/rendezvous/auth/v1") LP(nonce) LP(public_ws_url) LP(spki) LP(desk_id))`.
3. Server verifies (P-256, DER, SPKI <= 128 bytes), applies 1.4 rules -> `AuthResult{ok, desk_id, error}` then `IceConfig`. Nonce single use, 10 s auth deadline, any other frame before auth closes the socket.

### 3.3 Frames (`RendezvousFrame { uint64 seq = 1; oneof msg {...} }`)
| # | Message | Dir | Fields / rule |
|---|---|---|---|
| 10 | ServerHello | S->C | nonce, server_time_ms, protocol |
| 11 | ClientAuth | C->S | above |
| 12 | AuthResult | S->C | ok, desk_id, error enum (KEY_MISMATCH, UNKNOWN_ID, REVOKED, RATE_LIMITED, BAD_SIGNATURE) |
| 13 | IceConfig | S->C | repeated IceServer{urls, username, credential}, expires_at_ms; resent on `IceConfigRequest` (14) |
| 20 | PairSlotCreate | C->S | code_hash(32), salt(16), ttl_s (<=300); one slot per device, replaces old |
| 21 | PairSlotCancel | C->S | - |
| 22 | PairSlotStatus | S->C | attempts_left, expired |
| 30 | OpenPipe | C->S | target_desk_id, purpose (PAIR_CODE, PAIR_QR, RECONNECT), code (PAIR_CODE only, 6 digits) |
| 31 | IncomingPipe | S->C | pipe_id, requester_desk_id, requester_fp, purpose |
| 32 | PipeDecision | C->S | pipe_id, accept; host must answer in 10 s |
| 33 | PipeOpened | S->C | pipe_id, peer_spki |
| 34 | PipeFailed | S->C | pipe_id, reason (UNAVAILABLE, RATE_LIMITED, BAD_CODE, TIMEOUT) |
| 35 | PipeData | both | pipe_id, bytes (<= 16 KiB) |
| 36 | PipeClose | both | pipe_id |
| 40/41 | Ping / Pong | both | every 25 s; close after 60 s silence |
| 50 | RevokeSelf | C->S | signed with domain "ReyDesk/rendezvous/revoke/v1" over nonce |
| 60 | Error | S->C | code, retry_after_ms |
- "Pair-request mailbox with TTL": an `OpenPipe` to a host that is registered but momentarily disconnected waits up to 20 s for it to come back, then `PipeFailed{UNAVAILABLE}`. In memory only.
- Host presence: a host is "online" while its authenticated WS is open. Windows keeps the WS open only when Settings > "Internet access" is on (default off).

### 3.4 Pipes
- A pipe is an ordered byte stream between two authenticated sockets; clients wrap it as a Stream and run TLS over it (Windows `SslStream`, Android `SSLEngine` adapter).
- Limits per pipe: 1 MiB total, 64 KiB/s sustained, 10 min idle, 6 h max life; per device 8 open pipes. These make the service useless as a data relay while leaving room for SDP, ICE trickle and restarts.
- Pipes do not survive a WS reconnect; the app reconnects at the pairing level.

### 3.5 Reconnection, limits, logging
- Client backoff: 1, 2, 4 ... 60 s with +-20% jitter; resets after 5 min connected. ICE config refreshed on every auth.
- WS read limit 32 KiB per frame; 50 frames/s burst 100 per connection; 200 concurrent connections per IP; global max connections env.
- Logs: JSON lines to stdout, docker log rotation 14 days. Fields: event, result code, `desk_id` first 4 chars, `ip_hash` = HMAC-SHA256(daily random key held in memory, IP) first 8 bytes. Never logged or stored: PipeData, codes, code hashes, SDP/ICE, full IPs, full IDs, keys.
- Client IP from `X-Forwarded-For` only when the peer address is in `TRUSTED_PROXY_CIDRS`.

### 3.6 PostgreSQL DDL
```sql
CREATE TABLE IF NOT EXISTS schema_migrations (version int PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE IF NOT EXISTS devices (
  desk_id      char(12)    PRIMARY KEY CHECK (desk_id ~ '^[0-9A-HJKMNP-TV-Z]{12}$'),
  key_fp       bytea       NOT NULL UNIQUE CHECK (octet_length(key_fp) = 32),
  spki         bytea       NOT NULL CHECK (octet_length(spki) BETWEEN 59 AND 128),
  platform     smallint    NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  last_auth_at timestamptz NOT NULL DEFAULT now(),
  revoked_at   timestamptz
);
CREATE INDEX IF NOT EXISTS devices_last_auth_idx ON devices (last_auth_at) WHERE revoked_at IS NULL;
CREATE TABLE IF NOT EXISTS daily_stats (
  day date PRIMARY KEY, registrations int NOT NULL DEFAULT 0, auths int NOT NULL DEFAULT 0,
  pipes_opened int NOT NULL DEFAULT 0, pipes_failed int NOT NULL DEFAULT 0
);
```
`last_auth_at` is updated at most once per hour per device. The server's check-char validation mirrors the clients'.

### 3.7 Config (env)
`LISTEN_ADDR` (:8080), `DATABASE_URL`, `PUBLIC_WS_URL` (bound into auth signatures, e.g. `wss://<domain>/v1/ws`), `TRUSTED_PROXY_CIDRS`, `STUN_URLS` (comma list), `TURN_URLS` (comma list, empty = no relay), `TURN_SECRET` (coturn `static-auth-secret`), `TURN_TTL_S` (3600), `RECLAIM_UNTIL` (empty), `MAX_CONNECTIONS` (5000), `LOG_LEVEL`.

### 3.8 Docker (local only)
- `Backend/rendezvous/Dockerfile`: multi-stage `golang:1.25` build -> `gcr.io/distroless/static` runtime, non-root, `HEALTHCHECK` on `/api/health`.
- `Backend/rendezvous/compose.yaml`: service `rendezvous` on `127.0.0.1:${APP_PORT:-13260}:8080`, joins the external `devdb` network (local dev setup). `compose.standalone.yaml` override adds `postgres:16` for machines without devdb. Profile `turn` adds `coturn/coturn` with `use-auth-secret`, listening 3478 udp/tcp and 5349 tls (self-signed locally). Production port, domain, TLS and firewall are a later owner CONFIRM.

### 3.9 ICE servers for clients
- LAN medium: empty ICE server list, exactly as today (no external STUN traffic on LAN).
- Remote medium: `STUN_URLS` from `IceConfig`. Default compose value: the service's own coturn `stun:<host>:3478`. Public STUN (e.g. Google) only if the operator opts in; reachability in Iran unverified.
- TURN (optional, default disabled server-side; client setting "Allow relay through ReyDesk server", default off): coturn REST credentials `username = "<expiry_unix>:<key_fp_hex16>"`, `credential = base64(HMAC-SHA1(TURN_SECRET, username))`, TTL 1 h, urls `turn:H:3478?transport=udp`, `turn:H:3478?transport=tcp`, `turns:H:443?transport=tcp`.

### 3.10 TCP/443 plan
- Signaling: WSS on 443 only (normal HTTPS through nginx). If Arvan's proxy does not pass WebSocket, use a DNS-only hostname for the WS (decided at deploy).
- Media over TCP: Android libwebrtc supports ICE-TCP and TURN over TCP/TLS. SIPSorcery 10.0.16 support for TURN-TCP/TLS and ICE-TCP is NOT verified; this has to be measured. If missing, the TCP/443 media path is unavailable when Windows is the host, and the app says so (Diagnostics state BLOCKED with VPN guidance). No workaround is promised in v2.
- Explicitly NOT promised: connection through every NAT/CGNAT/firewall; working during Iranian UDP shutdowns; any relay capacity or free relay bandwidth; pre-login access; connection without the PC app running and Internet access turned on; low latency on relayed paths; signaling availability (no SLA, single instance).

## 4. Proto changes
- `common.proto` `DeviceIdentity`: `string desk_id = 5;`
- `pairing.proto`: `enum PairingMethod { UNSPECIFIED=0; LAN_QR=1; LAN_SAS=2; REMOTE_QR=3; REMOTE_CODE=4; TRUSTED_ATTENDED=5; TRUSTED_UNATTENDED=6; }`, `enum PairingStage { STAGE_UNSPECIFIED=0; SAS_HOST_NONCE=1; SAS_REVEAL=2; SAS_CONFIRM=3; AWAITING_APPROVAL=4; }`.
  `PairingOffer`: `string rendezvous_url = 7;` (host desk id rides in `host.desk_id`).
  `PairingRequest`: `PairingMethod method = 7; PairingStage stage = 8; bytes sas_commitment = 9; bytes sas_client_nonce = 10;`
  `PairingResponse`: `PairingStage stage = 6; bytes sas_host_nonce = 7; string host_desk_id = 8;`
  `TrustedDevice`: `string desk_id = 6; bool remote_allowed = 7; PairingMethod paired_method = 8;`
  M5 challenge (section 2.8): `PairingStage` adds `PAIRING_STAGE_ATTENDED_CHALLENGE = 5; PAIRING_STAGE_ATTENDED_PROOF = 6;`, `PairingRequest` adds `bytes attended_client_nonce = 11;`, `PairingResponse` adds `bytes attended_host_nonce = 9;`.
- `envelope.proto`: `HostInfo host_info = 90;` with `message HostInfo { string desk_id = 1; }` (defined in pairing.proto).
- `diagnostics.proto`: `enum PathKind { PATH_UNSPECIFIED=0; PATH_LAN_DIRECT=1; PATH_REMOTE_DIRECT=2; PATH_RELAYED=3; }` and `ConnectionStatus.path = 4;`
- New `rendezvous.proto` (section 3.3). `Envelope.protocol_version` stays 1; v2 pairing is detected by `method != UNSPECIFIED`.

## 5. Connection Diagnostics (truthful states)
Path state comes from the selected ICE candidate pair (local/remote candidate types: host = direct; srflx/prflx = remote direct; relay = relayed).
| State | Detection | Guidance shown |
|---|---|---|
| LAN direct | both candidates `host`, private ranges | none |
| Remote direct | srflx/prflx pair succeeded | none |
| Relayed | `relay` candidate in selected pair | "Using the ReyDesk relay; slower, uses server bandwidth." |
| Service not configured | no rendezvous URL | "Internet connection service is not set up in this build." |
| Service unreachable | WSS connect fails (DNS, TCP, TLS separated) | check internet, try another network or VPN |
| PC not reachable | `PIPE_UNAVAILABLE` | PC offline, Internet access off on the PC, or wrong ID |
| Not allowed | host rejects in TLS (remote_allowed false, unattended off) | "Enable Allow over internet for this phone on the PC." |
| Blocked | pipe works, ICE fails in 20 s; tests: STUN response received or not (UDP blocked), srflx types differ per server (symmetric/CGNAT likely), TURN off | "Direct connection was blocked by the network (UDP filtered or CGNAT). Options: same Wi-Fi, a VPN you control, or enable relay if available." Never "works everywhere". |
| Auth failure | key mismatch / revoked | re-pair instructions |
- Replace `diagnostics.internet` (Strings.cs:153) text; keep `diagnostics.vpn` (154). Android Settings gets the same section. A "Run test" button performs: rendezvous auth, STUN binding to each STUN URL, TURN allocation if enabled; results listed with pass/fail and timestamps.
