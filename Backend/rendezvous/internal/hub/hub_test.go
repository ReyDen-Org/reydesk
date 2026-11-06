package hub

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	rendezvousv1 "reydesk/rendezvous/gen"
	"reydesk/rendezvous/internal/auth"
	"reydesk/rendezvous/internal/db"
	"reydesk/rendezvous/internal/ice"
	"reydesk/rendezvous/internal/ids"
	"reydesk/rendezvous/internal/limits"
	"reydesk/rendezvous/internal/logx"
)

const publicURL = "wss://rendezvous.example.test/v1/ws"

// ---- in-memory auth.Store ----

type memStore struct {
	mu   sync.Mutex
	rows map[string]*db.Device
}

func (s *memStore) DeviceByDeskID(_ context.Context, id string) (*db.Device, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if d, ok := s.rows[id]; ok {
		c := *d
		return &c, nil
	}
	return nil, db.ErrNotFound
}
func (s *memStore) ActiveDeviceByKeyFP(_ context.Context, fp []byte) (*db.Device, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, d := range s.rows {
		if d.RevokedAt == nil && bytes.Equal(d.KeyFP, fp) {
			c := *d
			return &c, nil
		}
	}
	return nil, db.ErrNotFound
}
func (s *memStore) InsertDevice(_ context.Context, id string, fp, spki []byte, p int16) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.rows == nil {
		s.rows = map[string]*db.Device{}
	}
	if _, ok := s.rows[id]; ok {
		return false, nil
	}
	s.rows[id] = &db.Device{DeskID: id, KeyFP: fp, SPKI: spki, Platform: p}
	return true, nil
}
func (s *memStore) TouchLastAuth(context.Context, string) error { return nil }
func (s *memStore) BumpStat(context.Context, string) error      { return nil }
func (s *memStore) Revoke(_ context.Context, id string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	now := time.Now()
	s.rows[id].RevokedAt = &now
	return nil
}

// ---- harness ----

type safeBuf struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (s *safeBuf) Write(p []byte) (int, error) { s.mu.Lock(); defer s.mu.Unlock(); return s.b.Write(p) }
func (s *safeBuf) String() string              { s.mu.Lock(); defer s.mu.Unlock(); return s.b.String() }

type fakeClock struct{ ns atomic.Int64 }

func newClock() *fakeClock {
	c := &fakeClock{}
	c.ns.Store(time.Now().UnixNano())
	return c
}
func (c *fakeClock) Now() time.Time          { return time.Unix(0, c.ns.Load()) }
func (c *fakeClock) Advance(d time.Duration) { c.ns.Add(int64(d)) }

type env struct {
	t     *testing.T
	srv   *httptest.Server
	hub   *Hub
	store *memStore
	log   *safeBuf
	clock *fakeClock
	lim   *limits.Limiter
}

type opts struct {
	hubCfg      func(*Config)
	srvCfg      func(*ServerConfig)
	trustedAll  bool
	iceConfig   *ice.Config
	useRealTime bool
}

func newEnv(t *testing.T, o opts) *env {
	t.Helper()
	e := &env{t: t, store: &memStore{}, log: &safeBuf{}, clock: newClock()}
	e.lim = limits.New(nil)
	lg := logx.New(slog.New(slog.NewJSONHandler(e.log, &slog.HandlerOptions{Level: slog.LevelDebug})), nil)
	hc := Config{Limiter: e.lim, Log: lg, Clock: e.clock.Now, MailboxWait: 600 * time.Millisecond, DecisionTimeout: 2 * time.Second, Floor: 300 * time.Millisecond}
	if o.hubCfg != nil {
		o.hubCfg(&hc)
	}
	e.hub = New(hc)
	ic := o.iceConfig
	if ic == nil {
		ic = &ice.Config{}
	}
	sc := ServerConfig{
		Hub: e.hub, Limiter: e.lim, Ice: ic, Log: lg, Clock: e.clock.Now,
		Verifier: auth.New(auth.Config{Store: e.store, Limiter: e.lim, PublicWSURL: publicURL, Clock: e.clock.Now}),
	}
	if o.trustedAll {
		sc.TrustedProxies = []netip.Prefix{netip.MustParsePrefix("127.0.0.0/8")}
	}
	if o.srvCfg != nil {
		o.srvCfg(&sc)
	}
	e.srv = httptest.NewServer(NewServer(sc))
	t.Cleanup(func() { e.hub.CloseAll(); e.srv.Close() })
	return e
}

type client struct {
	t      *testing.T
	conn   *websocket.Conn
	key    *ecdsa.PrivateKey
	spki   []byte
	DeskID string
	nonce  []byte
	Ice    *rendezvousv1.IceConfig
	seq    uint64
	in     chan inbound
}

type inbound struct {
	f   *pb
	err error
}

// rawClient dials and starts a reader goroutine. A websocket read must never be
// abandoned with a timed-out context (the library closes the connection), so
// all waiting happens on the channel.
func (e *env) rawClient() *client {
	c := &client{t: e.t, conn: e.rawDial(nil), in: make(chan inbound, 4096)}
	go func() {
		for {
			_, b, err := c.conn.Read(context.Background())
			if err != nil {
				c.in <- inbound{err: err}
				return
			}
			f := &pb{}
			if err := proto.Unmarshal(b, f); err != nil {
				c.in <- inbound{err: err}
				return
			}
			c.in <- inbound{f: f}
		}
	}()
	return c
}

func newKey(t *testing.T) (*ecdsa.PrivateKey, []byte) {
	t.Helper()
	k, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	spki, err := x509.MarshalPKIXPublicKey(&k.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	return k, spki
}

func (e *env) rawDial(headers http.Header) *websocket.Conn {
	e.t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	c, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(e.srv.URL, "http")+Endpoint, &websocket.DialOptions{HTTPHeader: headers})
	if err != nil {
		e.t.Fatalf("dial: %v", err)
	}
	e.t.Cleanup(func() { c.CloseNow() })
	return c
}

// connect dials, authenticates (registering when deskID is empty) and returns the client.
func (e *env) connect(key *ecdsa.PrivateKey, spki []byte, deskID string) *client {
	e.t.Helper()
	c := e.rawClient()
	c.key, c.spki = key, spki
	hello := c.read().GetServerHello()
	if hello == nil || len(hello.Nonce) != 32 || hello.Protocol != 1 {
		e.t.Fatalf("bad hello %v", hello)
	}
	c.nonce = hello.Nonce
	sig, err := ecdsa.SignASN1(rand.Reader, key, hashOf(auth.AuthPayload(hello.Nonce, publicURL, spki, deskID)))
	if err != nil {
		e.t.Fatal(err)
	}
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_ClientAuth{ClientAuth: &rendezvousv1.ClientAuth{
		Spki: spki, DeskId: deskID, Platform: "windows", AppVersion: "t", Sig: sig}}})
	r := c.read().GetAuthResult()
	if r == nil || !r.Ok {
		e.t.Fatalf("auth failed: %v", r)
	}
	c.DeskID = r.DeskId
	c.Ice = c.read().GetIceConfig()
	if c.Ice == nil {
		e.t.Fatal("no IceConfig after AuthResult")
	}
	return c
}

func (e *env) newDevice() *client {
	k, spki := newKey(e.t)
	return e.connect(k, spki, "")
}

func toHex(b []byte) string { return hex.EncodeToString(b) }
func toB64(b []byte) string { return base64.StdEncoding.EncodeToString(b) }

func hashOf(b []byte) []byte { h := sha256.Sum256(b); return h[:] }

func (c *client) send(f *pb) {
	c.t.Helper()
	c.seq++
	if f.Seq == 0 {
		f.Seq = c.seq
	}
	b, _ := proto.Marshal(f)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := c.conn.Write(ctx, websocket.MessageBinary, b); err != nil {
		c.t.Fatalf("write: %v", err)
	}
}

func (c *client) readErr(d time.Duration) (*pb, error) {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case m := <-c.in:
		if m.err != nil {
			c.in <- m // keep the error for later reads
		}
		return m.f, m.err
	case <-t.C:
		return nil, context.DeadlineExceeded
	}
}

func (c *client) read() *pb {
	c.t.Helper()
	for {
		f, err := c.readErr(5 * time.Second)
		if err != nil {
			c.t.Fatalf("read: %v", err)
		}
		if f.GetPing() != nil { // keep-alive noise
			continue
		}
		return f
	}
}

// expectClosed waits for the server to close the socket.
func (c *client) expectClosed() {
	c.t.Helper()
	for i := 0; i < 400; i++ {
		if _, err := c.readErr(3 * time.Second); err != nil {
			if err == context.DeadlineExceeded {
				c.t.Fatal("socket not closed")
			}
			return
		}
	}
	c.t.Fatal("socket still open")
}

// expectSilence asserts nothing (except pings) arrives within d.
func (c *client) expectSilence(d time.Duration) {
	c.t.Helper()
	deadline := time.Now().Add(d)
	for time.Until(deadline) > 0 {
		f, err := c.readErr(time.Until(deadline))
		if err != nil {
			return
		}
		if f.GetPing() == nil {
			c.t.Fatalf("unexpected frame %v", f)
		}
	}
}

func (c *client) openPipe(target string, purpose rendezvousv1.PipePurpose, code string) uint64 {
	c.seq++
	seq := c.seq
	c.send(&pb{Seq: seq, Msg: &rendezvousv1.RendezvousFrame_OpenPipe{OpenPipe: &rendezvousv1.OpenPipe{TargetDeskId: target, Purpose: purpose, Code: code}}})
	return seq
}

func (c *client) createSlot(code string, ttl uint32) {
	salt := bytes.Repeat([]byte{7}, 16)
	hash := SlotHash(c.DeskID, code, salt)
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PairSlotCreate{PairSlotCreate: &rendezvousv1.PairSlotCreate{CodeHash: hash[:], Salt: salt, TtlS: ttl}}})
	st := c.read().GetPairSlotStatus()
	if st == nil || st.AttemptsLeft != SlotAttempts {
		c.t.Fatalf("slot status %v", st)
	}
}

func (c *client) pipeData(id string, data []byte) {
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeData{PipeData: &rendezvousv1.PipeData{PipeId: id, Data: data}}})
}

const (
	pCode      = rendezvousv1.PipePurpose_PIPE_PURPOSE_PAIR_CODE
	pReconnect = rendezvousv1.PipePurpose_PIPE_PURPOSE_RECONNECT
)

// pairUp opens a pipe phone->host with a pair code and has the host accept.
func pairUp(t *testing.T, host, phone *client, code string) (pipeID string) {
	t.Helper()
	host.createSlot(code, 300)
	seq := phone.openPipe(host.DeskID, pCode, code)
	in := host.read().GetIncomingPipe()
	if in == nil || in.RequesterDeskId != phone.DeskID || len(in.RequesterFp) != 32 || in.Purpose != pCode {
		t.Fatalf("incoming %v", in)
	}
	host.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeDecision{PipeDecision: &rendezvousv1.PipeDecision{PipeId: in.PipeId, Accept: true}}})
	f := phone.read()
	po := f.GetPipeOpened()
	if po == nil || po.PipeId != in.PipeId || !bytes.Equal(po.PeerSpki, host.spki) || f.Seq != seq {
		t.Fatalf("phone PipeOpened %v", f)
	}
	ho := host.read().GetPipeOpened()
	if ho == nil || ho.PipeId != in.PipeId || !bytes.Equal(ho.PeerSpki, phone.spki) {
		t.Fatalf("host PipeOpened %v", ho)
	}
	return in.PipeId
}

func failReason(f *pb) rendezvousv1.PipeFailReason {
	if f.GetPipeFailed() == nil {
		return -1
	}
	return f.GetPipeFailed().Reason
}

// ---- tests ----

func TestAuthThenIceConfigRelayOff(t *testing.T) {
	e := newEnv(t, opts{iceConfig: &ice.Config{STUNURLs: []string{"stun:s:3478"}, TURNURLs: []string{"turn:s:3478"}}}) // no secret
	c := e.newDevice()
	if len(c.Ice.IceServers) != 1 || c.Ice.IceServers[0].Username != "" || c.Ice.IceServers[0].Credential != "" {
		t.Fatalf("relay off must send STUN only: %v", c.Ice)
	}
}

func TestRelayNotSwitchedOnIssuesNothingEvenWhenAsked(t *testing.T) {
	// URLs and secret present but TURN_RELAY_ENABLED false.
	e := newEnv(t, opts{iceConfig: &ice.Config{STUNURLs: []string{"stun:s:3478"}, TURNURLs: []string{"turn:s:3478"}, Secret: "sec"}})
	c := e.newDevice()
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_IceConfigRequest{IceConfigRequest: &rendezvousv1.IceConfigRequest{}}})
	got := c.read().GetIceConfig()
	if got == nil {
		t.Fatal("IceConfigRequest not answered")
	}
	for _, sv := range append(append([]*rendezvousv1.IceServer{}, c.Ice.IceServers...), got.IceServers...) {
		if sv.Username != "" || sv.Credential != "" {
			t.Fatalf("credential issued with relay not enabled: %v", sv)
		}
	}
}

func TestIceConfigRelayOnCarriesCredentialOnlyWhenAsked(t *testing.T) {
	e := newEnv(t, opts{iceConfig: &ice.Config{STUNURLs: []string{"stun:s:3478"}, TURNURLs: []string{"turn:s:3478"}, Secret: "sec", TTL: time.Hour, Enabled: true}})
	c := e.newDevice()
	for _, sv := range c.Ice.IceServers {
		if sv.Username != "" || sv.Credential != "" {
			t.Fatalf("auth-time IceConfig must not carry TURN credentials: %v", c.Ice)
		}
	}
	req := &pb{Msg: &rendezvousv1.RendezvousFrame_IceConfigRequest{IceConfigRequest: &rendezvousv1.IceConfigRequest{}}}
	c.send(req)
	got := c.read().GetIceConfig()
	if got == nil {
		t.Fatal("IceConfigRequest not answered")
	}
	var withCred int
	for _, sv := range got.IceServers {
		if sv.Username != "" && sv.Credential != "" {
			withCred++
		}
	}
	if withCred != 1 {
		t.Fatalf("requested config must carry exactly one TURN credential: %v", got)
	}
	c.send(&pb{Msg: req.Msg})
	if er := c.read().GetError(); er == nil || er.Code != rendezvousv1.ErrorCode_ERROR_CODE_RATE_LIMITED {
		t.Fatal("rapid IceConfigRequest must be rate limited")
	}
}

func TestNothingBeforeAuth(t *testing.T) {
	e := newEnv(t, opts{})
	for name, f := range map[string]*pb{
		"pipe_data": {Msg: &rendezvousv1.RendezvousFrame_PipeData{PipeData: &rendezvousv1.PipeData{PipeId: "x", Data: []byte("a")}}},
		"open_pipe": {Msg: &rendezvousv1.RendezvousFrame_OpenPipe{OpenPipe: &rendezvousv1.OpenPipe{TargetDeskId: "0123456789AB", Purpose: pReconnect}}},
		"ping":      {Msg: &rendezvousv1.RendezvousFrame_Ping{Ping: &rendezvousv1.Ping{}}},
	} {
		c := e.rawClient()
		if c.read().GetServerHello() == nil {
			t.Fatal("no hello")
		}
		c.send(f)
		if er := c.read().GetError(); er == nil || er.Code != rendezvousv1.ErrorCode_ERROR_CODE_NOT_AUTHENTICATED {
			t.Fatalf("%s: want NOT_AUTHENTICATED", name)
		}
		c.expectClosed()
	}
}

func TestAuthDeadlineClosesSilentSocket(t *testing.T) {
	e := newEnv(t, opts{srvCfg: func(c *ServerConfig) { c.AuthDeadline = 300 * time.Millisecond }})
	c := e.rawClient()
	c.read() // hello
	start := time.Now()
	c.expectClosed()
	if time.Since(start) > 3*time.Second {
		t.Fatal("deadline not enforced")
	}
}

func TestBadSignatureDenied(t *testing.T) {
	e := newEnv(t, opts{})
	k, spki := newKey(t)
	c := e.rawClient()
	hello := c.read().GetServerHello()
	sig, _ := ecdsa.SignASN1(rand.Reader, k, hashOf(auth.AuthPayload(hello.Nonce, "wss://evil.example/v1/ws", spki, "")))
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_ClientAuth{ClientAuth: &rendezvousv1.ClientAuth{Spki: spki, Sig: sig}}})
	r := c.read().GetAuthResult()
	if r == nil || r.Ok || r.Error != rendezvousv1.AuthError_AUTH_ERROR_BAD_SIGNATURE {
		t.Fatalf("got %v", r)
	}
	c.expectClosed()
}

func TestPairCodePipeEndToEndAndIsolation(t *testing.T) {
	e := newEnv(t, opts{})
	host, phone, other := e.newDevice(), e.newDevice(), e.newDevice()
	id := pairUp(t, host, phone, "123456")

	phone.pipeData(id, []byte("hello host"))
	if d := host.read().GetPipeData(); d == nil || d.PipeId != id || string(d.Data) != "hello host" {
		t.Fatalf("host got %v", d)
	}
	host.pipeData(id, []byte("hello phone"))
	if d := phone.read().GetPipeData(); d == nil || string(d.Data) != "hello phone" {
		t.Fatalf("phone got %v", d)
	}

	// a third authenticated device that learned the pipe id cannot inject into or read the pipe
	other.pipeData(id, []byte("INJECTED"))
	if c := other.read().GetPipeClose(); c == nil || c.PipeId != id {
		t.Fatal("outsider must just get PipeClose")
	}
	other.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeClose{PipeClose: &rendezvousv1.PipeClose{PipeId: id}}})
	other.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeDecision{PipeDecision: &rendezvousv1.PipeDecision{PipeId: id, Accept: false}}})
	other.read() // PipeClose answer to the bogus decision
	host.expectSilence(300 * time.Millisecond)
	phone.expectSilence(100 * time.Millisecond)

	// the slot was single use: the same code is gone
	seq := other.openPipe(host.DeskID, pCode, "123456")
	f := other.read()
	if failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE || f.Seq != seq {
		t.Fatalf("reused code: %v", f)
	}

	// closing from one end tells the other, and the id is dead afterwards
	phone.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeClose{PipeClose: &rendezvousv1.PipeClose{PipeId: id}}})
	if c := host.read().GetPipeClose(); c == nil || c.PipeId != id {
		t.Fatal("host not told of close")
	}
	host.pipeData(id, []byte("late"))
	if host.read().GetPipeClose() == nil {
		t.Fatal("data on closed pipe must be answered with PipeClose")
	}
	if _, n := e.hub.Counts(); n != 0 {
		t.Fatalf("pipes left: %d", n)
	}
}

func TestUniformUnavailable(t *testing.T) {
	e := newEnv(t, opts{})
	host, phone := e.newDevice(), e.newDevice()
	U := rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE

	// unknown (well-formed) id: waits out the mailbox like an offline device, then UNAVAILABLE
	seq := phone.openPipe(ids.FromBits(12345), pReconnect, "")
	if f := phone.read(); failReason(f) != U || f.Seq != seq {
		t.Fatalf("unknown: %v", f)
	}
	// malformed id: same reason
	phone.openPipe("not-an-id", pReconnect, "")
	if f := phone.read(); failReason(f) != U {
		t.Fatalf("malformed: %v", f)
	}
	// host online but no slot for a PAIR_CODE open: same reason, floor applies
	start := time.Now()
	phone.openPipe(host.DeskID, pCode, "000000")
	if f := phone.read(); failReason(f) != U {
		t.Fatalf("no slot: %v", f)
	}
	if time.Since(start) < 290*time.Millisecond {
		t.Fatalf("floor not applied: %v", time.Since(start))
	}
	// host declines: same reason, floor applies
	start = time.Now()
	phone.openPipe(host.DeskID, pReconnect, "")
	in := host.read().GetIncomingPipe()
	host.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeDecision{PipeDecision: &rendezvousv1.PipeDecision{PipeId: in.PipeId, Accept: false}}})
	if f := phone.read(); failReason(f) != U {
		t.Fatalf("declined: %v", f)
	}
	if time.Since(start) < 290*time.Millisecond {
		t.Fatalf("floor not applied on decline: %v", time.Since(start))
	}
	// the declined pipe is gone
	if _, n := e.hub.Counts(); n != 0 {
		t.Fatalf("pipes left: %d", n)
	}
}

func TestDecisionTimeout(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.DecisionTimeout = 300 * time.Millisecond }})
	host, phone := e.newDevice(), e.newDevice()
	phone.openPipe(host.DeskID, pReconnect, "")
	in := host.read().GetIncomingPipe()
	if f := phone.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_TIMEOUT {
		t.Fatalf("got %v", f)
	}
	if c := host.read().GetPipeClose(); c == nil || c.PipeId != in.PipeId {
		t.Fatal("host must be told the request is gone")
	}
}

func TestMailboxHostReconnectsInTime(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.MailboxWait = 3 * time.Second }})
	hostKey, hostSPKI := newKey(t)
	host := e.connect(hostKey, hostSPKI, "")
	hostID := host.DeskID
	host.conn.CloseNow()
	deadline := time.Now().Add(2 * time.Second)
	for e.hub.Online(hostID) && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	phone := e.newDevice()
	seq := phone.openPipe(hostID, pReconnect, "")
	time.Sleep(200 * time.Millisecond)
	host2 := e.connect(hostKey, hostSPKI, hostID)
	in := host2.read().GetIncomingPipe()
	if in == nil || in.RequesterDeskId != phone.DeskID {
		t.Fatalf("mailboxed open not delivered: %v", in)
	}
	host2.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeDecision{PipeDecision: &rendezvousv1.PipeDecision{PipeId: in.PipeId, Accept: true}}})
	if f := phone.read(); f.GetPipeOpened() == nil || f.Seq != seq {
		t.Fatalf("got %v", f)
	}
}

func TestWrongCodeAttemptsDestroySlot(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.Floor = 20 * time.Millisecond }})
	host, phone := e.newDevice(), e.newDevice()
	host.createSlot("123456", 300)
	for i := 1; i <= SlotAttempts; i++ {
		phone.openPipe(host.DeskID, pCode, "000000")
		if f := phone.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_BAD_CODE {
			t.Fatalf("attempt %d: %v", i, f)
		}
		st := host.read().GetPairSlotStatus()
		if st == nil || int(st.AttemptsLeft) != SlotAttempts-i || st.Expired != (i == SlotAttempts) {
			t.Fatalf("attempt %d: status %v", i, st)
		}
	}
	// even the right code is refused now, with the uniform reason
	phone.openPipe(host.DeskID, pCode, "123456")
	if f := phone.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE {
		t.Fatalf("destroyed slot accepted a code: %v", f)
	}
	host.expectSilence(100 * time.Millisecond)
}

func TestSlotTTLExpiry(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.Floor = 20 * time.Millisecond }})
	host, phone := e.newDevice(), e.newDevice()
	host.createSlot("123456", 60)
	e.clock.Advance(61 * time.Second)
	phone.openPipe(host.DeskID, pCode, "123456")
	if f := phone.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE {
		t.Fatalf("expired slot accepted: %v", f)
	}
	if st := host.read().GetPairSlotStatus(); st == nil || !st.Expired {
		t.Fatalf("host not told: %v", st)
	}
}

func TestSlotReplaceCancelAndValidation(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.Floor = 20 * time.Millisecond }})
	host, phone := e.newDevice(), e.newDevice()
	host.createSlot("111111", 300)
	host.createSlot("222222", 300) // replaces
	phone.openPipe(host.DeskID, pCode, "111111")
	if f := phone.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_BAD_CODE {
		t.Fatalf("old slot still valid: %v", f)
	}
	host.read() // status
	host.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PairSlotCancel{PairSlotCancel: &rendezvousv1.PairSlotCancel{}}})
	time.Sleep(50 * time.Millisecond)
	phone.openPipe(host.DeskID, pCode, "222222")
	if f := phone.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE {
		t.Fatalf("cancelled slot valid: %v", f)
	}
	// ttl > 300 is a protocol violation
	bad := bytes.Repeat([]byte{1}, 32)
	host.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PairSlotCreate{PairSlotCreate: &rendezvousv1.PairSlotCreate{CodeHash: bad, Salt: bad[:16], TtlS: 301}}})
	host.read() // Error
	host.expectClosed()
}

func TestPipeByteCap(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.PipeRate = 1 << 30 }})
	host, phone := e.newDevice(), e.newDevice()
	id := pairUp(t, host, phone, "123456")
	chunk := bytes.Repeat([]byte{9}, MaxPipeData)
	go func() { // drain the host so its queue never fills
		for {
			if _, err := host.readErr(5 * time.Second); err != nil {
				return
			}
		}
	}()
	sent := 0
	for sent+MaxPipeData <= MaxPipeBytes {
		phone.pipeData(id, chunk)
		sent += MaxPipeData
	}
	phone.pipeData(id, chunk) // one frame over the 1 MiB cap
	deadline := time.Now().Add(5 * time.Second)
	for {
		f := phone.read()
		if f.GetPipeClose() != nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("cap not enforced")
		}
	}
	if _, n := e.hub.Counts(); n != 0 {
		t.Fatalf("pipe survived the cap: %d", n)
	}
}

func TestPipeRateBackpressure(t *testing.T) {
	e := newEnv(t, opts{})
	host, phone := e.newDevice(), e.newDevice()
	id := pairUp(t, host, phone, "123456")
	chunk := bytes.Repeat([]byte{1}, MaxPipeData)
	start := time.Now()
	for i := 0; i < 8; i++ { // 128 KiB at 64 KiB/s with a 64 KiB burst: about 1 s
		phone.pipeData(id, chunk)
	}
	var got []byte
	for len(got) < 8*MaxPipeData {
		d := host.read().GetPipeData()
		if d == nil {
			t.Fatal("stream interrupted")
		}
		got = append(got, d.Data...)
	}
	if el := time.Since(start); el < 700*time.Millisecond {
		t.Fatalf("rate not enforced: %v", el)
	}
	if !bytes.Equal(got, bytes.Repeat([]byte{1}, 8*MaxPipeData)) {
		t.Fatal("bytes lost or reordered (backpressure must not drop)")
	}
}

func TestOversizePipeDataIsViolation(t *testing.T) {
	e := newEnv(t, opts{})
	host, phone := e.newDevice(), e.newDevice()
	id := pairUp(t, host, phone, "123456")
	phone.pipeData(id, make([]byte, MaxPipeData+1))
	phone.read() // Error BAD_FRAME
	phone.expectClosed()
	if c := host.read().GetPipeClose(); c == nil {
		t.Fatal("host must learn the pipe died with its peer")
	}
}

func TestFrameSizeCap(t *testing.T) {
	e := newEnv(t, opts{})
	c := e.newDevice()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = c.conn.Write(ctx, websocket.MessageBinary, make([]byte, MaxFrame+1))
	c.expectClosed()
}

func TestPreAuthFrameCap(t *testing.T) {
	e := newEnv(t, opts{})
	c := e.rawClient()
	c.read()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = c.conn.Write(ctx, websocket.MessageBinary, make([]byte, preAuthMaxFrame+1))
	c.expectClosed()
}

func TestTextFrameAndGarbageClose(t *testing.T) {
	e := newEnv(t, opts{})
	c := e.newDevice()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = c.conn.Write(ctx, websocket.MessageText, []byte("hi"))
	c.expectClosed()
	d := e.newDevice()
	_ = d.conn.Write(ctx, websocket.MessageBinary, []byte{0xff, 0xff, 0xff, 0xff, 0xff})
	d.expectClosed()
}

func TestUnexpectedFramesClose(t *testing.T) {
	e := newEnv(t, opts{})
	for name, f := range map[string]*pb{
		"server_hello": {Msg: &rendezvousv1.RendezvousFrame_ServerHello{ServerHello: &rendezvousv1.ServerHello{}}},
		"second_auth":  {Msg: &rendezvousv1.RendezvousFrame_ClientAuth{ClientAuth: &rendezvousv1.ClientAuth{}}},
		"incoming":     {Msg: &rendezvousv1.RendezvousFrame_IncomingPipe{IncomingPipe: &rendezvousv1.IncomingPipe{}}},
		"empty":        {},
	} {
		c := e.newDevice()
		c.send(f)
		c.read() // Error
		c.expectClosed()
		_ = name
	}
}

func TestOpenPipeValidation(t *testing.T) {
	e := newEnv(t, opts{})
	for name, m := range map[string]*rendezvousv1.OpenPipe{
		"no_purpose":    {TargetDeskId: "0123456789AB"},
		"short_code":    {TargetDeskId: "0123456789AB", Purpose: pCode, Code: "12345"},
		"letters":       {TargetDeskId: "0123456789AB", Purpose: pCode, Code: "12345a"},
		"code_on_recon": {TargetDeskId: "0123456789AB", Purpose: pReconnect, Code: "123456"},
		"huge_target":   {TargetDeskId: strings.Repeat("A", 100), Purpose: pReconnect},
	} {
		c := e.newDevice()
		c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_OpenPipe{OpenPipe: m}})
		c.read()
		c.expectClosed()
		_ = name
	}
}

func TestFrameRateLimitCloses(t *testing.T) {
	e := newEnv(t, opts{})
	c := e.newDevice()
	for i := 0; i < 400; i++ {
		b, _ := proto.Marshal(&pb{Msg: &rendezvousv1.RendezvousFrame_Pong{Pong: &rendezvousv1.Pong{}}})
		ctx, cancel := context.WithTimeout(context.Background(), time.Second)
		err := c.conn.Write(ctx, websocket.MessageBinary, b)
		cancel()
		if err != nil {
			break
		}
	}
	c.expectClosed()
}

func TestIdleTimeoutAndServerPing(t *testing.T) {
	e := newEnv(t, opts{srvCfg: func(c *ServerConfig) { c.PingEvery = 100 * time.Millisecond; c.IdleTimeout = 600 * time.Millisecond }})
	c := e.newDevice()
	sawPing := false
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		f, err := c.readErr(2 * time.Second)
		if err != nil {
			if !sawPing {
				t.Fatal("closed before any Ping")
			}
			return
		}
		if f.GetPing() != nil {
			sawPing = true
		}
	}
	t.Fatal("silent client was not closed")
}

func TestClientPingGetsPong(t *testing.T) {
	e := newEnv(t, opts{})
	c := e.newDevice()
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_Ping{Ping: &rendezvousv1.Ping{}}})
	if c.read().GetPong() == nil {
		t.Fatal("no Pong")
	}
}

func TestDisconnectTearsDownPipesAndSlot(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.Floor = 20 * time.Millisecond }})
	host, phone := e.newDevice(), e.newDevice()
	id := pairUp(t, host, phone, "123456")
	phone.conn.CloseNow()
	if c := host.read().GetPipeClose(); c == nil || c.PipeId != id {
		t.Fatal("host not told")
	}
	if _, n := e.hub.Counts(); n != 0 {
		t.Fatal("pipe left")
	}
	// host disconnect drops its slot
	host.createSlot("999999", 300)
	hostID := host.DeskID
	host.conn.CloseNow()
	deadline := time.Now().Add(2 * time.Second)
	for e.hub.Online(hostID) && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	e.hub.mu.Lock()
	for _, p := range e.hub.online {
		if p.slot != nil {
			t.Error("slot of other peer?")
		}
	}
	e.hub.mu.Unlock()
}

func TestSweepIdleAndMaxLife(t *testing.T) {
	e := newEnv(t, opts{})
	host, phone := e.newDevice(), e.newDevice()
	id := pairUp(t, host, phone, "123456")
	e.clock.Advance(PipeIdle - time.Second)
	e.hub.Sweep()
	if _, n := e.hub.Counts(); n != 1 {
		t.Fatal("pipe swept too early")
	}
	phone.pipeData(id, []byte("x")) // activity resets idle
	host.read()
	e.clock.Advance(PipeIdle - time.Second)
	e.hub.Sweep()
	if _, n := e.hub.Counts(); n != 1 {
		t.Fatal("activity must reset idle timer")
	}
	e.clock.Advance(2 * time.Second)
	e.hub.Sweep()
	if c := host.read().GetPipeClose(); c == nil {
		t.Fatal("idle pipe not closed")
	}
	if phone.read().GetPipeClose() == nil {
		t.Fatal("phone not told")
	}
	// max life even when active
	id = pairUp(t, host, phone, "654321")
	for i := 0; i < 40; i++ {
		e.clock.Advance(PipeMaxLife / 40)
		phone.pipeData(id, []byte("x"))
		host.read()
		if i == 38 {
			break
		}
	}
	e.clock.Advance(PipeMaxLife / 40)
	e.hub.Sweep()
	if host.read().GetPipeClose() == nil {
		t.Fatal("max life not enforced")
	}
}

func TestPerDevicePipeCap(t *testing.T) {
	e := newEnv(t, opts{})
	host := e.newDevice()
	phones := make([]*client, 0, MaxPipesPerDevice)
	for i := 0; i < MaxPipesPerDevice; i++ {
		ph := e.newDevice()
		ph.openPipe(host.DeskID, pReconnect, "")
		in := host.read().GetIncomingPipe()
		host.send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeDecision{PipeDecision: &rendezvousv1.PipeDecision{PipeId: in.PipeId, Accept: true}}})
		ph.read()
		host.read()
		phones = append(phones, ph)
	}
	extra := e.newDevice()
	extra.openPipe(host.DeskID, pReconnect, "")
	if f := extra.read(); failReason(f) != rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE {
		t.Fatalf("9th pipe: %v", f)
	}
	_ = phones
}

func TestOpenRateLimitedPerDevice(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.MailboxWait = 30 * time.Millisecond; c.Floor = time.Millisecond }})
	c := e.newDevice()
	sawRate := false
	for i := 0; i < limits.PipeDistinctMax+3 && !sawRate; i++ {
		c.openPipe(ids.FromBits(uint64(1000+i)), pReconnect, "")
		for {
			f := c.read()
			if f.GetError() != nil {
				continue
			}
			if failReason(f) == rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_RATE_LIMITED {
				sawRate = true
			}
			break
		}
	}
	if !sawRate {
		t.Fatal("device pipe-open limit not applied")
	}
}

func TestRevokeSelf(t *testing.T) {
	e := newEnv(t, opts{})
	k, spki := newKey(t)
	c := e.connect(k, spki, "")
	// a wrong signature is a violation and revokes nothing
	bad := e.connect(k, spki, c.DeskID)
	bad.send(&pb{Msg: &rendezvousv1.RendezvousFrame_RevokeSelf{RevokeSelf: &rendezvousv1.RevokeSelf{Sig: []byte{1, 2, 3}}}})
	bad.read()
	bad.expectClosed()
	if row := e.store.rows[c.DeskID]; row.RevokedAt != nil {
		t.Fatal("bad signature revoked the device")
	}
	c = e.connect(k, spki, c.DeskID)
	sig, _ := ecdsa.SignASN1(rand.Reader, k, hashOf(auth.RevokePayload(c.nonce)))
	c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_RevokeSelf{RevokeSelf: &rendezvousv1.RevokeSelf{Sig: sig}}})
	c.expectClosed()
	if row := e.store.rows[c.DeskID]; row.RevokedAt == nil {
		t.Fatal("device not tombstoned")
	}
	// reconnecting with the revoked ID is refused
	c2 := e.rawClient()
	hello := c2.read().GetServerHello()
	sig, _ = ecdsa.SignASN1(rand.Reader, k, hashOf(auth.AuthPayload(hello.Nonce, publicURL, spki, c.DeskID)))
	c2.send(&pb{Msg: &rendezvousv1.RendezvousFrame_ClientAuth{ClientAuth: &rendezvousv1.ClientAuth{Spki: spki, DeskId: c.DeskID, Sig: sig}}})
	if r := c2.read().GetAuthResult(); r == nil || r.Ok || r.Error != rendezvousv1.AuthError_AUTH_ERROR_REVOKED {
		t.Fatalf("got %v", r)
	}
}

func TestNewConnectionReplacesOld(t *testing.T) {
	e := newEnv(t, opts{})
	k, spki := newKey(t)
	a := e.connect(k, spki, "")
	b := e.connect(k, spki, a.DeskID)
	a.expectClosed()
	if !e.hub.Online(a.DeskID) {
		t.Fatal("new connection must stay registered")
	}
	b.send(&pb{Msg: &rendezvousv1.RendezvousFrame_Ping{Ping: &rendezvousv1.Ping{}}})
	if b.read().GetPong() == nil {
		t.Fatal("new connection dead")
	}
}

func TestConnectionLimitPerIPAndGlobal(t *testing.T) {
	e := newEnv(t, opts{srvCfg: func(c *ServerConfig) { c.MaxConnections = 2 }})
	e.rawDial(nil)
	e.rawDial(nil)
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	_, resp, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(e.srv.URL, "http")+Endpoint, nil)
	if err == nil || resp == nil || resp.StatusCode != http.StatusServiceUnavailable {
		t.Fatalf("global cap not enforced: %v %v", err, resp)
	}
}

func TestClientIP(t *testing.T) {
	trusted := []netip.Prefix{netip.MustParsePrefix("10.0.0.0/8")}
	mk := func(remote, xff string) *http.Request {
		r := httptest.NewRequest("GET", "/", nil)
		r.RemoteAddr = remote
		if xff != "" {
			r.Header.Set("X-Forwarded-For", xff)
		}
		return r
	}
	cases := []struct{ remote, xff, want string }{
		{"203.0.113.5:1234", "1.2.3.4", "203.0.113.5"},           // untrusted peer: header ignored
		{"10.0.0.2:1", "198.51.100.9", "198.51.100.9"},           // trusted proxy
		{"10.0.0.2:1", "6.6.6.6, 198.51.100.9", "198.51.100.9"},  // client-supplied left entry ignored
		{"10.0.0.2:1", "198.51.100.9, 10.0.0.7", "198.51.100.9"}, // chain of trusted hops
		{"10.0.0.2:1", "garbage", "10.0.0.2"},
		{"10.0.0.2:1", "", "10.0.0.2"},
	}
	for _, c := range cases {
		if got := ClientIP(mk(c.remote, c.xff), trusted); got != c.want {
			t.Errorf("%s %q: got %s want %s", c.remote, c.xff, got, c.want)
		}
	}
}

// Log assertion (spec 6): a full flow with distinctive payload, code and IDs
// must leave none of them, nor a full IP or a key, in the log.
func TestLogsContainNoPayloadCodeKeyOrFullIdentifiers(t *testing.T) {
	e := newEnv(t, opts{hubCfg: func(c *Config) { c.Floor = 10 * time.Millisecond }, trustedAll: true})
	host, phone := e.newDevice(), e.newDevice()
	const marker = "PIPEDATA-MARKER-9f3a"
	id := pairUp(t, host, phone, "482913")
	phone.pipeData(id, []byte(marker))
	host.read()
	host.createSlot("111222", 300)
	phone.openPipe(host.DeskID, pCode, "735201") // wrong code path
	phone.read()
	host.read()
	phone.conn.CloseNow()
	host.read() // PipeClose
	host.conn.CloseNow()
	time.Sleep(200 * time.Millisecond)

	out := e.log.String()
	if !strings.Contains(out, `"event":"pipe_open"`) || !strings.Contains(out, `"event":"auth"`) {
		t.Fatalf("expected events missing: %s", out)
	}
	secrets := []string{marker, "482913", "735201", host.DeskID, phone.DeskID, id, "127.0.0.1", "::1"}
	for _, s := range []*client{host, phone} {
		secrets = append(secrets, string(s.spki), toHex(s.spki), toB64(s.spki))
		secrets = append(secrets, host.DeskID[4:], phone.DeskID[4:])
	}
	for _, s := range secrets {
		if s != "" && strings.Contains(out, s) {
			t.Fatalf("log contains %q:\n%s", s, out)
		}
	}
	if !strings.Contains(out, `"desk_id":"`+host.DeskID[:4]+`"`) {
		t.Fatalf("expected 4 char desk id prefix:\n%s", out)
	}
}

// Bad first frames from one address never turn into an HTTP 429 for that whole
// address: a valid device behind the same IP still connects.
func TestBadFirstFramesDoNotLockOutSharedIP(t *testing.T) {
	e := newEnv(t, opts{})
	for i := 0; i < 8; i++ {
		c := e.rawClient()
		if c.read().GetServerHello() == nil {
			t.Fatal("no hello")
		}
		c.send(&pb{Msg: &rendezvousv1.RendezvousFrame_Ping{Ping: &rendezvousv1.Ping{}}})
		if er := c.read().GetError(); er == nil || er.Code != rendezvousv1.ErrorCode_ERROR_CODE_NOT_AUTHENTICATED {
			t.Fatal("expected NOT_AUTHENTICATED")
		}
	}
	if c := e.newDevice(); c.DeskID == "" { // dial would t.Fatal on an HTTP 429
		t.Fatal("valid device must still authenticate")
	}
}
