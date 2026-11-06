package db

import (
	"context"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// Device is one row of the devices table.
type Device struct {
	DeskID     string
	KeyFP      []byte
	SPKI       []byte
	Platform   int16
	LastAuthAt time.Time
	RevokedAt  *time.Time
}

// ErrNotFound is returned when no row matches.
var ErrNotFound = errors.New("db: not found")

// AuthQueries implements the device queries the auth package needs.
type AuthQueries struct{ Pool *pgxpool.Pool }

const deviceCols = `desk_id, key_fp, spki, platform, last_auth_at, revoked_at`

func scanDevice(row pgx.Row) (*Device, error) {
	var d Device
	if err := row.Scan(&d.DeskID, &d.KeyFP, &d.SPKI, &d.Platform, &d.LastAuthAt, &d.RevokedAt); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return nil, ErrNotFound
		}
		return nil, err
	}
	return &d, nil
}

// DeviceByDeskID returns the row (revoked or not) for a canonical Desk ID.
func (q AuthQueries) DeviceByDeskID(ctx context.Context, deskID string) (*Device, error) {
	return scanDevice(q.Pool.QueryRow(ctx, `SELECT `+deviceCols+` FROM devices WHERE desk_id = $1`, deskID))
}

// ActiveDeviceByKeyFP returns the non-revoked row bound to a key fingerprint.
func (q AuthQueries) ActiveDeviceByKeyFP(ctx context.Context, fp []byte) (*Device, error) {
	return scanDevice(q.Pool.QueryRow(ctx, `SELECT `+deviceCols+` FROM devices WHERE key_fp = $1 AND revoked_at IS NULL`, fp))
}

// InsertDevice inserts a new row. inserted is false when the Desk ID or an
// active key fingerprint already exists (ON CONFLICT DO NOTHING).
func (q AuthQueries) InsertDevice(ctx context.Context, deskID string, fp, spki []byte, platform int16) (bool, error) {
	var got string
	err := q.Pool.QueryRow(ctx,
		`INSERT INTO devices (desk_id, key_fp, spki, platform) VALUES ($1, $2, $3, $4)
		 ON CONFLICT DO NOTHING RETURNING desk_id`, deskID, fp, spki, platform).Scan(&got)
	if errors.Is(err, pgx.ErrNoRows) {
		return false, nil
	}
	return err == nil, err
}

// TouchLastAuth updates last_auth_at at most once per hour per device.
func (q AuthQueries) TouchLastAuth(ctx context.Context, deskID string) error {
	_, err := q.Pool.Exec(ctx,
		`UPDATE devices SET last_auth_at = now() WHERE desk_id = $1 AND last_auth_at < now() - interval '1 hour'`, deskID)
	return err
}

// Revoke tombstones a device. The row is never deleted.
func (q AuthQueries) Revoke(ctx context.Context, deskID string) error {
	_, err := q.Pool.Exec(ctx, `UPDATE devices SET revoked_at = now() WHERE desk_id = $1 AND revoked_at IS NULL`, deskID)
	return err
}

// RevokeStale tombstones active devices with no auth for maxAge (spec: 365 days).
func (q AuthQueries) RevokeStale(ctx context.Context, maxAge time.Duration) (int64, error) {
	tag, err := q.Pool.Exec(ctx,
		`UPDATE devices SET revoked_at = now() WHERE revoked_at IS NULL AND last_auth_at < now() - make_interval(secs => $1)`,
		maxAge.Seconds())
	return tag.RowsAffected(), err
}

// BumpStat increments one daily_stats counter ("registrations" or "auths").
func (q AuthQueries) BumpStat(ctx context.Context, column string) error {
	var sql string
	switch column {
	case "registrations":
		sql = `INSERT INTO daily_stats (day, registrations) VALUES (current_date, 1) ON CONFLICT (day) DO UPDATE SET registrations = daily_stats.registrations + 1`
	case "auths":
		sql = `INSERT INTO daily_stats (day, auths) VALUES (current_date, 1) ON CONFLICT (day) DO UPDATE SET auths = daily_stats.auths + 1`
	default:
		return errors.New("db: unknown stat column")
	}
	_, err := q.Pool.Exec(ctx, sql)
	return err
}
