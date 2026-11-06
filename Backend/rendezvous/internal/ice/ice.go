// Package ice builds the IceConfig a client receives after authentication
// (spec 3.9). STUN entries are public data. TURN credentials follow the coturn
// REST scheme (use-auth-secret) and are produced ONLY when the operator enabled
// relay explicitly (TURN_RELAY_ENABLED plus TURN_URLS and TURN_SECRET) AND the
// device asked for them (an IceConfigRequest); the IceConfig pushed right after
// authentication never carries a credential. Otherwise the server never
// computes or sends one.
package ice

import (
	"crypto/hmac"
	"crypto/sha1" //nolint:gosec // the coturn REST scheme mandates HMAC-SHA1
	"encoding/base64"
	"encoding/hex"
	"strconv"
	"time"
)

// Config is the ICE part of the server environment.
type Config struct {
	STUNURLs []string
	TURNURLs []string
	Secret   string        // TURN_SECRET (coturn static-auth-secret)
	TTL      time.Duration // TURN_TTL_S (default 10 min), also the advertised config lifetime
	Enabled  bool          // TURN_RELAY_ENABLED: the operator switched the relay on
}

// Server is one ICE server entry; Username/Credential are set only for TURN.
type Server struct {
	URLs       []string
	Username   string
	Credential string
}

// Result is the config for one authenticated device.
type Result struct {
	Servers   []Server
	ExpiresAt time.Time
}

// RelayEnabled reports whether TURN credentials will be issued.
func (c *Config) RelayEnabled() bool {
	return c != nil && c.Enabled && len(c.TURNURLs) > 0 && c.Secret != ""
}

// DefaultTTL is the lifetime of a TURN credential when TTL is unset.
const DefaultTTL = 10 * time.Minute

// ForDevice returns the ICE servers for the device with key fingerprint keyFP.
// TURN credentials are included only when wantRelay is true (the device asked)
// and the relay is enabled.
func (c *Config) ForDevice(keyFP []byte, now time.Time, wantRelay bool) Result {
	ttl := c.TTL
	if ttl <= 0 {
		ttl = DefaultTTL
	}
	res := Result{ExpiresAt: now.Add(ttl)}
	if len(c.STUNURLs) > 0 {
		res.Servers = append(res.Servers, Server{URLs: append([]string(nil), c.STUNURLs...)})
	}
	if wantRelay && c.RelayEnabled() {
		user, cred := TURNCredential(c.Secret, keyFP, res.ExpiresAt)
		res.Servers = append(res.Servers, Server{
			URLs: append([]string(nil), c.TURNURLs...), Username: user, Credential: cred,
		})
	}
	return res
}

// TURNCredential returns the coturn REST pair: username
// "<expiry_unix>:<first 16 hex chars of key_fp>" and credential
// base64(HMAC-SHA1(secret, username)).
func TURNCredential(secret string, keyFP []byte, expiry time.Time) (username, credential string) {
	fp := hex.EncodeToString(keyFP)
	if len(fp) > 16 {
		fp = fp[:16]
	}
	username = strconv.FormatInt(expiry.Unix(), 10) + ":" + fp
	m := hmac.New(sha1.New, []byte(secret))
	m.Write([]byte(username))
	return username, base64.StdEncoding.EncodeToString(m.Sum(nil))
}
