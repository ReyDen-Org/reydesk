// Command e2eclient plays two clients (a host and a phone) against a running
// rendezvous service and checks the whole flow end to end: register, pairing
// slot, pipe by code, echo both ways, BAD_CODE, uniform UNAVAILABLE and
// revoke-self. It speaks the wire protocol directly (its own signing code, no
// server internals except the Desk ID generator) so it also checks the spec.
//
//	e2eclient [-url ws://127.0.0.1:13260/v1/ws] [-sign-url <PUBLIC_WS_URL>]
//
// Exit code 0 means every check passed, 1 otherwise.
package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/binary"
	"flag"
	"fmt"
	"os"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	rv "reydesk/rendezvous/gen"
	"reydesk/rendezvous/internal/ids"
)

type frame = rv.RendezvousFrame

func lp(b *bytes.Buffer, p []byte) {
	var n [4]byte
	binary.BigEndian.PutUint32(n[:], uint32(len(p)))
	b.Write(n[:])
	b.Write(p)
}

func sign(key *ecdsa.PrivateKey, parts ...[]byte) []byte {
	var b bytes.Buffer
	for _, p := range parts {
		lp(&b, p)
	}
	h := sha256.Sum256(b.Bytes())
	sig, err := ecdsa.SignASN1(rand.Reader, key, h[:])
	if err != nil {
		panic(err)
	}
	return sig
}

type client struct {
	name   string
	key    *ecdsa.PrivateKey
	spki   []byte
	deskID string
	nonce  []byte
	conn   *websocket.Conn
	in     chan *frame
	errc   chan error
	seq    uint64
}

func newClient(name string) *client {
	k, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	check(err == nil, "keygen")
	spki, err := x509.MarshalPKIXPublicKey(&k.PublicKey)
	check(err == nil, "spki")
	return &client{name: name, key: k, spki: spki}
}

// dial connects and authenticates with the given Desk ID ("" registers) and
// returns the AuthResult.
func (c *client) dial(ctx context.Context, url, signURL, deskID, platform string) *rv.AuthResult {
	conn, _, err := websocket.Dial(ctx, url, nil)
	check(err == nil, "%s: dial: %v", c.name, err)
	conn.SetReadLimit(64 << 10)
	c.conn = conn
	c.in = make(chan *frame, 64)
	c.errc = make(chan error, 1)
	hello := c.readOne(ctx).GetServerHello()
	check(hello != nil && len(hello.Nonce) == 32, "%s: ServerHello with 32-byte nonce", c.name)
	c.nonce = hello.Nonce
	c.send(ctx, &frame{Msg: &rv.RendezvousFrame_ClientAuth{ClientAuth: &rv.ClientAuth{
		Spki: c.spki, DeskId: deskID, Platform: platform, AppVersion: "e2e",
		Sig: sign(c.key, []byte("ReyDesk/rendezvous/auth/v1"), c.nonce, []byte(signURL), c.spki, []byte(deskID)),
	}}})
	ar := c.readOne(ctx).GetAuthResult()
	check(ar != nil, "%s: AuthResult frame", c.name)
	if ar.Ok {
		go c.pump()
	}
	return ar
}

// readOne reads one frame synchronously (before the pump goroutine starts).
func (c *client) readOne(ctx context.Context) *frame {
	_, data, err := c.conn.Read(ctx)
	check(err == nil, "%s: read: %v", c.name, err)
	f := &frame{}
	check(proto.Unmarshal(data, f) == nil, "%s: decode", c.name)
	return f
}

func (c *client) pump() {
	for {
		_, data, err := c.conn.Read(context.Background())
		if err != nil {
			c.errc <- err
			close(c.in)
			return
		}
		f := &frame{}
		if proto.Unmarshal(data, f) != nil {
			c.errc <- fmt.Errorf("bad frame from server")
			close(c.in)
			return
		}
		if f.GetPing() != nil {
			_ = c.send(context.Background(), &frame{Msg: &rv.RendezvousFrame_Pong{Pong: &rv.Pong{}}})
			continue
		}
		c.in <- f
	}
}

func (c *client) send(ctx context.Context, f *frame) error {
	c.seq++
	f.Seq = c.seq
	b, err := proto.Marshal(f)
	check(err == nil, "marshal")
	return c.conn.Write(ctx, websocket.MessageBinary, b)
}

// next returns the next frame matching pred, skipping everything else, and
// fails on timeout or close.
func (c *client) next(what string, timeout time.Duration, pred func(*frame) bool) *frame {
	t := time.After(timeout)
	for {
		select {
		case f, ok := <-c.in:
			if !ok {
				check(false, "%s: connection closed while waiting for %s: %v", c.name, what, <-c.errc)
			}
			if pred(f) {
				return f
			}
		case <-t:
			check(false, "%s: timeout waiting for %s", c.name, what)
		}
	}
}

func check(ok bool, format string, a ...any) {
	if ok {
		return
	}
	fmt.Fprintf(os.Stderr, "FAIL: "+format+"\n", a...)
	os.Exit(1)
}

func pass(format string, a ...any) { fmt.Printf("ok   "+format+"\n", a...) }

func main() {
	url := flag.String("url", "ws://127.0.0.1:13260/v1/ws", "service WebSocket URL")
	signURL := flag.String("sign-url", "", "PUBLIC_WS_URL the server signs against (default: -url)")
	flag.Parse()
	if *signURL == "" {
		*signURL = *url
	}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()

	host, phone := newClient("host"), newClient("phone")

	// 1. Register both (empty desk_id); IceConfig follows the AuthResult.
	for _, c := range []*client{host, phone} {
		platform := "android"
		if c == host {
			platform = "windows"
		}
		ar := c.dial(ctx, *url, *signURL, "", platform)
		check(ar.Ok && ids.IsCanonical(ar.DeskId), "%s: register ok with canonical Desk ID (ok=%v id=%q err=%v)", c.name, ar.Ok, ar.DeskId, ar.Error)
		c.deskID = ar.DeskId
		c.next("IceConfig", 5*time.Second, func(f *frame) bool { return f.GetIceConfig() != nil })
		pass("%s registered as %s...", c.name, c.deskID[:4])
	}
	check(host.deskID != phone.deskID, "distinct Desk IDs")

	// 2. Host creates a pairing slot.
	code := "482915"
	salt := make([]byte, 16)
	_, _ = rand.Read(salt)
	var hb bytes.Buffer
	lp(&hb, []byte(host.deskID))
	lp(&hb, []byte(code))
	lp(&hb, salt)
	ch := sha256.Sum256(hb.Bytes())
	check(host.send(ctx, &frame{Msg: &rv.RendezvousFrame_PairSlotCreate{PairSlotCreate: &rv.PairSlotCreate{CodeHash: ch[:], Salt: salt, TtlS: 300}}}) == nil, "slot create")
	// The initial status frame doubles as the create ack; without waiting, the phone can race the slot.
	host.next("PairSlotStatus (initial)", 5*time.Second, func(f *frame) bool { return f.GetPairSlotStatus() != nil })
	pass("pairing slot created")

	// 3. Wrong code gets BAD_CODE; the host sees the attempt counter drop.
	phone.send(ctx, &frame{Msg: &rv.RendezvousFrame_OpenPipe{OpenPipe: &rv.OpenPipe{TargetDeskId: host.deskID, Purpose: rv.PipePurpose_PIPE_PURPOSE_PAIR_CODE, Code: "000000"}}})
	pf := phone.next("PipeFailed", 10*time.Second, func(f *frame) bool { return f.GetPipeFailed() != nil })
	check(pf.GetPipeFailed().Reason == rv.PipeFailReason_PIPE_FAIL_REASON_BAD_CODE, "wrong code: want BAD_CODE, got %v", pf.GetPipeFailed().Reason)
	st := host.next("PairSlotStatus", 5*time.Second, func(f *frame) bool {
		s := f.GetPairSlotStatus()
		return s != nil && s.AttemptsLeft < 5
	})
	check(!st.GetPairSlotStatus().Expired && st.GetPairSlotStatus().AttemptsLeft == 4, "slot status after one wrong code: %v", st.GetPairSlotStatus())
	pass("wrong code -> BAD_CODE, host sees attempts_left=4")

	// 4. Right code opens a pipe; the host accepts.
	phone.send(ctx, &frame{Msg: &rv.RendezvousFrame_OpenPipe{OpenPipe: &rv.OpenPipe{TargetDeskId: host.deskID, Purpose: rv.PipePurpose_PIPE_PURPOSE_PAIR_CODE, Code: code}}})
	inc := host.next("IncomingPipe", 10*time.Second, func(f *frame) bool { return f.GetIncomingPipe() != nil }).GetIncomingPipe()
	check(inc.RequesterDeskId == phone.deskID && inc.Purpose == rv.PipePurpose_PIPE_PURPOSE_PAIR_CODE, "IncomingPipe fields: %v", inc)
	check(len(inc.RequesterFp) > 0, "IncomingPipe requester_fp present")
	host.send(ctx, &frame{Msg: &rv.RendezvousFrame_PipeDecision{PipeDecision: &rv.PipeDecision{PipeId: inc.PipeId, Accept: true}}})
	po := phone.next("PipeOpened (phone)", 10*time.Second, func(f *frame) bool { return f.GetPipeOpened() != nil }).GetPipeOpened()
	check(po.PipeId == inc.PipeId && bytes.Equal(po.PeerSpki, host.spki), "phone PipeOpened must carry the host SPKI")
	ho := host.next("PipeOpened (host)", 10*time.Second, func(f *frame) bool { return f.GetPipeOpened() != nil }).GetPipeOpened()
	check(ho.PipeId == inc.PipeId && bytes.Equal(ho.PeerSpki, phone.spki), "host PipeOpened must carry the phone SPKI")
	pass("pipe opened with code, SPKIs exchanged")

	// 5. Echo both ways.
	pid := inc.PipeId
	echo := func(from, to *client, msg []byte) {
		from.send(ctx, &frame{Msg: &rv.RendezvousFrame_PipeData{PipeData: &rv.PipeData{PipeId: pid, Data: msg}}})
		got := to.next("PipeData", 10*time.Second, func(f *frame) bool { return f.GetPipeData() != nil }).GetPipeData()
		check(got.PipeId == pid && bytes.Equal(got.Data, msg), "%s -> %s: data mismatch", from.name, to.name)
	}
	echo(phone, host, []byte("hello host"))
	echo(host, phone, []byte("hello phone"))
	big := make([]byte, 16<<10)
	_, _ = rand.Read(big)
	echo(phone, host, big)
	pass("bytes echoed both ways (incl. a 16 KiB frame)")

	phone.send(ctx, &frame{Msg: &rv.RendezvousFrame_PipeClose{PipeClose: &rv.PipeClose{PipeId: pid}}})
	host.next("PipeClose", 10*time.Second, func(f *frame) bool { return f.GetPipeClose() != nil })
	pass("pipe close propagated")

	// 6. Unknown (but well-formed) target: uniform UNAVAILABLE.
	ghost, err := ids.Generate(rand.Reader)
	check(err == nil, "generate id")
	start := time.Now()
	phone.send(ctx, &frame{Msg: &rv.RendezvousFrame_OpenPipe{OpenPipe: &rv.OpenPipe{TargetDeskId: ghost, Purpose: rv.PipePurpose_PIPE_PURPOSE_RECONNECT}}})
	pf = phone.next("PipeFailed (unknown)", 40*time.Second, func(f *frame) bool { return f.GetPipeFailed() != nil })
	check(pf.GetPipeFailed().Reason == rv.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE, "unknown target: want UNAVAILABLE, got %v", pf.GetPipeFailed().Reason)
	pass("unknown target -> UNAVAILABLE after %s", time.Since(start).Round(time.Millisecond))

	// 7. Revoke self: the socket closes normally, the old ID answers REVOKED.
	host.send(ctx, &frame{Msg: &rv.RendezvousFrame_RevokeSelf{RevokeSelf: &rv.RevokeSelf{
		Sig: sign(host.key, []byte("ReyDesk/rendezvous/revoke/v1"), host.nonce)}}})
	select {
	case _, ok := <-host.in:
		check(!ok, "after revoke the server must close the socket")
	case <-time.After(10 * time.Second):
		check(false, "timeout waiting for close after revoke")
	}
	check(websocket.CloseStatus(<-host.errc) == websocket.StatusNormalClosure, "revoke should close with normal closure")
	again := newClient("host-again")
	again.key, again.spki = host.key, host.spki
	ar := again.dial(ctx, *url, *signURL, host.deskID, "windows")
	check(!ar.Ok && ar.Error == rv.AuthError_AUTH_ERROR_REVOKED, "revoked ID must answer REVOKED (ok=%v err=%v)", ar.Ok, ar.Error)
	pass("revoke-self closes the socket and the old Desk ID answers REVOKED")

	_ = phone.conn.Close(websocket.StatusNormalClosure, "done")
	fmt.Println("E2E PASS")
}
