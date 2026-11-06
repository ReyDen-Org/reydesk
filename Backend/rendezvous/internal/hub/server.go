package hub

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	rendezvousv1 "reydesk/rendezvous/gen"
	"reydesk/rendezvous/internal/auth"
	"reydesk/rendezvous/internal/ice"
	"reydesk/rendezvous/internal/limits"
	"reydesk/rendezvous/internal/logx"
)

// Endpoint is the WebSocket route (spec 3.1).
const Endpoint = "/v1/ws"

const (
	MaxFrame        = 32 << 10 // spec 3.5 read limit
	preAuthMaxFrame = 2 << 10  // ClientAuth is a few hundred bytes
	protocolVersion = 1
	writeTimeout    = 10 * time.Second
	iceMinInterval  = 5 * time.Second
)

// ServerConfig wires the WebSocket endpoint.
type ServerConfig struct {
	Hub            *Hub
	Verifier       *auth.Verifier
	Limiter        *limits.Limiter
	Ice            *ice.Config
	Log            *logx.Logger
	TrustedProxies []netip.Prefix
	MaxConnections int           // global, 0 = 5000
	PingEvery      time.Duration // 25 s
	IdleTimeout    time.Duration // 60 s without any frame closes the socket
	AuthDeadline   time.Duration // 10 s
	Clock          func() time.Time
}

// Server is the http.Handler of GET /v1/ws.
type Server struct {
	c      ServerConfig
	active atomic.Int64
}

func NewServer(c ServerConfig) *Server {
	if c.MaxConnections <= 0 {
		c.MaxConnections = 5000
	}
	if c.PingEvery <= 0 {
		c.PingEvery = 25 * time.Second
	}
	if c.IdleTimeout <= 0 {
		c.IdleTimeout = 60 * time.Second
	}
	if c.AuthDeadline <= 0 {
		c.AuthDeadline = auth.AuthDeadline
	}
	if c.Clock == nil {
		c.Clock = time.Now
	}
	return &Server{c: c}
}

// ClientIP returns the client address: the peer address, or, when the peer is
// inside trusted, the right-most X-Forwarded-For entry that is not itself
// trusted (entries further left are client-supplied and ignored).
func ClientIP(r *http.Request, trusted []netip.Prefix) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	peer, err := netip.ParseAddr(host)
	if err != nil {
		return host
	}
	peer = peer.Unmap()
	in := func(a netip.Addr) bool {
		for _, p := range trusted {
			if p.Contains(a) {
				return true
			}
		}
		return false
	}
	if !in(peer) {
		return peer.String()
	}
	parts := strings.Split(strings.Join(r.Header.Values("X-Forwarded-For"), ","), ",")
	for i := len(parts) - 1; i >= 0; i-- {
		a, err := netip.ParseAddr(strings.TrimSpace(parts[i]))
		if err != nil {
			return peer.String() // malformed chain: do not guess
		}
		if a = a.Unmap(); !in(a) {
			return a.String()
		}
	}
	return peer.String()
}

// closer records the first close request of a connection.
type closer struct {
	mu     sync.Mutex
	set    bool
	code   websocket.StatusCode
	reason string // a logx tag: it is logged and sent as the close reason
	cancel context.CancelFunc
}

func (c *closer) close(code websocket.StatusCode, reason string) {
	c.mu.Lock()
	if !c.set {
		c.set, c.code, c.reason = true, code, reason
	}
	c.mu.Unlock()
	c.cancel()
}

func (c *closer) get() (websocket.StatusCode, string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if !c.set {
		return websocket.StatusNormalClosure, "closed"
	}
	return c.code, c.reason
}

func errFrame(code rendezvousv1.ErrorCode, retry time.Duration) *pb {
	return &pb{Msg: &rendezvousv1.RendezvousFrame_Error{Error: &rendezvousv1.Error{Code: code, RetryAfterMs: uint32(retry.Milliseconds())}}}
}

func writeFrame(ctx context.Context, c *websocket.Conn, f *pb) error {
	b, err := proto.Marshal(f)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, writeTimeout)
	defer cancel()
	return c.Write(ctx, websocket.MessageBinary, b)
}

var (
	errBinaryOnly = errors.New("non-binary message")
	errBadProto   = errors.New("undecodable frame")
)

func readFrame(ctx context.Context, c *websocket.Conn, timeout time.Duration) (*pb, error) {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	typ, b, err := c.Read(ctx)
	if err != nil {
		return nil, err
	}
	if typ != websocket.MessageBinary {
		return nil, errBinaryOnly
	}
	f := &pb{}
	if err := proto.Unmarshal(b, f); err != nil {
		return nil, errBadProto
	}
	return f, nil
}

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	ip := ClientIP(r, s.c.TrustedProxies)
	ipKey := limits.NormalizeIP(ip)
	lg := s.c.Log
	if s.active.Add(1) > int64(s.c.MaxConnections) {
		s.active.Add(-1)
		w.Header().Set("Retry-After", "30")
		http.Error(w, "server busy", http.StatusServiceUnavailable)
		lg.Warn("ws_connect", logx.Fields{Result: "server_busy", IP: ip})
		return
	}
	defer s.active.Add(-1)
	release, ok := s.c.Limiter.AcquireConn(ipKey)
	if !ok {
		http.Error(w, "too many connections", http.StatusTooManyRequests)
		lg.Warn("ws_connect", logx.Fields{Result: "ip_conn_limit", IP: ip})
		return
	}
	defer release()

	conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{CompressionMode: websocket.CompressionDisabled})
	if err != nil {
		return // Accept already answered the HTTP request
	}
	defer conn.CloseNow()
	conn.SetReadLimit(preAuthMaxFrame)
	s.serve(conn, ip, ipKey)
}

func (s *Server) serve(conn *websocket.Conn, ip, ipKey string) {
	lg := s.c.Log
	root, cancel := context.WithCancel(context.Background())
	defer cancel()
	cl := &closer{cancel: cancel}
	finish := func() { code, reason := cl.get(); _ = conn.Close(code, reason) }
	// bye closes the socket before authentication (nothing else is running yet).
	bye := func(code websocket.StatusCode, reason string) {
		cl.close(code, reason)
		finish()
	}

	ch, err := s.c.Verifier.NewChallenge()
	if err != nil {
		bye(websocket.StatusInternalError, "internal")
		return
	}
	hello := &rendezvousv1.ServerHello{Nonce: ch.Nonce[:], ServerTimeMs: s.c.Clock().UnixMilli(), Protocol: protocolVersion}
	if writeFrame(root, conn, &pb{Msg: &rendezvousv1.RendezvousFrame_ServerHello{ServerHello: hello}}) != nil {
		return
	}

	// ---- authentication: the first frame must be ClientAuth, within the deadline ----
	f, err := readFrame(root, conn, s.c.AuthDeadline)
	if err != nil {
		lg.Info("auth", logx.Fields{Result: "no_auth_frame", Reason: closeTag(err), IP: ip})
		if isProtocolErr(err) {
			s.c.Limiter.RecordPreAuthFailure(ipKey)
			bye(websocket.StatusPolicyViolation, "bad_frame")
		}
		return
	}
	ca := f.GetClientAuth()
	if ca == nil {
		s.c.Limiter.RecordPreAuthFailure(ipKey)
		_ = writeFrame(root, conn, errFrame(rendezvousv1.ErrorCode_ERROR_CODE_NOT_AUTHENTICATED, 0))
		lg.Info("auth", logx.Fields{Result: "not_authenticated", IP: ip})
		bye(websocket.StatusPolicyViolation, "not_authenticated")
		return
	}
	actx, acancel := context.WithTimeout(root, 8*time.Second)
	res, err := s.c.Verifier.Authenticate(actx, ch, auth.ClientAuth{
		SPKI: ca.Spki, DeskID: ca.DeskId, Platform: ca.Platform, AppVersion: ca.AppVersion, Sig: ca.Sig,
	}, ip)
	acancel()
	if err != nil {
		_ = writeFrame(root, conn, errFrame(rendezvousv1.ErrorCode_ERROR_CODE_INTERNAL, 0))
		lg.Warn("auth", logx.Fields{Result: "internal_error", IP: ip})
		bye(websocket.StatusInternalError, "unavailable")
		return
	}
	if !res.OK {
		_ = writeFrame(root, conn, &pb{Msg: &rendezvousv1.RendezvousFrame_AuthResult{AuthResult: &rendezvousv1.AuthResult{Error: res.Code.Proto()}}})
		if res.Code == auth.CodeRateLimited && res.RetryAfter > 0 {
			_ = writeFrame(root, conn, errFrame(rendezvousv1.ErrorCode_ERROR_CODE_RATE_LIMITED, res.RetryAfter))
		}
		lg.Info("auth", logx.Fields{Result: "denied", Reason: res.Reason, IP: ip})
		bye(websocket.StatusPolicyViolation, "auth_failed")
		return
	}

	// ---- authenticated ----
	conn.SetReadLimit(MaxFrame)
	peer := NewPeer(root, res.DeskID, res.Session.KeyFP, append([]byte(nil), ca.Spki...), func(reason string) {
		code := websocket.StatusNormalClosure
		if reason == "shutdown" {
			code = websocket.StatusGoingAway
		}
		cl.close(code, reason)
	})
	// AuthResult and IceConfig go out before the peer becomes reachable.
	peer.Send(&pb{Msg: &rendezvousv1.RendezvousFrame_AuthResult{AuthResult: &rendezvousv1.AuthResult{Ok: true, DeskId: res.DeskID}}})
	peer.Send(s.iceFrame(peer, false)) // never a TURN credential here; the device must ask
	if !s.c.Hub.Register(peer) {
		bye(websocket.StatusGoingAway, "shutdown")
		return
	}
	lg.Info("auth", logx.Fields{Result: "ok", DeskID: res.DeskID, IP: ip})

	wdone := make(chan struct{})
	go s.writer(root, conn, peer, wdone)
	go s.pinger(root, peer)

	s.readLoop(root, conn, peer, res.Session, cl, ip)

	s.c.Hub.Unregister(peer)
	cancel()
	<-wdone
	finish()
	_, why := cl.get()
	lg.Info("disconnect", logx.Fields{Result: "closed", Reason: why, DeskID: res.DeskID, IP: ip})
}

func closeTag(err error) string {
	switch {
	case err == nil:
		return "none"
	case errors.Is(err, context.DeadlineExceeded):
		return "timeout"
	case websocket.CloseStatus(err) == websocket.StatusMessageTooBig:
		return "frame_too_large"
	case errors.Is(err, errBinaryOnly):
		return "text_frame"
	case isProtocolErr(err):
		return "bad_frame"
	}
	return "io"
}

func isProtocolErr(err error) bool {
	return errors.Is(err, errBinaryOnly) || errors.Is(err, errBadProto) ||
		websocket.CloseStatus(err) == websocket.StatusMessageTooBig
}

func (s *Server) iceFrame(p *Peer, wantRelay bool) *pb {
	r := s.c.Ice.ForDevice(p.KeyFP[:], s.c.Clock(), wantRelay)
	cfg := &rendezvousv1.IceConfig{ExpiresAtMs: r.ExpiresAt.UnixMilli()}
	for _, sv := range r.Servers {
		cfg.IceServers = append(cfg.IceServers, &rendezvousv1.IceServer{Urls: sv.URLs, Username: sv.Username, Credential: sv.Credential})
	}
	return &pb{Msg: &rendezvousv1.RendezvousFrame_IceConfig{IceConfig: cfg}}
}

// writer drains the peer queue; after the connection is cancelled it flushes
// what is already queued (e.g. a final Error frame) and exits.
func (s *Server) writer(ctx context.Context, conn *websocket.Conn, p *Peer, done chan<- struct{}) {
	defer close(done)
	var seq uint64
	send := func(f *pb) bool {
		if f.Seq == 0 {
			seq++
			f.Seq = seq
		}
		return writeFrame(context.Background(), conn, f) == nil
	}
	for {
		select {
		case f := <-p.out:
			if !send(f) {
				p.kill("write_failed")
				return
			}
		case <-ctx.Done():
			for {
				select {
				case f := <-p.out:
					if !send(f) {
						return
					}
				default:
					return
				}
			}
		}
	}
}

func (s *Server) pinger(ctx context.Context, p *Peer) {
	t := time.NewTicker(s.c.PingEvery)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			p.Send(&pb{Msg: &rendezvousv1.RendezvousFrame_Ping{Ping: &rendezvousv1.Ping{}}})
		}
	}
}

func (s *Server) readLoop(ctx context.Context, conn *websocket.Conn, p *Peer, sess *auth.Session, cl *closer, ip string) {
	lg := s.c.Log
	bucket := limits.NewFrameBucket(s.c.Clock)
	var lastIce time.Time
	violate := func(reason string, code rendezvousv1.ErrorCode) {
		p.Send(errFrame(code, 0))
		lg.Info("violation", logx.Fields{Result: "closed", Reason: reason, DeskID: p.DeskID, IP: ip})
		cl.close(websocket.StatusPolicyViolation, reason)
	}
	bad := rendezvousv1.ErrorCode_ERROR_CODE_BAD_FRAME
	for ctx.Err() == nil {
		f, err := readFrame(ctx, conn, s.c.IdleTimeout)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			switch {
			case isProtocolErr(err):
				violate(closeTag(err), bad)
			case errors.Is(err, context.DeadlineExceeded):
				cl.close(websocket.StatusGoingAway, "idle")
			default:
				cl.close(websocket.StatusNormalClosure, "closed")
			}
			return
		}
		if !bucket.Allow() {
			violate("frame_rate", rendezvousv1.ErrorCode_ERROR_CODE_RATE_LIMITED)
			return
		}
		switch m := f.Msg.(type) {
		case *rendezvousv1.RendezvousFrame_Ping:
			p.Send(&pb{Msg: &rendezvousv1.RendezvousFrame_Pong{Pong: &rendezvousv1.Pong{}}})
		case *rendezvousv1.RendezvousFrame_Pong:
		case *rendezvousv1.RendezvousFrame_IceConfigRequest:
			if now := s.c.Clock(); now.Sub(lastIce) < iceMinInterval {
				p.Send(errFrame(rendezvousv1.ErrorCode_ERROR_CODE_RATE_LIMITED, iceMinInterval))
			} else {
				lastIce = now
				p.Send(s.iceFrame(p, true))
			}
		case *rendezvousv1.RendezvousFrame_PairSlotCreate:
			if s.c.Hub.SlotCreate(p, m.PairSlotCreate) != nil {
				violate("bad_slot", bad)
				return
			}
		case *rendezvousv1.RendezvousFrame_PairSlotCancel:
			s.c.Hub.SlotCancel(p)
		case *rendezvousv1.RendezvousFrame_OpenPipe:
			if s.c.Hub.OpenPipe(p, f.Seq, m.OpenPipe) != nil {
				violate("bad_open_pipe", bad)
				return
			}
		case *rendezvousv1.RendezvousFrame_PipeDecision:
			s.c.Hub.Decision(p, m.PipeDecision.PipeId, m.PipeDecision.Accept)
		case *rendezvousv1.RendezvousFrame_PipeData:
			if s.c.Hub.Data(ctx, p, m.PipeData.PipeId, m.PipeData.Data) != nil {
				violate("pipe_frame_too_large", bad)
				return
			}
		case *rendezvousv1.RendezvousFrame_PipeClose:
			s.c.Hub.Close(p, m.PipeClose.PipeId)
		case *rendezvousv1.RendezvousFrame_RevokeSelf:
			rctx, rcancel := context.WithTimeout(ctx, 8*time.Second)
			err := s.c.Verifier.RevokeSelf(rctx, sess, m.RevokeSelf.Sig)
			rcancel()
			switch {
			case errors.Is(err, auth.ErrBadSignature):
				violate("bad_revoke", bad)
			case err != nil:
				p.Send(errFrame(rendezvousv1.ErrorCode_ERROR_CODE_INTERNAL, 0))
				cl.close(websocket.StatusInternalError, "unavailable")
			default:
				lg.Info("revoke_self", logx.Fields{Result: "ok", DeskID: p.DeskID, IP: ip})
				cl.close(websocket.StatusNormalClosure, "revoked")
			}
			return
		default:
			// ClientAuth again, server-only frames, or an empty frame.
			violate("unexpected_frame", bad)
			return
		}
	}
}
