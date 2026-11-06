package config

import "testing"

func env(m map[string]string) func(string) string { return func(k string) string { return m[k] } }

func TestRelayIsOffByDefault(t *testing.T) {
	c, err := load(env(map[string]string{"DATABASE_URL": "x", "TURN_URLS": "turn:h", "TURN_SECRET": "s"}))
	if err != nil {
		t.Fatal(err)
	}
	if c.TURNRelayEnabled {
		t.Fatal("URLs and secret alone must not enable the relay")
	}
	if c.TURNTTLSeconds != 600 {
		t.Fatalf("default TURN_TTL_S must be 600, got %d", c.TURNTTLSeconds)
	}
}

func TestRelayEnabledNeedsURLsAndSecret(t *testing.T) {
	if _, err := load(env(map[string]string{"DATABASE_URL": "x", "TURN_RELAY_ENABLED": "true"})); err == nil {
		t.Fatal("enabling the relay without URLs and secret must fail")
	}
	c, err := load(env(map[string]string{"DATABASE_URL": "x", "TURN_RELAY_ENABLED": "true", "TURN_URLS": "turn:h", "TURN_SECRET": "s"}))
	if err != nil || !c.TURNRelayEnabled {
		t.Fatalf("enabled relay rejected: %v", err)
	}
	if _, err := load(env(map[string]string{"DATABASE_URL": "x", "TURN_RELAY_ENABLED": "maybe"})); err == nil {
		t.Fatal("garbage value accepted")
	}
}
