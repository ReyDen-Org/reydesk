package ice

import (
	"crypto/hmac"
	"crypto/sha1"
	"encoding/base64"
	"regexp"
	"testing"
	"time"
)

var now = time.Unix(1_800_000_000, 0)
var fp = []byte{0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff}

func TestNoRelayWhenTURNNotConfigured(t *testing.T) {
	for _, c := range []Config{
		{STUNURLs: []string{"stun:h:3478"}},
		{TURNURLs: []string{"turn:h:3478"}},              // no secret
		{Secret: "s", STUNURLs: []string{"stun:h"}},      // no urls
		{TURNURLs: []string{"turn:h:3478"}, Secret: "s"}, // configured but not switched on
	} {
		c := c
		if c.RelayEnabled() {
			t.Fatalf("relay must be off: %+v", c)
		}
		for _, s := range c.ForDevice(fp, now, true).Servers {
			if s.Username != "" || s.Credential != "" {
				t.Fatalf("credential issued with relay off: %+v", s)
			}
		}
	}
}

func TestEmptyConfigIsEmptyList(t *testing.T) {
	r := (&Config{}).ForDevice(fp, now, true)
	if len(r.Servers) != 0 {
		t.Fatal("LAN medium expects an empty list")
	}
}

// coturn REST: username = "<unix expiry>:<id>", credential = base64(HMAC-SHA1(secret, username)).
func TestTURNCredentialFormat(t *testing.T) {
	c := Config{
		STUNURLs: []string{"stun:h:3478"},
		TURNURLs: []string{"turn:h:3478?transport=udp", "turn:h:3478?transport=tcp", "turns:h:443?transport=tcp"},
		Secret:   "static-auth-secret", TTL: time.Hour, Enabled: true,
	}
	r := c.ForDevice(fp, now, true)
	if !r.ExpiresAt.Equal(now.Add(time.Hour)) || len(r.Servers) != 2 {
		t.Fatalf("bad result %+v", r)
	}
	turn := r.Servers[1]
	if !regexp.MustCompile(`^1800003600:0011223344556677$`).MatchString(turn.Username) {
		t.Fatalf("username %q", turn.Username)
	}
	m := hmac.New(sha1.New, []byte("static-auth-secret"))
	m.Write([]byte(turn.Username))
	want := base64.StdEncoding.EncodeToString(m.Sum(nil))
	if turn.Credential != want {
		t.Fatal("credential mismatch")
	}
	if len(turn.URLs) != 3 || len(r.Servers[0].URLs) != 1 || r.Servers[0].Username != "" {
		t.Fatalf("urls %+v", r.Servers)
	}
}

func TestNoCredentialUnlessDeviceAsked(t *testing.T) {
	c := Config{TURNURLs: []string{"turn:h:3478"}, Secret: "s", Enabled: true}
	if !c.RelayEnabled() {
		t.Fatal("relay must be enabled")
	}
	if r := c.ForDevice(fp, now, false); len(r.Servers) != 0 {
		t.Fatalf("no STUN configured, the unrequested list must be empty: %+v", r)
	}
	c.STUNURLs = []string{"stun:h:3478"}
	for _, s := range c.ForDevice(fp, now, false).Servers {
		if s.Username != "" || s.Credential != "" || s.URLs[0] != "stun:h:3478" {
			t.Fatalf("credential or turn url issued without a request: %+v", s)
		}
	}
}

func TestDefaultTTLIsTenMinutes(t *testing.T) {
	c := Config{TURNURLs: []string{"turn:h"}, Secret: "s", Enabled: true}
	if got := c.ForDevice(fp, now, true).ExpiresAt; !got.Equal(now.Add(10 * time.Minute)) {
		t.Fatalf("expiry %v", got)
	}
}
