# coturn for ReyDesk

`turnserver.conf` is the hardened config (T18 H2): REST auth only (`use-auth-secret`), no CLI, every private,
loopback, link-local and multicast peer range denied, per-user and total quotas, bandwidth caps. It has no
`allow-loopback-peers` and must never get one outside the isolated spike.

Start (from `Backend/rendezvous`):

```
TURN_SECRET=<long random> TURN_REALM=<host> TURN_EXTERNAL_IP=<public ip> docker compose --profile turn up -d turn
```

Then set on the rendezvous service: `TURN_RELAY_ENABLED=true`, `TURN_URLS=turn:<host>:3478?transport=udp,turn:<host>:3478?transport=tcp`,
the same `TURN_SECRET`. Leave the relay off until a real deployment needs it.

Consequence of the deny list: a relay between two peers that sit on private LAN addresses is refused on purpose (those
sessions use the direct LAN path). Relaying works for peers with public or CGNAT-mapped addresses seen by coturn.
Open the UDP/TCP 3478 and the `min-port`..`max-port` range in the host firewall only for this service.
