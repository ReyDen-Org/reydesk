// Package logx is the only way the rendezvous server writes events (spec 3.5).
// The API is closed on purpose: an event is a name, a result tag, a reason tag,
// the first 4 chars of a Desk ID and a keyed hash of the client IP. There is no
// way to attach a payload, code, key, SDP/ICE text or a full ID or IP, and tag
// strings that are not short [a-z0-9_] words are replaced, so a mistaken
// argument cannot leak data into the log.
package logx

import (
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"log/slog"
	"strings"
	"sync"
	"time"

	"reydesk/rendezvous/internal/limits"
)

// Fields are the only values an event may carry.
type Fields struct {
	Result string // outcome tag, e.g. "ok", "bad_signature"
	Reason string // short internal tag, e.g. "key_mismatch"
	DeskID string // any length; only the first 4 chars are logged
	IP     string // any form; only HMAC-SHA256(daily key, normalised IP)[:8] is logged
}

// Logger writes JSON-line style events through slog.
type Logger struct {
	l *slog.Logger
	h *IPHasher
}

// New returns a Logger. hasher may be nil (a fresh daily hasher is created).
func New(l *slog.Logger, hasher *IPHasher) *Logger {
	if l == nil {
		l = slog.Default()
	}
	if hasher == nil {
		hasher = NewIPHasher(nil)
	}
	return &Logger{l: l, h: hasher}
}

func (g *Logger) Debug(event string, f Fields) { g.log(slog.LevelDebug, event, f) }
func (g *Logger) Info(event string, f Fields)  { g.log(slog.LevelInfo, event, f) }
func (g *Logger) Warn(event string, f Fields)  { g.log(slog.LevelWarn, event, f) }

func (g *Logger) log(lv slog.Level, event string, f Fields) {
	if g == nil {
		return
	}
	ctx := context.Background()
	if !g.l.Enabled(ctx, lv) {
		return
	}
	attrs := []slog.Attr{slog.String("event", Tag(event))}
	if f.Result != "" {
		attrs = append(attrs, slog.String("result", Tag(f.Result)))
	}
	if f.Reason != "" {
		attrs = append(attrs, slog.String("reason", Tag(f.Reason)))
	}
	if f.DeskID != "" {
		attrs = append(attrs, slog.String("desk_id", DeskPrefix(f.DeskID)))
	}
	if f.IP != "" {
		attrs = append(attrs, slog.String("ip_hash", g.h.Hash(f.IP)))
	}
	g.l.LogAttrs(ctx, lv, "", attrs...)
}

// Tag returns s when it is a short lowercase word ([a-z0-9_], <= 40 chars) and
// "invalid" otherwise.
func Tag(s string) string {
	if s == "" || len(s) > 40 {
		return "invalid"
	}
	for i := 0; i < len(s); i++ {
		c := s[i]
		if !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_') {
			return "invalid"
		}
	}
	return s
}

const crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

// DeskPrefix is the first 4 chars of a Desk ID, or "????" when the input is not
// a 12 character Crockford string (so free text can never pass through).
func DeskPrefix(id string) string {
	if len(id) != 12 {
		return "????"
	}
	p := strings.ToUpper(id)
	for i := 0; i < 12; i++ {
		if !strings.ContainsRune(crockford, rune(p[i])) {
			return "????"
		}
	}
	return p[:4]
}

// IPHasher hashes IPs with a random key that lives only in memory and is
// replaced at every UTC day change, so hashes cannot be linked across days.
type IPHasher struct {
	mu    sync.Mutex
	clock func() time.Time
	day   int64
	key   [32]byte
}

func NewIPHasher(clock func() time.Time) *IPHasher {
	if clock == nil {
		clock = time.Now
	}
	return &IPHasher{clock: clock, day: -1}
}

// Hash returns the first 8 bytes (16 hex chars) of HMAC-SHA256(key, normalised ip).
func (h *IPHasher) Hash(ip string) string {
	h.mu.Lock()
	day := h.clock().UTC().Unix() / 86400
	if day != h.day {
		if _, err := rand.Read(h.key[:]); err != nil {
			panic("logx: no randomness: " + err.Error())
		}
		h.day = day
	}
	key := h.key
	h.mu.Unlock()
	m := hmac.New(sha256.New, key[:])
	m.Write([]byte(limits.NormalizeIP(ip)))
	return hex.EncodeToString(m.Sum(nil)[:8])
}
