package logx

import (
	"bytes"
	"log/slog"
	"strings"
	"testing"
	"time"
)

func TestNeverLogsSensitiveValues(t *testing.T) {
	var buf bytes.Buffer
	g := New(slog.New(slog.NewJSONHandler(&buf, &slog.HandlerOptions{Level: slog.LevelDebug})), nil)
	const (
		fullID = "ABCDEFGHJKMN"
		fullIP = "203.0.113.77"
		secret = "123456 SECRET sdp v=0 candidate:1"
	)
	g.Info("auth", Fields{Result: "ok", DeskID: fullID, IP: fullIP})
	// free text passed by mistake in tag positions and as a Desk ID must be dropped
	g.Warn(secret, Fields{Result: secret, Reason: secret, DeskID: secret, IP: fullIP})
	out := buf.String()
	for _, bad := range []string{fullID, fullIP, "123456", "SECRET", "sdp", "candidate", "EFGHJKMN"} {
		if strings.Contains(out, bad) {
			t.Fatalf("log leaks %q: %s", bad, out)
		}
	}
	if !strings.Contains(out, `"desk_id":"ABCD"`) || !strings.Contains(out, `"ip_hash":"`) {
		t.Fatalf("missing fields: %s", out)
	}
	if !strings.Contains(out, `"desk_id":"????"`) || !strings.Contains(out, `"event":"invalid"`) {
		t.Fatalf("free text not replaced: %s", out)
	}
}

func TestIPHashStableWithinDayRotatesNextDay(t *testing.T) {
	now := time.Date(2026, 11, 5, 10, 0, 0, 0, time.UTC)
	h := NewIPHasher(func() time.Time { return now })
	a, b := h.Hash("203.0.113.7"), h.Hash("203.0.113.7")
	if a != b || len(a) != 16 {
		t.Fatalf("unstable or wrong length: %q %q", a, b)
	}
	if h.Hash("203.0.113.8") == a {
		t.Fatal("different IPs collided")
	}
	if h.Hash("2001:db8::1") != h.Hash("2001:db8::ffff") {
		t.Fatal("same /64 must hash alike")
	}
	now = now.Add(24 * time.Hour)
	if h.Hash("203.0.113.7") == a {
		t.Fatal("key must rotate daily")
	}
}
