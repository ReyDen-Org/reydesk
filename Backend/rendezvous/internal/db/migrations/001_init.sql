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
