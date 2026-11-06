package auth

import (
	"context"
	"os"
	"sync"
	"testing"
	"time"

	"reydesk/rendezvous/internal/db"
	"reydesk/rendezvous/internal/ids"
)

// These tests need a real PostgreSQL: set TEST_DATABASE_URL to a throwaway
// database (the tables are wiped). They are skipped when it is unset.
func dbEnv(t *testing.T) (*env, db.AuthQueries) {
	t.Helper()
	url := os.Getenv("TEST_DATABASE_URL")
	if url == "" {
		t.Skip("TEST_DATABASE_URL not set")
	}
	ctx := context.Background()
	pool, err := db.Connect(ctx, url)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(pool.Close)
	if err := db.Migrate(ctx, pool); err != nil {
		t.Fatal(err)
	}
	if _, err := pool.Exec(ctx, "TRUNCATE devices, daily_stats"); err != nil {
		t.Fatal(err)
	}
	q := db.AuthQueries{Pool: pool}
	return newEnv(t, q, time.Time{}, nil), q
}

func TestDBRegisterIdempotentMismatchRevoke(t *testing.T) {
	e, q := dbEnv(t)
	ctx := context.Background()
	d, other := newDevice(t), newDevice(t)

	r1 := e.auth(t, d, "", "203.0.113.1")
	if !r1.OK || !r1.Created || !ids.IsCanonical(r1.DeskID) {
		t.Fatalf("register: %+v", r1)
	}
	if r := e.auth(t, d, "", "203.0.113.1"); !r.OK || r.Created || r.DeskID != r1.DeskID {
		t.Fatalf("idempotence by key: %+v", r)
	}
	if r := e.auth(t, d, r1.DeskID, "203.0.113.1"); !r.OK {
		t.Fatalf("connect: %+v", r)
	}
	wantCode(t, e.auth(t, other, r1.DeskID, "203.0.113.2"), CodeKeyMismatch)
	wantCode(t, e.auth(t, other, ids.FromBits(31337), "203.0.113.3"), CodeUnknownID)

	if err := e.v.RevokeSelf(ctx, r1.Session, d.sign(t, RevokePayload(r1.Session.nonce[:]))); err != nil {
		t.Fatal(err)
	}
	wantCode(t, e.auth(t, d, r1.DeskID, "203.0.113.4"), CodeRevoked)
	r2 := e.auth(t, d, "", "203.0.113.5") // same key registers again after a reset
	if !r2.OK || !r2.Created || r2.DeskID == r1.DeskID {
		t.Fatalf("re-register after revoke: %+v", r2)
	}
	row, err := q.DeviceByDeskID(ctx, r1.DeskID)
	if err != nil || row.RevokedAt == nil {
		t.Fatalf("tombstone missing: %v %+v", err, row)
	}
	var n int
	if err := q.Pool.QueryRow(ctx, "SELECT count(*) FROM devices").Scan(&n); err != nil || n != 2 {
		t.Fatalf("rows=%d err=%v", n, err)
	}
}

func TestDBConcurrentRegistrationOfOneKeyYieldsOneID(t *testing.T) {
	e, q := dbEnv(t)
	d := newDevice(t)
	var wg sync.WaitGroup
	got := make([]string, 12)
	for i := range got {
		wg.Add(1)
		go func() {
			defer wg.Done()
			ch, _ := e.v.NewChallenge()
			res, err := e.v.Authenticate(context.Background(), ch, d.authReq(t, ch, testURL, ""), "203.0.113.9")
			if err != nil || !res.OK {
				t.Errorf("auth %d: %+v %v", i, res, err)
				return
			}
			got[i] = res.DeskID
		}()
	}
	wg.Wait()
	for _, g := range got {
		if g != got[0] {
			t.Fatalf("different IDs for one key: %v", got)
		}
	}
	var n int
	if err := q.Pool.QueryRow(context.Background(), "SELECT count(*) FROM devices").Scan(&n); err != nil || n != 1 {
		t.Fatalf("rows=%d err=%v", n, err)
	}
}

func TestDBReclaimAndStaleSweep(t *testing.T) {
	e, q := dbEnv(t)
	ctx := context.Background()
	e.v = New(Config{Store: q, Limiter: e.lim, PublicWSURL: testURL, ReclaimUntil: time.Now().Add(time.Hour), Clock: time.Now})
	d := newDevice(t)
	id := ids.FromBits(2024)
	if r := e.auth(t, d, id, "203.0.113.1"); !r.OK || r.DeskID != id {
		t.Fatalf("reclaim: %+v", r)
	}
	if _, err := q.Pool.Exec(ctx, "UPDATE devices SET last_auth_at = now() - interval '400 days'"); err != nil {
		t.Fatal(err)
	}
	n, err := q.RevokeStale(ctx, StaleAfter)
	if err != nil || n != 1 {
		t.Fatalf("stale sweep n=%d err=%v", n, err)
	}
	wantCode(t, e.auth(t, d, id, "203.0.113.1"), CodeRevoked)
}

func TestDBLastAuthTouchedAtMostHourly(t *testing.T) {
	e, q := dbEnv(t)
	ctx := context.Background()
	d := newDevice(t)
	r := e.auth(t, d, "", "203.0.113.1")
	var before time.Time
	q.Pool.QueryRow(ctx, "SELECT last_auth_at FROM devices WHERE desk_id=$1", r.DeskID).Scan(&before)
	e.auth(t, d, r.DeskID, "203.0.113.1")
	var after time.Time
	q.Pool.QueryRow(ctx, "SELECT last_auth_at FROM devices WHERE desk_id=$1", r.DeskID).Scan(&after)
	if !before.Equal(after) {
		t.Fatal("last_auth_at touched within the hour")
	}
	q.Pool.Exec(ctx, "UPDATE devices SET last_auth_at = now() - interval '2 hours'")
	e.auth(t, d, r.DeskID, "203.0.113.1")
	q.Pool.QueryRow(ctx, "SELECT last_auth_at FROM devices WHERE desk_id=$1", r.DeskID).Scan(&after)
	if time.Since(after) > time.Minute {
		t.Fatal("last_auth_at not refreshed after an hour")
	}
}
