// Package config loads the rendezvous server configuration from the environment.
package config

import (
	"errors"
	"fmt"
	"log/slog"
	"net/netip"
	"os"
	"strconv"
	"strings"
)

type Config struct {
	ListenAddr        string
	DatabaseURL       string
	PublicWSURL       string
	TrustedProxyCIDRs []netip.Prefix
	STUNURLs          []string
	TURNURLs          []string
	TURNSecret        string
	TURNRelayEnabled  bool // TURN_RELAY_ENABLED: relay credentials are issued only when true
	TURNTTLSeconds    int
	ReclaimUntil      string
	MaxConnections    int
	LogLevel          slog.Level
}

func Load() (*Config, error) { return load(os.Getenv) }

func load(get func(string) string) (*Config, error) {
	c := &Config{
		ListenAddr:   def(get("LISTEN_ADDR"), ":8080"),
		DatabaseURL:  get("DATABASE_URL"),
		PublicWSURL:  get("PUBLIC_WS_URL"),
		TURNSecret:   get("TURN_SECRET"),
		ReclaimUntil: strings.TrimSpace(get("RECLAIM_UNTIL")),
		STUNURLs:     list(get("STUN_URLS")),
		TURNURLs:     list(get("TURN_URLS")),
	}
	var errs []error
	if c.DatabaseURL == "" {
		errs = append(errs, errors.New("DATABASE_URL is required"))
	}
	for _, s := range list(get("TRUSTED_PROXY_CIDRS")) {
		p, err := netip.ParsePrefix(s)
		if err != nil {
			errs = append(errs, fmt.Errorf("TRUSTED_PROXY_CIDRS: %w", err))
			continue
		}
		c.TrustedProxyCIDRs = append(c.TrustedProxyCIDRs, p)
	}
	var err error
	if c.TURNTTLSeconds, err = intEnv(get, "TURN_TTL_S", 600); err != nil {
		errs = append(errs, err)
	}
	if c.MaxConnections, err = intEnv(get, "MAX_CONNECTIONS", 5000); err != nil {
		errs = append(errs, err)
	}
	switch strings.ToLower(strings.TrimSpace(get("TURN_RELAY_ENABLED"))) {
	case "", "0", "false", "no", "off":
	case "1", "true", "yes", "on":
		c.TURNRelayEnabled = true
	default:
		errs = append(errs, errors.New("TURN_RELAY_ENABLED must be true or false"))
	}
	if c.TURNRelayEnabled && (len(c.TURNURLs) == 0 || c.TURNSecret == "") {
		errs = append(errs, errors.New("TURN_URLS and TURN_SECRET are required when TURN_RELAY_ENABLED is true"))
	}
	if lv := get("LOG_LEVEL"); lv != "" {
		if err := c.LogLevel.UnmarshalText([]byte(lv)); err != nil {
			errs = append(errs, fmt.Errorf("LOG_LEVEL: %w", err))
		}
	}
	return c, errors.Join(errs...)
}

func def(v, d string) string {
	if v == "" {
		return d
	}
	return v
}

func intEnv(get func(string) string, key string, d int) (int, error) {
	v := get(key)
	if v == "" {
		return d, nil
	}
	n, err := strconv.Atoi(v)
	if err != nil || n <= 0 {
		return 0, fmt.Errorf("%s must be a positive integer", key)
	}
	return n, nil
}

func list(v string) []string {
	var out []string
	for _, s := range strings.Split(v, ",") {
		if s = strings.TrimSpace(s); s != "" {
			out = append(out, s)
		}
	}
	return out
}
