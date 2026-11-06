# ReyDesk rendezvous server

Go service (module `reydesk/rendezvous`). Skeleton: env config, PostgreSQL pool, embedded migrations, `GET /api/health`
(200 when the DB answers, 503 otherwise). Spec: `docs/identity-and-pairing.md` (3.6-3.8).

## Layout
- `cmd/rendezvous` entrypoint (`rendezvous healthcheck` is the Docker HEALTHCHECK)
- `internal/config` env loading (3.7)
- `internal/db` pgx pool + migrations (`internal/db/migrations/NNN_name.sql`, applied at start under an advisory lock)
- `Dockerfile` multi-stage, distroless, non-root
- `compose.yaml` service on `127.0.0.1:${APP_PORT:-13260}`, joins external network `devdb`
- `compose.standalone.yaml` adds its own `postgres:16` (no devdb needed)

## Run
```
go vet ./... && go build ./...

# with the shared devdb (create the DB once)
docker exec devdb-postgres psql -U postgres -c "CREATE DATABASE reydesk"
docker compose up -d --build

# without devdb
docker compose -f compose.yaml -f compose.standalone.yaml up -d --build

curl -i http://127.0.0.1:13260/api/health
docker compose down
```
Env: `LISTEN_ADDR`, `DATABASE_URL` (required), `PUBLIC_WS_URL`, `TRUSTED_PROXY_CIDRS`, `STUN_URLS`, `TURN_URLS`,
`TURN_SECRET`, `TURN_RELAY_ENABLED` (default false), `TURN_TTL_S` (default 600), `RECLAIM_UNTIL`, `MAX_CONNECTIONS`,
`LOG_LEVEL`.

Relay (T18 H2): TURN credentials are issued only when `TURN_RELAY_ENABLED=true` with `TURN_URLS` and `TURN_SECRET`
set, and only in answer to a device's IceConfigRequest, never in the IceConfig pushed after authentication. The
optional coturn runs with `docker compose --profile turn up -d`; its hardened config is `turn/turnserver.conf`
(see `turn/README.md`).

Auth failure limits (T18 H1): failures before a valid signature draw from a per-IP soft bucket (burst 20, +1 per 6 s)
and are answered `RATE_LIMITED` in the protocol, not HTTP 429. Failures with a valid signature are blocked per key and
per (IP, Desk ID) for 10 minutes, so one address cannot lock out other devices. Behind nginx or any proxy set
`TRUSTED_PROXY_CIDRS`, otherwise every client shares the proxy address.
