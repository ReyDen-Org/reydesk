package auth

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"sync"
	"testing"
	"time"

	"reydesk/rendezvous/internal/db"
	"reydesk/rendezvous/internal/ids"
	"reydesk/rendezvous/internal/limits"
)

const testURL = "wss://rendezvous.example.test/v1/ws"

// ---- shared vectors ----

type vecKey struct {
	Scalar string `json:"private_scalar_hex"`
	SPKI   string `json:"spki_der_hex"`
}

type vecFile struct {
	Keys     map[string]vecKey `json:"keys"`
	AuthRows []struct {
		Label   string `json:"label"`
		Nonce   string `json:"nonce_hex"`
		URL     string `json:"public_ws_url"`
		Key     string `json:"key"`
		DeskID  string `json:"desk_id"`
		Payload string `json:"payload_hex"`
	} `json:"rendezvous_auth_payload"`
	RevokeRows []struct {
		Nonce   string `json:"nonce_hex"`
		Payload string `json:"payload_hex"`
	} `json:"revoke_self_payload"`
	SigChecks []struct {
		Group   string `json:"group"`
		Label   string `json:"label"`
		Key     string `json:"key"`
		Payload string `json:"payload_hex"`
		Sig     string `json:"signature_der_hex"`
		Valid   bool   `json:"expect_valid"`
	} `json:"signature_checks"`
}

func loadVectors(t *testing.T) vecFile {
	t.Helper()
	raw, err := os.ReadFile("../../../../Tests/Vectors/identity-v2.json")
	if err != nil {
		t.Fatal(err)
	}
	var v vecFile
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatal(err)
	}
	return v
}

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestVectorPayloadBytes(t *testing.T) {
	v := loadVectors(t)
	for _, r := range v.AuthRows {
		got := AuthPayload(mustHex(t, r.Nonce), r.URL, mustHex(t, v.Keys[r.Key].SPKI), r.DeskID)
		if !bytes.Equal(got, mustHex(t, r.Payload)) {
			t.Errorf("%s: auth payload differs", r.Label)
		}
	}
	for _, r := range v.RevokeRows {
		if !bytes.Equal(RevokePayload(mustHex(t, r.Nonce)), mustHex(t, r.Payload)) {
			t.Error("revoke payload differs")
		}
	}
}

func TestVectorSignatures(t *testing.T) {
	v := loadVectors(t)
	pubs := map[string]*ecdsa.PublicKey{}
	for name, k := range v.Keys {
		pub, fp, ok := parseSPKI(mustHex(t, k.SPKI))
		if !ok {
			t.Fatalf("vector key %s rejected by parseSPKI", name)
		}
		_ = fp
		pubs[name] = pub
	}
	n := 0
	for _, c := range v.SigChecks {
		got := verify(pubs[c.Key], mustHex(t, c.Payload), mustHex(t, c.Sig))
		if got != c.Valid {
			t.Errorf("[%s] %s: verify=%v want %v", c.Group, c.Label, got, c.Valid)
		}
		n++
	}
	if n == 0 {
		t.Fatal("no signature checks ran")
	}
}

func TestVectorFingerprint(t *testing.T) {
	v := loadVectors(t)
	spki := mustHex(t, v.Keys["host"].SPKI)
	_, fp, ok := parseSPKI(spki)
	if !ok || fp != sha256.Sum256(spki) || hex.EncodeToString(fp[:]) != "239e320f27a75db0b7997e1cfd5f91b1f402575ac69c05cfa6f7e2da9614d0d1" {
		t.Fatalf("fingerprint mismatch: %x", fp)
	}
}

// ---- fake store ----

type fakeStore struct {
	mu   sync.Mutex
	rows []*db.Device // insertion order; revoked rows stay
	err  error
}

func (s *fakeStore) DeviceByDeskID(_ context.Context, id string) (*db.Device, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.err != nil {
		return nil, s.err
	}
	for _, r := range s.rows {
		if r.DeskID == id {
			c := *r
			return &c, nil
		}
	}
	return nil, db.ErrNotFound
}

func (s *fakeStore) ActiveDeviceByKeyFP(_ context.Context, fp []byte) (*db.Device, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, r := range s.rows {
		if r.RevokedAt == nil && bytes.Equal(r.KeyFP, fp) {
			c := *r
			return &c, nil
		}
	}
	return nil, db.ErrNotFound
}

func (s *fakeStore) InsertDevice(_ context.Context, id string, fp, spki []byte, p int16) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, r := range s.rows {
		if r.DeskID == id || (r.RevokedAt == nil && bytes.Equal(r.KeyFP, fp)) {
			return false, nil
		}
	}
	s.rows = append(s.rows, &db.Device{DeskID: id, KeyFP: fp, SPKI: spki, Platform: p})
	return true, nil
}

func (s *fakeStore) TouchLastAuth(context.Context, string) error { return nil }
func (s *fakeStore) BumpStat(context.Context, string) error      { return nil }

func (s *fakeStore) Revoke(_ context.Context, id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := time.Now()
	for _, r := range s.rows {
		if r.DeskID == id && r.RevokedAt == nil {
			r.RevokedAt = &now
		}
	}
	return nil
}

// ---- harness ----

type device struct {
	priv *ecdsa.PrivateKey
	spki []byte
}

func newDevice(t *testing.T) *device {
	t.Helper()
	k, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	spki, err := x509.MarshalPKIXPublicKey(&k.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	return &device{k, spki}
}

func (d *device) sign(t *testing.T, payload []byte) []byte {
	t.Helper()
	h := sha256.Sum256(payload)
	sig, err := ecdsa.SignASN1(rand.Reader, d.priv, h[:])
	if err != nil {
		t.Fatal(err)
	}
	return sig
}

func (d *device) authReq(t *testing.T, ch *Challenge, url, deskID string) ClientAuth {
	return ClientAuth{SPKI: d.spki, DeskID: deskID, Platform: "android", AppVersion: "1.0",
		Sig: d.sign(t, AuthPayload(ch.Nonce[:], url, d.spki, deskID))}
}

type env struct {
	v     *Verifier
	store Store
	clock *time.Time
	lim   *limits.Limiter
}

func newEnv(t *testing.T, store Store, reclaim time.Time, rng *bytes.Reader) *env {
	t.Helper()
	now := time.Date(2026, 11, 5, 12, 0, 0, 0, time.UTC)
	e := &env{store: store, clock: &now}
	clock := func() time.Time { return *e.clock }
	e.lim = limits.New(clock)
	cfg := Config{Store: store, Limiter: e.lim, PublicWSURL: testURL, ReclaimUntil: reclaim, Clock: clock}
	if rng != nil {
		cfg.Rand = rng
	}
	e.v = New(cfg)
	return e
}

func (e *env) auth(t *testing.T, d *device, deskID, ip string) Result {
	t.Helper()
	ch, err := e.v.NewChallenge()
	if err != nil {
		t.Fatal(err)
	}
	res, err := e.v.Authenticate(context.Background(), ch, d.authReq(t, ch, testURL, deskID), ip)
	if err != nil {
		t.Fatal(err)
	}
	return res
}

func wantCode(t *testing.T, res Result, c Code) {
	t.Helper()
	if res.OK || res.Code != c {
		t.Fatalf("got ok=%v code=%d reason=%s want code %d", res.OK, res.Code, res.Reason, c)
	}
}

func rngFor(bits ...uint64) *bytes.Reader {
	var b []byte
	for _, v := range bits {
		for i := 6; i >= 0; i-- {
			b = append(b, byte(v>>(8*i)))
		}
	}
	return bytes.NewReader(b)
}

// ---- tests ----

func TestRegisterThenConnect(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	r1 := e.auth(t, d, "", "203.0.113.1")
	if !r1.OK || !r1.Created || !ids.IsCanonical(r1.DeskID) || r1.Session.DeskID != r1.DeskID {
		t.Fatalf("registration failed: %+v", r1)
	}
	r2 := e.auth(t, d, r1.DeskID, "203.0.113.1")
	if !r2.OK || r2.Created || r2.DeskID != r1.DeskID {
		t.Fatalf("connect failed: %+v", r2)
	}
}

func TestRegistrationIdempotentByKey(t *testing.T) {
	st := &fakeStore{}
	e := newEnv(t, st, time.Time{}, nil)
	d := newDevice(t)
	r1 := e.auth(t, d, "", "203.0.113.1")
	r2 := e.auth(t, d, "", "203.0.113.2") // lost local state: same key, empty ID
	if !r2.OK || r2.Created || r2.DeskID != r1.DeskID || len(st.rows) != 1 {
		t.Fatalf("not idempotent: %+v rows=%d", r2, len(st.rows))
	}
}

func TestKeyMismatch(t *testing.T) {
	st := &fakeStore{}
	e := newEnv(t, st, time.Time{}, nil)
	victim, attacker := newDevice(t), newDevice(t)
	id := e.auth(t, victim, "", "203.0.113.1").DeskID
	wantCode(t, e.auth(t, attacker, id, "203.0.113.9"), CodeKeyMismatch)
	if len(st.rows) != 1 || !bytes.Equal(st.rows[0].SPKI, victim.spki) {
		t.Fatal("a mismatch must not change anything")
	}
}

func TestUnknownIDWithoutReclaim(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	wantCode(t, e.auth(t, newDevice(t), ids.FromBits(12345), "203.0.113.1"), CodeUnknownID)
}

func TestReclaimOnlyInsideWindow(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Date(2026, 11, 5, 13, 0, 0, 0, time.UTC), nil)
	id := ids.FromBits(777)
	d := newDevice(t)
	r := e.auth(t, d, id, "203.0.113.1")
	if !r.OK || r.DeskID != id || !r.Created {
		t.Fatalf("reclaim inside window failed: %+v", r)
	}
	// Once bound, another key cannot take it even inside the window.
	wantCode(t, e.auth(t, newDevice(t), id, "203.0.113.2"), CodeKeyMismatch)
	// After the window an unknown ID is refused again.
	*e.clock = e.clock.Add(2 * time.Hour)
	wantCode(t, e.auth(t, newDevice(t), ids.FromBits(778), "203.0.113.3"), CodeUnknownID)
}

func TestReclaimRefusedWhenKeyAlreadyActiveElsewhere(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Date(2026, 11, 5, 13, 0, 0, 0, time.UTC), nil)
	d := newDevice(t)
	e.auth(t, d, "", "203.0.113.1")
	wantCode(t, e.auth(t, d, ids.FromBits(4242), "203.0.113.1"), CodeUnknownID)
}

func TestWrongDomainWrongURLAndTamperedFieldsRejected(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	ipN := 0
	try := func(name string, mutate func(ch *Challenge, req *ClientAuth)) {
		ipN++
		ch, _ := e.v.NewChallenge()
		req := d.authReq(t, ch, testURL, "")
		mutate(ch, &req)
		res, err := e.v.Authenticate(context.Background(), ch, req, fmt.Sprintf("203.0.113.%d", 100+ipN))
		if err != nil {
			t.Fatal(err)
		}
		if res.OK || res.Code != CodeBadSignature {
			t.Errorf("%s accepted/wrong code: %+v", name, res)
		}
	}
	try("wrong url", func(ch *Challenge, r *ClientAuth) {
		r.Sig = d.sign(t, AuthPayload(ch.Nonce[:], "wss://evil.example/v1/ws", d.spki, ""))
	})
	try("wrong domain", func(ch *Challenge, r *ClientAuth) {
		r.Sig = d.sign(t, RevokePayload(ch.Nonce[:]))
	})
	try("other nonce", func(ch *Challenge, r *ClientAuth) {
		var other [NonceSize]byte
		r.Sig = d.sign(t, AuthPayload(other[:], testURL, d.spki, ""))
	})
	try("claimed id not covered by signature", func(ch *Challenge, r *ClientAuth) {
		r.DeskID = ids.FromBits(99)
	})
	try("empty sig", func(ch *Challenge, r *ClientAuth) { r.Sig = nil })
	try("truncated sig", func(ch *Challenge, r *ClientAuth) { r.Sig = r.Sig[:len(r.Sig)-3] })
	try("oversized sig", func(ch *Challenge, r *ClientAuth) { r.Sig = bytes.Repeat([]byte{0x30}, 200) })
	try("signed by other key", func(ch *Challenge, r *ClientAuth) {
		r.Sig = newDevice(t).sign(t, AuthPayload(ch.Nonce[:], testURL, d.spki, ""))
	})
}

func TestReplayedNonceRejected(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	ch, _ := e.v.NewChallenge()
	req := d.authReq(t, ch, testURL, "")
	if res, _ := e.v.Authenticate(context.Background(), ch, req, "203.0.113.1"); !res.OK {
		t.Fatalf("first use must succeed: %+v", res)
	}
	res, _ := e.v.Authenticate(context.Background(), ch, req, "203.0.113.1")
	wantCode(t, res, CodeBadSignature)
	if res.Reason != "nonce_reused" {
		t.Fatalf("reason %q", res.Reason)
	}
}

func TestFailedAttemptConsumesNonce(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	ch, _ := e.v.NewChallenge()
	bad := d.authReq(t, ch, "wss://evil/", "")
	_, _ = e.v.Authenticate(context.Background(), ch, bad, "203.0.113.1")
	res, _ := e.v.Authenticate(context.Background(), ch, d.authReq(t, ch, testURL, ""), "203.0.113.1")
	wantCode(t, res, CodeBadSignature)
}

func TestAuthDeadline(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	ch, _ := e.v.NewChallenge()
	req := d.authReq(t, ch, testURL, "")
	*e.clock = e.clock.Add(AuthDeadline + time.Millisecond)
	res, _ := e.v.Authenticate(context.Background(), ch, req, "203.0.113.1")
	wantCode(t, res, CodeBadSignature)
	if res.Reason != "deadline" {
		t.Fatalf("reason %q", res.Reason)
	}
	// exactly at the deadline is still fine (fresh challenge)
	ch2, _ := e.v.NewChallenge()
	req2 := d.authReq(t, ch2, testURL, "")
	*e.clock = e.clock.Add(AuthDeadline)
	if res, _ := e.v.Authenticate(context.Background(), ch2, req2, "203.0.113.1"); !res.OK {
		t.Fatalf("at-deadline auth refused: %+v", res)
	}
}

func TestMalformedInputRejected(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	good := ids.FromBits(5)
	cases := map[string]func(r *ClientAuth){
		"lowercase id":    func(r *ClientAuth) { r.DeskID = "jgcexmgvaq1c" },
		"display form id": func(r *ClientAuth) { r.DeskID = ids.Display(good) },
		"bad check id":    func(r *ClientAuth) { r.DeskID = good[:11] + "Z" },
		"short spki":      func(r *ClientAuth) { r.SPKI = r.SPKI[:40] },
		"garbage spki":    func(r *ClientAuth) { r.SPKI = bytes.Repeat([]byte{7}, 91) },
		"huge spki":       func(r *ClientAuth) { r.SPKI = bytes.Repeat([]byte{7}, 4000) },
		"empty spki":      func(r *ClientAuth) { r.SPKI = nil },
		"huge platform":   func(r *ClientAuth) { r.Platform = string(bytes.Repeat([]byte{'a'}, 500)) },
		"trailing spki":   func(r *ClientAuth) { r.SPKI = append(append([]byte{}, r.SPKI...), 0) },
		"non-p256 (p384)": nil,
	}
	k384, _ := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	spki384, _ := x509.MarshalPKIXPublicKey(&k384.PublicKey)
	cases["non-p256 (p384)"] = func(r *ClientAuth) { r.SPKI = spki384 }
	ipN := 0
	for name, mut := range cases {
		ipN++
		ch, _ := e.v.NewChallenge()
		req := d.authReq(t, ch, testURL, "")
		mut(&req)
		res, err := e.v.Authenticate(context.Background(), ch, req, fmt.Sprintf("203.0.113.%d", 150+ipN))
		if err != nil || res.OK || res.Code != CodeBadSignature {
			t.Errorf("%s: %+v err=%v", name, res, err)
		}
	}
}

func TestNonP256Rejected(t *testing.T) {
	k, _ := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	spki, _ := x509.MarshalPKIXPublicKey(&k.PublicKey)
	if _, _, ok := parseSPKI(spki); ok {
		t.Fatal("P-384 accepted")
	}
}

func TestRevokeSelfNeverReissuesAndAllowsNewID(t *testing.T) {
	st := &fakeStore{}
	// First two draws collide with the revoked ID, the third is fresh.
	oldBits := uint64(0x4a0c77690daae1)
	e := newEnv(t, st, time.Time{}, rngFor(oldBits, oldBits, oldBits, 0x1bb7a0e4a443ef))
	d := newDevice(t)
	r1 := e.auth(t, d, "", "203.0.113.1")
	if r1.DeskID != ids.FromBits(oldBits) {
		t.Fatalf("unexpected first id %s", r1.DeskID)
	}
	sig := d.sign(t, RevokePayload(r1.Session.nonce[:]))
	if err := e.v.RevokeSelf(context.Background(), r1.Session, sig); err != nil {
		t.Fatal(err)
	}
	wantCode(t, e.auth(t, d, r1.DeskID, "203.0.113.1"), CodeRevoked)
	r2 := e.auth(t, d, "", "203.0.113.1")
	if !r2.OK || !r2.Created || r2.DeskID == r1.DeskID || r2.DeskID != ids.FromBits(0x1bb7a0e4a443ef) {
		t.Fatalf("re-registration after reset: %+v", r2)
	}
	if len(st.rows) != 2 {
		t.Fatalf("tombstone must stay: rows=%d", len(st.rows))
	}
}

func TestRevokeSelfRejectsBadSignatures(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	r := e.auth(t, d, "", "203.0.113.1")
	ctx := context.Background()
	other := newDevice(t)
	for name, sig := range map[string][]byte{
		"auth domain": d.sign(t, AuthPayload(r.Session.nonce[:], testURL, d.spki, "")),
		"other key":   other.sign(t, RevokePayload(r.Session.nonce[:])),
		"other nonce": d.sign(t, RevokePayload(make([]byte, NonceSize))),
		"empty":       nil,
	} {
		if err := e.v.RevokeSelf(ctx, r.Session, sig); !errors.Is(err, ErrBadSignature) {
			t.Errorf("%s accepted: %v", name, err)
		}
	}
	if err := e.v.RevokeSelf(ctx, nil, nil); !errors.Is(err, ErrBadSignature) {
		t.Error("nil session accepted")
	}
	if res := e.auth(t, d, r.DeskID, "203.0.113.1"); !res.OK {
		t.Fatalf("failed revokes must not revoke: %+v", res)
	}
}

func TestAllocationRetriesAfterCollision(t *testing.T) {
	st := &fakeStore{}
	a, b := uint64(0x4a0c77690daae1), uint64(0x1bb7a0e4a443ef)
	e := newEnv(t, st, time.Time{}, rngFor(a, a, b))
	r1 := e.auth(t, newDevice(t), "", "203.0.113.1")
	r2 := e.auth(t, newDevice(t), "", "203.0.113.2") // draws a (taken), then b
	if !r1.OK || !r2.OK || r1.DeskID == r2.DeskID || r2.DeskID != ids.FromBits(b) {
		t.Fatalf("%+v %+v", r1, r2)
	}
}

func TestAllocationExhaustionIs503(t *testing.T) {
	st := &fakeStore{}
	same := uint64(0x4a0c77690daae1)
	e := newEnv(t, st, time.Time{}, rngFor(same, same, same, same, same, same, same))
	e.auth(t, newDevice(t), "", "203.0.113.1")
	ch, _ := e.v.NewChallenge()
	d := newDevice(t)
	_, err := e.v.Authenticate(context.Background(), ch, d.authReq(t, ch, testURL, ""), "203.0.113.2")
	if !errors.Is(err, ErrUnavailable) {
		t.Fatalf("got %v", err)
	}
}

func TestStoreErrorIsInternalNotAuthFailure(t *testing.T) {
	st := &fakeStore{err: errors.New("db down")}
	e := newEnv(t, st, time.Time{}, nil)
	d := newDevice(t)
	ch, _ := e.v.NewChallenge()
	res, err := e.v.Authenticate(context.Background(), ch, d.authReq(t, ch, testURL, ids.FromBits(1)), "203.0.113.1")
	if err == nil || res.OK {
		t.Fatalf("db error must surface: %+v %v", res, err)
	}
}

func TestRegistrationRateLimit(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	for i := 0; i < limits.RegPerHour; i++ {
		if r := e.auth(t, newDevice(t), "", "198.51.100.7"); !r.OK {
			t.Fatalf("registration %d: %+v", i, r)
		}
	}
	r := e.auth(t, newDevice(t), "", "198.51.100.7")
	wantCode(t, r, CodeRateLimited)
	if r.RetryAfter <= 0 {
		t.Fatal("retry-after missing")
	}
	// an already registered device is not blocked by the registration limit
	d := newDevice(t)
	if r := e.auth(t, d, "", "198.51.100.8"); !r.OK {
		t.Fatal(r)
	}
	if r := e.auth(t, d, "", "198.51.100.7"); !r.OK || r.Created {
		t.Fatalf("idempotent re-auth must not be charged or blocked: %+v", r)
	}
}

func (e *env) badSig(t *testing.T, d *device, ip string) Result {
	t.Helper()
	ch, _ := e.v.NewChallenge()
	res, err := e.v.Authenticate(context.Background(), ch, d.authReq(t, ch, "wss://evil/", ""), ip)
	if err != nil {
		t.Fatal(err)
	}
	return res
}

// One IP (CGNAT, shared proxy) that sends a few bad frames must not lock out
// valid devices behind the same address.
func TestBadSignaturesDoNotLockOutSharedIP(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	bad := newDevice(t)
	for i := 0; i < limits.PreAuthBurst-1; i++ {
		wantCode(t, e.badSig(t, bad, "192.0.2.5"), CodeBadSignature)
	}
	if r := e.auth(t, newDevice(t), "", "192.0.2.5"); !r.OK {
		t.Fatalf("valid device behind the same IP must still work: %+v", r)
	}
}

// A flood of unsigned garbage empties the IP bucket; then it is answered
// in-protocol with RATE_LIMITED and a short retry-after, and it recovers.
func TestPreAuthFloodThrottlesIPSoftly(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	bad := newDevice(t)
	for i := 0; i < limits.PreAuthBurst; i++ {
		e.badSig(t, bad, "192.0.2.5")
	}
	r := e.auth(t, newDevice(t), "", "192.0.2.5")
	wantCode(t, r, CodeRateLimited)
	if r.RetryAfter <= 0 || r.RetryAfter > limits.PreAuthRefill {
		t.Fatalf("retry-after %v must be one refill interval at most", r.RetryAfter)
	}
	if r := e.auth(t, newDevice(t), "", "192.0.2.6"); !r.OK {
		t.Fatalf("other IP must be fine: %+v", r)
	}
	*e.clock = e.clock.Add(limits.PreAuthRefill + time.Second)
	if r := e.auth(t, newDevice(t), "", "192.0.2.5"); !r.OK {
		t.Fatalf("throttle must lift within one refill interval: %+v", r)
	}
}

// Signed failures (KEY_MISMATCH here) are charged to the key and to the
// (IP, desk id) pair, never to the whole address.
func TestSignedFailuresBlockKeyNotIP(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	owner, intruder := newDevice(t), newDevice(t)
	r := e.auth(t, owner, "", "198.51.100.1")
	if !r.OK {
		t.Fatal(r)
	}
	for i := 0; i < limits.AuthFailMax; i++ {
		wantCode(t, e.auth(t, intruder, r.DeskID, "192.0.2.5"), CodeKeyMismatch)
	}
	rr := e.auth(t, intruder, r.DeskID, "192.0.2.5")
	wantCode(t, rr, CodeRateLimited)
	if rr.RetryAfter <= 0 || rr.RetryAfter > limits.AuthFailBlock {
		t.Fatalf("retry-after %v", rr.RetryAfter)
	}
	// the same IP stays open to every other device, including the real owner
	if x := e.auth(t, newDevice(t), "", "192.0.2.5"); !x.OK {
		t.Fatalf("other device on the failing IP must work: %+v", x)
	}
	if x := e.auth(t, owner, r.DeskID, "192.0.2.5"); !x.OK {
		t.Fatalf("the owner of that desk id on the same IP must work: %+v", x)
	}
	// the blocked key is blocked from other IPs too (per key_fp)
	wantCode(t, e.auth(t, intruder, r.DeskID, "203.0.113.77"), CodeRateLimited)
	*e.clock = e.clock.Add(limits.AuthFailBlock + time.Second)
	wantCode(t, e.auth(t, intruder, r.DeskID, "192.0.2.5"), CodeKeyMismatch)
}

// Many different keys failing against one desk id from one IP are capped per
// (IP, desk id) even though each key is below its own limit.
func TestIPDeskBudget(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	owner := newDevice(t)
	r := e.auth(t, owner, "", "198.51.100.1")
	for i := 0; i < limits.AuthFailMax; i++ {
		wantCode(t, e.auth(t, newDevice(t), r.DeskID, "192.0.2.5"), CodeKeyMismatch)
	}
	wantCode(t, e.auth(t, newDevice(t), r.DeskID, "192.0.2.5"), CodeRateLimited)
	if x := e.auth(t, newDevice(t), r.DeskID, "192.0.2.9"); x.Code != CodeKeyMismatch {
		t.Fatalf("a different IP has its own budget: %+v", x)
	}
}

func TestIPv6SharesPreAuthBucketPerSlash64(t *testing.T) {
	e := newEnv(t, &fakeStore{}, time.Time{}, nil)
	d := newDevice(t)
	for i := 0; i < limits.PreAuthBurst; i++ {
		e.badSig(t, d, "2001:db8:1:2::1")
	}
	wantCode(t, e.auth(t, d, "", "2001:db8:1:2:ffff::9"), CodeRateLimited)
}

func TestAuthPayloadDoesNotAliasFields(t *testing.T) {
	// LP framing: moving bytes between adjacent fields must change the payload.
	n := make([]byte, NonceSize)
	a := AuthPayload(n, "wss://a/", []byte{1, 2}, "X")
	b := AuthPayload(n, "wss://a/\x00\x00\x00\x02", []byte{1}, "X")
	if bytes.Equal(a, b) {
		t.Fatal("payload ambiguity")
	}
}

func TestParseReclaimUntil(t *testing.T) {
	if z, err := ParseReclaimUntil(""); err != nil || !z.IsZero() {
		t.Fatal("empty must be disabled")
	}
	if ts, err := ParseReclaimUntil("2026-11-06T00:00:00Z"); err != nil || ts.IsZero() {
		t.Fatal(err)
	}
	if _, err := ParseReclaimUntil("tomorrow"); err == nil {
		t.Fatal("garbage accepted")
	}
}
