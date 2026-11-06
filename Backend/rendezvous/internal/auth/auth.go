// Package auth implements rendezvous authentication (spec 1.4, 3.2): a server
// nonce signed with the device's P-256 key, Desk ID registration bound to that
// key, and RevokeSelf. The Desk ID a client claims is never trusted: it is only
// accepted when the signature over it verifies under the key stored for it.
//
// Typical use per WebSocket connection (T5):
//
//	ch, _ := v.NewChallenge()                  // put ch.Nonce in ServerHello
//	res, err := v.Authenticate(ctx, ch, clientAuth, ip)
//	// err != nil: internal failure (DB down, ErrUnavailable): close, 503-ish.
//	// !res.OK: send AuthResult{error: res.Code.Proto()} and close.
//	// res.OK: res.Session identifies the device; res.DeskID is the canonical ID.
//	err = v.RevokeSelf(ctx, res.Session, sig)  // on a RevokeSelf frame
package auth

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/x509"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"io"
	"strings"
	"sync/atomic"
	"time"

	"reydesk/rendezvous/internal/db"
	"reydesk/rendezvous/internal/ids"
	"reydesk/rendezvous/internal/limits"

	rendezvousv1 "reydesk/rendezvous/gen"
)

const (
	AuthDomain   = "ReyDesk/rendezvous/auth/v1"
	RevokeDomain = "ReyDesk/rendezvous/revoke/v1"

	NonceSize    = 32
	AuthDeadline = 10 * time.Second
	MinSPKI      = 59 // matches the devices.spki CHECK
	MaxSPKI      = 128
	MaxSigDER    = 80
	maxMetaLen   = 64

	// StaleAfter: devices with no auth for this long are tombstoned (RevokeStale).
	StaleAfter = 365 * 24 * time.Hour
)

var (
	// ErrUnavailable means Desk ID allocation failed 5 times (spec: answer 503).
	ErrUnavailable = errors.New("auth: desk id allocation unavailable")
	// ErrBadSignature is returned by RevokeSelf for a signature that does not verify.
	ErrBadSignature = errors.New("auth: bad signature")
)

// Code is the authentication outcome; values map 1:1 to rendezvousv1.AuthError.
type Code int

const (
	CodeOK Code = iota
	CodeKeyMismatch
	CodeUnknownID
	CodeRevoked
	CodeRateLimited
	CodeBadSignature
)

// Proto returns the wire enum for AuthResult.error.
func (c Code) Proto() rendezvousv1.AuthError {
	switch c {
	case CodeOK:
		return rendezvousv1.AuthError_AUTH_ERROR_NONE
	case CodeKeyMismatch:
		return rendezvousv1.AuthError_AUTH_ERROR_KEY_MISMATCH
	case CodeUnknownID:
		return rendezvousv1.AuthError_AUTH_ERROR_UNKNOWN_ID
	case CodeRevoked:
		return rendezvousv1.AuthError_AUTH_ERROR_REVOKED
	case CodeRateLimited:
		return rendezvousv1.AuthError_AUTH_ERROR_RATE_LIMITED
	default:
		return rendezvousv1.AuthError_AUTH_ERROR_BAD_SIGNATURE
	}
}

// Store is the device persistence the verifier needs; db.AuthQueries implements it.
type Store interface {
	DeviceByDeskID(ctx context.Context, deskID string) (*db.Device, error)
	ActiveDeviceByKeyFP(ctx context.Context, fp []byte) (*db.Device, error)
	InsertDevice(ctx context.Context, deskID string, fp, spki []byte, platform int16) (bool, error)
	TouchLastAuth(ctx context.Context, deskID string) error
	Revoke(ctx context.Context, deskID string) error
	BumpStat(ctx context.Context, column string) error
}

// Config configures a Verifier.
type Config struct {
	Store        Store
	Limiter      *limits.Limiter // required
	PublicWSURL  string          // exact PUBLIC_WS_URL, bound into signatures
	ReclaimUntil time.Time       // zero = reclaim disabled (RECLAIM_UNTIL)
	Clock        func() time.Time
	Rand         io.Reader // crypto/rand when nil; tests inject a colliding RNG
}

// Verifier authenticates devices. Safe for concurrent use.
type Verifier struct{ c Config }

func New(c Config) *Verifier {
	if c.Clock == nil {
		c.Clock = time.Now
	}
	if c.Rand == nil {
		c.Rand = rand.Reader
	}
	return &Verifier{c: c}
}

// ParseReclaimUntil parses the RECLAIM_UNTIL env value (RFC 3339); empty = disabled.
func ParseReclaimUntil(s string) (time.Time, error) {
	if strings.TrimSpace(s) == "" {
		return time.Time{}, nil
	}
	return time.Parse(time.RFC3339, strings.TrimSpace(s))
}

// Challenge is the per-connection server nonce. It is consumed by the first
// Authenticate call, successful or not, and expires after AuthDeadline.
type Challenge struct {
	Nonce    [NonceSize]byte
	IssuedAt time.Time
	used     atomic.Bool
}

func (v *Verifier) NewChallenge() (*Challenge, error) {
	ch := &Challenge{IssuedAt: v.c.Clock()}
	if _, err := io.ReadFull(rand.Reader, ch.Nonce[:]); err != nil {
		return nil, err
	}
	return ch, nil
}

// ClientAuth is the decoded ClientAuth frame.
type ClientAuth struct {
	SPKI       []byte
	DeskID     string // canonical, or empty on first registration
	Platform   string
	AppVersion string
	Sig        []byte // DER ECDSA
}

// Session is an authenticated connection's identity.
type Session struct {
	DeskID string
	KeyFP  [32]byte
	nonce  [NonceSize]byte
	pub    *ecdsa.PublicKey
}

// Result is the outcome of Authenticate. Reason is a short log-safe tag
// (never contains keys, IDs or signatures); it must not be sent to clients.
type Result struct {
	OK         bool
	Code       Code
	DeskID     string
	Created    bool // a new Desk ID was allocated (or reclaimed) by this call
	RetryAfter time.Duration
	Reason     string
	Session    *Session

	// Set by authenticate for the failure accounting in Authenticate.
	verified bool   // the signature verified under the presented key
	keyFPHex string // hex key fingerprint (verified only)
	deskReq  string // Desk ID the client claimed
}

func fail(c Code, reason string) Result { return Result{Code: c, Reason: reason} }

func lp(buf *bytes.Buffer, b []byte) {
	var n [4]byte
	binary.BigEndian.PutUint32(n[:], uint32(len(b)))
	buf.Write(n[:])
	buf.Write(b)
}

// AuthPayload is LP(domain) LP(nonce) LP(public_ws_url) LP(spki) LP(desk_id).
func AuthPayload(nonce []byte, publicWSURL string, spki []byte, deskID string) []byte {
	var b bytes.Buffer
	lp(&b, []byte(AuthDomain))
	lp(&b, nonce)
	lp(&b, []byte(publicWSURL))
	lp(&b, spki)
	lp(&b, []byte(deskID))
	return b.Bytes()
}

// RevokePayload is LP(domain) LP(nonce).
func RevokePayload(nonce []byte) []byte {
	var b bytes.Buffer
	lp(&b, []byte(RevokeDomain))
	lp(&b, nonce)
	return b.Bytes()
}

// parseSPKI accepts only a canonical DER P-256 SubjectPublicKeyInfo.
func parseSPKI(spki []byte) (*ecdsa.PublicKey, [32]byte, bool) {
	var fp [32]byte
	if len(spki) < MinSPKI || len(spki) > MaxSPKI {
		return nil, fp, false
	}
	k, err := x509.ParsePKIXPublicKey(spki)
	if err != nil {
		return nil, fp, false
	}
	pub, ok := k.(*ecdsa.PublicKey)
	if !ok || pub.Curve != elliptic.P256() {
		return nil, fp, false
	}
	if again, err := x509.MarshalPKIXPublicKey(pub); err != nil || !bytes.Equal(again, spki) {
		return nil, fp, false // reject non-canonical encodings so the fingerprint is unique per key
	}
	return pub, sha256.Sum256(spki), true
}

func verify(pub *ecdsa.PublicKey, payload, sig []byte) bool {
	if len(sig) == 0 || len(sig) > MaxSigDER {
		return false
	}
	h := sha256.Sum256(payload)
	return ecdsa.VerifyASN1(pub, h[:], sig)
}

func platformCode(p string) int16 {
	switch strings.ToLower(p) {
	case "windows":
		return 1
	case "android":
		return 2
	}
	return 0
}

// Authenticate runs the full check for one ClientAuth frame. ip is the client
// address (already derived via TRUSTED_PROXY_CIDRS); it is normalised here.
func (v *Verifier) Authenticate(ctx context.Context, ch *Challenge, req ClientAuth, ip string) (Result, error) {
	ipKey := limits.NormalizeIP(ip)
	if blocked, d := v.c.Limiter.PreAuthBlocked(ipKey); blocked {
		ch.used.Store(true)
		return Result{Code: CodeRateLimited, RetryAfter: d, Reason: "ip_throttled"}, nil
	}
	res, err := v.authenticate(ctx, ch, req, ipKey)
	if err == nil && !res.OK && res.Code != CodeRateLimited {
		if res.verified {
			// A valid signature proves who is failing: charge that key, and
			// that key from this address, never the whole address.
			v.c.Limiter.RecordKeyFailure(KeyBucket(res.keyFPHex))
			if res.deskReq != "" {
				v.c.Limiter.RecordKeyFailure(IPDeskBucket(ipKey, res.deskReq))
			}
		} else {
			v.c.Limiter.RecordPreAuthFailure(ipKey)
		}
	}
	return res, err
}

// KeyBucket and IPDeskBucket name the limiter keys of signed failures.
func KeyBucket(fpHex string) string         { return "fp:" + fpHex }
func IPDeskBucket(ip, deskID string) string { return "ipdesk:" + ip + "|" + deskID }

func (v *Verifier) authenticate(ctx context.Context, ch *Challenge, req ClientAuth, ipKey string) (Result, error) {
	if ch.used.Swap(true) {
		return fail(CodeBadSignature, "nonce_reused"), nil
	}
	now := v.c.Clock()
	if now.Sub(ch.IssuedAt) > AuthDeadline || now.Before(ch.IssuedAt) {
		return fail(CodeBadSignature, "deadline"), nil
	}
	if len(req.Platform) > maxMetaLen || len(req.AppVersion) > maxMetaLen {
		return fail(CodeBadSignature, "malformed_meta"), nil
	}
	if req.DeskID != "" && !ids.IsCanonical(req.DeskID) {
		return fail(CodeBadSignature, "malformed_desk_id"), nil
	}
	pub, fp, ok := parseSPKI(req.SPKI)
	if !ok {
		return fail(CodeBadSignature, "malformed_spki"), nil
	}
	payload := AuthPayload(ch.Nonce[:], v.c.PublicWSURL, req.SPKI, req.DeskID)
	if !verify(pub, payload, req.Sig) {
		return fail(CodeBadSignature, "bad_signature"), nil
	}
	sess := &Session{KeyFP: fp, nonce: ch.Nonce, pub: pub}
	platform := platformCode(req.Platform)

	fpHex := hex.EncodeToString(fp[:])
	if blocked, d := v.c.Limiter.KeyBlocked(KeyBucket(fpHex)); blocked {
		return Result{Code: CodeRateLimited, RetryAfter: d, Reason: "key_blocked"}, nil
	}
	var res Result
	var err error
	if req.DeskID == "" {
		res, err = v.register(ctx, ipKey, sess, req.SPKI, platform)
	} else {
		res, err = v.connect(ctx, now, ipKey, sess, req.DeskID, req.SPKI, platform)
	}
	res.verified, res.keyFPHex, res.deskReq = true, fpHex, req.DeskID
	return res, err
}

func (v *Verifier) ok(sess *Session, deskID string, created bool) Result {
	sess.DeskID = deskID
	return Result{OK: true, DeskID: deskID, Created: created, Session: sess}
}

// register handles a first connect: idempotent by key, otherwise a new random ID.
func (v *Verifier) register(ctx context.Context, ipKey string, sess *Session, spki []byte, platform int16) (Result, error) {
	fp := sess.KeyFP[:]
	if d, err := v.c.Store.ActiveDeviceByKeyFP(ctx, fp); err == nil {
		_ = v.c.Store.TouchLastAuth(ctx, d.DeskID)
		return v.ok(sess, d.DeskID, false), nil
	} else if !errors.Is(err, db.ErrNotFound) {
		return Result{}, err
	}
	if allowed, retry := v.c.Limiter.AllowRegistration(ipKey); !allowed {
		return Result{Code: CodeRateLimited, RetryAfter: retry, Reason: "registration_limit"}, nil
	}
	var existing *db.Device
	id, err := ids.Allocate(ctx, v.c.Rand, func(ctx context.Context, id string) (bool, error) {
		inserted, err := v.c.Store.InsertDevice(ctx, id, fp, spki, platform)
		if err != nil || inserted {
			return inserted, err
		}
		// Conflict: either the ID is taken or this key got registered concurrently.
		d, err := v.c.Store.ActiveDeviceByKeyFP(ctx, fp)
		if err == nil {
			existing = d
			return true, nil
		}
		if errors.Is(err, db.ErrNotFound) {
			return false, nil
		}
		return false, err
	})
	if err != nil {
		if errors.Is(err, ids.ErrExhausted) {
			return Result{}, ErrUnavailable
		}
		return Result{}, err
	}
	if existing != nil {
		return v.ok(sess, existing.DeskID, false), nil
	}
	_ = v.c.Store.BumpStat(ctx, "registrations")
	return v.ok(sess, id, true), nil
}

// connect handles a stored Desk ID: the row must exist, be active, and carry
// exactly the presented key. There is no rebinding to a different key.
// signedFail is a failed connect with a valid signature. Once the (IP, desk id)
// budget is spent, further failures from that address against that desk id are
// answered RATE_LIMITED. The desk's real key never reaches this path, so its
// owner is not locked out by someone else's attempts.
func (v *Verifier) signedFail(ipKey, deskID string, c Code, reason string) Result {
	if blocked, d := v.c.Limiter.KeyBlocked(IPDeskBucket(ipKey, deskID)); blocked {
		return Result{Code: CodeRateLimited, RetryAfter: d, Reason: "ip_desk_blocked"}
	}
	return fail(c, reason)
}

func (v *Verifier) connect(ctx context.Context, now time.Time, ipKey string, sess *Session, deskID string, spki []byte, platform int16) (Result, error) {
	d, err := v.c.Store.DeviceByDeskID(ctx, deskID)
	if errors.Is(err, db.ErrNotFound) {
		if !v.c.ReclaimUntil.IsZero() && now.Before(v.c.ReclaimUntil) {
			inserted, ierr := v.c.Store.InsertDevice(ctx, deskID, sess.KeyFP[:], spki, platform)
			if ierr != nil {
				return Result{}, ierr
			}
			if inserted {
				return v.ok(sess, deskID, true), nil
			}
		}
		return v.signedFail(ipKey, deskID, CodeUnknownID, "unknown_id"), nil
	}
	if err != nil {
		return Result{}, err
	}
	if d.RevokedAt != nil {
		return v.signedFail(ipKey, deskID, CodeRevoked, "revoked"), nil
	}
	if subtle.ConstantTimeCompare(d.KeyFP, sess.KeyFP[:]) != 1 {
		return v.signedFail(ipKey, deskID, CodeKeyMismatch, "key_mismatch"), nil
	}
	_ = v.c.Store.TouchLastAuth(ctx, deskID)
	_ = v.c.Store.BumpStat(ctx, "auths")
	return v.ok(sess, deskID, false), nil
}

// RevokeSelf verifies sig over RevokePayload(session nonce) with the session's
// key and tombstones the session's Desk ID. The row is kept forever, so the ID
// is never reissued; the client then registers again for a new one.
func (v *Verifier) RevokeSelf(ctx context.Context, s *Session, sig []byte) error {
	if s == nil || s.DeskID == "" || !verify(s.pub, RevokePayload(s.nonce[:]), sig) {
		return ErrBadSignature
	}
	return v.c.Store.Revoke(ctx, s.DeskID)
}
