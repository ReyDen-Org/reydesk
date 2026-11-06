// Package hub keeps the in-memory state of the rendezvous service (spec 3.3,
// 3.4, 1.5): which authenticated devices are online, their pairing-code slots,
// the pipes between two devices and the pair-request mailbox. Nothing here is
// persisted and nothing here ever touches the network: the websocket layer
// (server.go) feeds it frames from authenticated peers only, so no pipe can be
// opened or relayed before authentication.
//
// Isolation rules enforced here:
//   - a pipe id is 128 random bits and every pipe operation checks that the
//     sender IS one of the two ends; unknown ids and foreign ids get the same
//     answer (PipeClose), so ids are no oracle;
//   - PipeData is only forwarded on an OPEN pipe, between its two ends;
//   - every queue is bounded; a peer that cannot keep up is disconnected, and a
//     pipe whose receiver is slow ends after forwardWait.
//
// Locking: Hub.mu guards all maps and pipe/slot state. Sends to peers made
// while holding it are non-blocking (Peer.trySend) and Peer.kill never takes
// Hub.mu. The Limiter has its own mutex and is only called without Hub.mu.
package hub

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"sync"
	"sync/atomic"
	"time"

	rendezvousv1 "reydesk/rendezvous/gen"
	"reydesk/rendezvous/internal/ids"
	"reydesk/rendezvous/internal/limits"
	"reydesk/rendezvous/internal/logx"
)

type pb = rendezvousv1.RendezvousFrame

// Spec numbers.
const (
	MaxPipeData       = 16 << 10
	MaxPipeBytes      = 1 << 20
	PipeRate          = 64 << 10 // bytes per second sustained
	PipeIdle          = 10 * time.Minute
	PipeMaxLife       = 6 * time.Hour
	MaxPipesPerDevice = 8
	MaxPendingOpens   = 4 // OpenPipe requests one device may have in flight
	SlotAttempts      = 5
	MaxSlotTTL        = 300
	CodeLen           = 6
	SendQueue         = 64
	forwardWait       = 5 * time.Second
)

// ErrProtocol means the peer broke the protocol; the caller closes its socket.
var ErrProtocol = errors.New("hub: protocol violation")

// Config configures a Hub. Zero durations take the spec defaults.
type Config struct {
	Limiter         *limits.Limiter // required
	Log             *logx.Logger    // optional
	Clock           func() time.Time
	MailboxWait     time.Duration // 20 s
	DecisionTimeout time.Duration // 10 s
	Floor           time.Duration // 300 ms
	PipeRate        float64       // bytes/s per pipe, default PipeRate (tests only)
}

type Hub struct {
	cfg     Config
	mu      sync.Mutex
	online  map[string]*Peer
	pipes   map[string]*pipe
	waiters map[string]map[*waiter]struct{}
	closed  bool
}

func New(c Config) *Hub {
	if c.Clock == nil {
		c.Clock = time.Now
	}
	if c.MailboxWait == 0 {
		c.MailboxWait = 20 * time.Second
	}
	if c.DecisionTimeout == 0 {
		c.DecisionTimeout = 10 * time.Second
	}
	if c.PipeRate == 0 {
		c.PipeRate = PipeRate
	}
	if c.Floor == 0 {
		c.Floor = 300 * time.Millisecond
	}
	return &Hub{cfg: c, online: map[string]*Peer{}, pipes: map[string]*pipe{}, waiters: map[string]map[*waiter]struct{}{}}
}

// ---- peers ----

// Peer is one authenticated, connected device.
type Peer struct {
	DeskID string
	KeyFP  [32]byte
	SPKI   []byte

	ctx     context.Context
	out     chan *pb
	kill    func(reason string)
	pending atomic.Int32

	// guarded by Hub.mu
	slot  *slot
	pipes map[string]*pipe
	gone  bool
}

// NewPeer creates a peer. kill must be safe to call from any goroutine, must
// not block and must not call into the Hub.
func NewPeer(ctx context.Context, deskID string, keyFP [32]byte, spki []byte, kill func(reason string)) *Peer {
	return &Peer{
		DeskID: deskID, KeyFP: keyFP, SPKI: spki, ctx: ctx, kill: kill,
		out: make(chan *pb, SendQueue), pipes: map[string]*pipe{},
	}
}

// Out is the queue the connection's writer drains.
func (p *Peer) Out() <-chan *pb { return p.out }

// Send queues a frame without blocking; a full queue disconnects the peer.
func (p *Peer) Send(f *pb) bool {
	select {
	case p.out <- f:
		return true
	default:
		p.kill("slow_consumer")
		return false
	}
}

func (p *Peer) sendWait(ctx context.Context, f *pb, d time.Duration) bool {
	select {
	case p.out <- f:
		return true
	default:
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case p.out <- f:
		return true
	case <-t.C:
		return false
	case <-ctx.Done():
		return false
	case <-p.ctx.Done():
		return false
	}
}

// Register makes p reachable under its Desk ID, replacing (and disconnecting)
// an older connection of the same device, and wakes mailbox waiters.
// It returns false when the hub is shutting down.
func (h *Hub) Register(p *Peer) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.closed {
		return false
	}
	if old := h.online[p.DeskID]; old != nil && old != p {
		old.kill("replaced")
	}
	h.online[p.DeskID] = p
	for w := range h.waiters[p.DeskID] {
		close(w.ch)
	}
	delete(h.waiters, p.DeskID)
	return true
}

// Unregister removes p, its slot and all its pipes. Peers on the other end of
// those pipes are told.
func (h *Hub) Unregister(p *Peer) {
	h.mu.Lock()
	defer h.mu.Unlock()
	p.gone = true
	if h.online[p.DeskID] == p {
		delete(h.online, p.DeskID)
	}
	p.slot = nil
	for _, pl := range snapshot(p.pipes) {
		h.endLocked(pl, p, rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE)
	}
}

// CloseAll disconnects every peer (server shutdown) and refuses new ones.
func (h *Hub) CloseAll() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.closed = true
	for _, p := range h.online {
		p.kill("shutdown")
	}
	for k, set := range h.waiters {
		for w := range set {
			close(w.ch)
		}
		delete(h.waiters, k)
	}
}

// Online reports whether deskID currently has an authenticated connection.
func (h *Hub) Online(deskID string) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.online[deskID] != nil
}

// Counts returns online peers and pipes (tests, metrics).
func (h *Hub) Counts() (peers, pipes int) {
	h.mu.Lock()
	defer h.mu.Unlock()
	return len(h.online), len(h.pipes)
}

func snapshot(m map[string]*pipe) []*pipe {
	out := make([]*pipe, 0, len(m))
	for _, pl := range m {
		out = append(out, pl)
	}
	return out
}

// ---- slots ----

type slot struct {
	hash     [32]byte
	salt     []byte
	expires  time.Time
	attempts int
}

// SlotHash is SHA256(LP(desk_id) LP(code) LP(salt)), the value a host sends in
// PairSlotCreate (spec 2.4 M4).
func SlotHash(deskID, code string, salt []byte) [32]byte {
	h := sha256.New()
	for _, part := range [][]byte{[]byte(deskID), []byte(code), salt} {
		var n [4]byte
		binary.BigEndian.PutUint32(n[:], uint32(len(part)))
		h.Write(n[:])
		h.Write(part)
	}
	var out [32]byte
	copy(out[:], h.Sum(nil))
	return out
}

// SlotCreate installs p's pairing slot (replacing any older one).
func (h *Hub) SlotCreate(p *Peer, m *rendezvousv1.PairSlotCreate) error {
	if len(m.CodeHash) != 32 || len(m.Salt) != 16 || m.TtlS == 0 || m.TtlS > MaxSlotTTL {
		return ErrProtocol
	}
	s := &slot{salt: append([]byte(nil), m.Salt...), attempts: SlotAttempts,
		expires: h.cfg.Clock().Add(time.Duration(m.TtlS) * time.Second)}
	copy(s.hash[:], m.CodeHash)
	h.mu.Lock()
	defer h.mu.Unlock()
	p.slot = s
	p.Send(slotStatus(SlotAttempts, false))
	return nil
}

// SlotCancel drops p's slot.
func (h *Hub) SlotCancel(p *Peer) {
	h.mu.Lock()
	p.slot = nil
	h.mu.Unlock()
}

func slotStatus(left int, expired bool) *pb {
	return &pb{Msg: &rendezvousv1.RendezvousFrame_PairSlotStatus{PairSlotStatus: &rendezvousv1.PairSlotStatus{AttemptsLeft: uint32(left), Expired: expired}}}
}

type codeResult int

const (
	codeOK codeResult = iota
	codeNoSlot
	codeWrong
)

// checkCodeLocked verifies code against host's slot in constant time.
// A correct code consumes the slot (single use); a wrong one costs an attempt
// and the 5th destroys the slot.
func (h *Hub) checkCodeLocked(host *Peer, code string) codeResult {
	s := host.slot
	if s == nil {
		return codeNoSlot
	}
	if !h.cfg.Clock().Before(s.expires) {
		host.slot = nil
		host.Send(slotStatus(0, true))
		return codeNoSlot
	}
	got := SlotHash(host.DeskID, code, s.salt)
	if subtle.ConstantTimeCompare(got[:], s.hash[:]) == 1 {
		host.slot = nil
		return codeOK
	}
	s.attempts--
	if s.attempts <= 0 {
		host.slot = nil
		host.Send(slotStatus(0, true))
	} else {
		host.Send(slotStatus(s.attempts, false))
	}
	return codeWrong
}

// ---- pipes ----

const (
	statePending = iota
	stateOpen
)

type pipe struct {
	id          string
	req, host   *Peer
	reqSeq      uint64
	state       int
	decided     bool
	dead        bool
	created     time.Time
	started     time.Time // wall clock start of the open, for the uniform-answer floor
	lastActive  time.Time
	total       int
	tokens      float64
	tokensAt    time.Time
	timer       *time.Timer
	release     func()
	declineWait *time.Timer
}

// reserve takes n bytes from the pipe's rate bucket and returns how long the
// sender must wait before forwarding (backpressure, never a drop).
func (pl *pipe) reserve(n int, now time.Time, rate float64) time.Duration {
	pl.tokens += now.Sub(pl.tokensAt).Seconds() * rate
	if pl.tokens > rate {
		pl.tokens = rate // burst = one second of rate
	}
	pl.tokensAt = now
	pl.tokens -= float64(n)
	if pl.tokens >= 0 {
		return 0
	}
	return time.Duration(-pl.tokens / rate * float64(time.Second))
}

func newID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		panic("hub: no randomness: " + err.Error())
	}
	return hex.EncodeToString(b[:])
}

func failed(seq uint64, r rendezvousv1.PipeFailReason) *pb {
	return &pb{Seq: seq, Msg: &rendezvousv1.RendezvousFrame_PipeFailed{PipeFailed: &rendezvousv1.PipeFailed{Reason: r}}}
}

func closeFrame(id string) *pb {
	return &pb{Msg: &rendezvousv1.RendezvousFrame_PipeClose{PipeClose: &rendezvousv1.PipeClose{PipeId: id}}}
}

// endLocked removes a pipe and tells the ends other than from (nil = both).
// A pending pipe fails its requester with reason (it does not know the id yet).
func (h *Hub) endLocked(pl *pipe, from *Peer, reason rendezvousv1.PipeFailReason) {
	if pl.dead {
		return
	}
	pl.dead = true
	delete(h.pipes, pl.id)
	delete(pl.req.pipes, pl.id)
	delete(pl.host.pipes, pl.id)
	if pl.timer != nil {
		pl.timer.Stop()
	}
	if pl.declineWait != nil {
		pl.declineWait.Stop()
	}
	if pl.release != nil {
		pl.release()
		pl.release = nil
	}
	for _, end := range [2]*Peer{pl.req, pl.host} {
		if end == from {
			continue
		}
		if pl.state == statePending && end == pl.req {
			end.Send(failed(pl.reqSeq, reason))
		} else {
			end.Send(closeFrame(pl.id))
		}
	}
}

type waiter struct{ ch chan struct{} }

func (h *Hub) event(name string, f logx.Fields) {
	if h.cfg.Log != nil {
		h.cfg.Log.Info(name, f)
	}
}

// OpenPipe validates an OpenPipe frame and starts the (asynchronous) open. It
// returns ErrProtocol for a malformed request; every other outcome is reported
// to the requester as PipeFailed / PipeOpened carrying seq of the request.
func (h *Hub) OpenPipe(p *Peer, seq uint64, m *rendezvousv1.OpenPipe) error {
	switch m.Purpose {
	case rendezvousv1.PipePurpose_PIPE_PURPOSE_PAIR_CODE:
		if len(m.Code) != CodeLen || !asciiDigits(m.Code) {
			return ErrProtocol
		}
	case rendezvousv1.PipePurpose_PIPE_PURPOSE_PAIR_QR, rendezvousv1.PipePurpose_PIPE_PURPOSE_RECONNECT:
		if m.Code != "" {
			return ErrProtocol
		}
	default:
		return ErrProtocol
	}
	if len(m.TargetDeskId) > 32 {
		return ErrProtocol
	}
	if p.pending.Add(1) > MaxPendingOpens {
		p.pending.Add(-1)
		p.Send(failed(seq, rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_RATE_LIMITED))
		return nil
	}
	go func() {
		defer p.pending.Add(-1)
		h.openPipe(p, seq, m.TargetDeskId, m.Purpose, m.Code)
	}()
	return nil
}

func asciiDigits(s string) bool {
	for i := 0; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return false
		}
	}
	return true
}

func sleepCtx(ctx context.Context, d time.Duration) bool {
	if d <= 0 {
		return ctx.Err() == nil
	}
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-t.C:
		return true
	case <-ctx.Done():
		return false
	}
}

func (h *Hub) openPipe(p *Peer, seq uint64, target string, purpose rendezvousv1.PipePurpose, code string) {
	R := rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_RATE_LIMITED
	U := rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE
	B := rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_BAD_CODE
	start := time.Now()
	ev := func(res string) { h.event("pipe_open", logx.Fields{Result: res, DeskID: p.DeskID}) }
	// answer sends a failure; floor delays it so unknown/offline/declined/no-slot look alike.
	answer := func(r rendezvousv1.PipeFailReason, floor bool, res string) {
		if floor && !sleepCtx(p.ctx, h.cfg.Floor-time.Since(start)) {
			return
		}
		ev(res)
		p.Send(failed(seq, r))
	}
	if !ids.IsCanonical(target) || target == p.DeskID {
		answer(U, true, "unavailable")
		return
	}
	if ok, retry := h.cfg.Limiter.AllowPipeOpen(p.DeskID, target); !ok {
		p.Send(&pb{Msg: &rendezvousv1.RendezvousFrame_Error{Error: &rendezvousv1.Error{
			Code: rendezvousv1.ErrorCode_ERROR_CODE_RATE_LIMITED, RetryAfterMs: uint32(retry.Milliseconds())}}})
		answer(R, false, "rate_limited")
		return
	}
	if ok, _ := h.cfg.Limiter.AllowTargetOpen(target); !ok {
		answer(R, false, "target_rate_limited")
		return
	}
	release, ok := h.cfg.Limiter.AcquireWaiting(target)
	if !ok {
		answer(R, false, "target_busy")
		return
	}
	// From here until the pipe takes over `release`, every exit is a failed open.
	owned := false
	defer func() {
		if !owned {
			release()
			h.cfg.Limiter.RecordPipeFailure(p.DeskID, target)
		}
	}()

	deadline := time.NewTimer(h.cfg.MailboxWait)
	defer deadline.Stop()
	for {
		h.mu.Lock()
		host := h.online[target]
		if h.closed || p.gone {
			h.mu.Unlock()
			return
		}
		if host == nil {
			w := &waiter{ch: make(chan struct{})}
			if h.waiters[target] == nil {
				h.waiters[target] = map[*waiter]struct{}{}
			}
			h.waiters[target][w] = struct{}{}
			h.mu.Unlock()
			select {
			case <-w.ch:
				continue
			case <-deadline.C:
				h.dropWaiter(target, w)
				answer(U, false, "unavailable")
				return
			case <-p.ctx.Done():
				h.dropWaiter(target, w)
				return
			}
		}
		// Host online: code check, caps, create the pending pipe, all atomically.
		var verdict rendezvousv1.PipeFailReason
		fail := ""
		if purpose == rendezvousv1.PipePurpose_PIPE_PURPOSE_PAIR_CODE {
			switch h.checkCodeLocked(host, code) {
			case codeNoSlot:
				verdict, fail = U, "unavailable"
			case codeWrong:
				verdict, fail = B, "bad_code"
			}
		}
		if fail == "" && len(p.pipes) >= MaxPipesPerDevice {
			verdict, fail = R, "pipe_cap"
		}
		if fail == "" && len(host.pipes) >= MaxPipesPerDevice {
			verdict, fail = U, "unavailable"
		}
		if fail != "" {
			h.mu.Unlock()
			answer(verdict, fail != "pipe_cap", fail)
			return
		}
		now := h.cfg.Clock()
		pl := &pipe{id: newID(), req: p, host: host, reqSeq: seq, state: statePending, started: start,
			created: now, lastActive: now, tokens: h.cfg.PipeRate, tokensAt: now, release: release}
		h.pipes[pl.id] = pl
		p.pipes[pl.id] = pl
		host.pipes[pl.id] = pl
		pl.timer = time.AfterFunc(h.cfg.DecisionTimeout, func() { h.decisionTimeout(pl) })
		owned = true
		ok := host.Send(&pb{Msg: &rendezvousv1.RendezvousFrame_IncomingPipe{IncomingPipe: &rendezvousv1.IncomingPipe{
			PipeId: pl.id, RequesterDeskId: p.DeskID, RequesterFp: append([]byte(nil), p.KeyFP[:]...), Purpose: purpose}}})
		if !ok {
			pl.decided = true
			h.endLocked(pl, host, U)
		}
		h.mu.Unlock()
		ev("incoming")
		if !ok {
			h.cfg.Limiter.RecordPipeFailure(p.DeskID, target)
		}
		return
	}
}

func (h *Hub) dropWaiter(target string, w *waiter) {
	h.mu.Lock()
	delete(h.waiters[target], w)
	if len(h.waiters[target]) == 0 {
		delete(h.waiters, target)
	}
	h.mu.Unlock()
}

func (h *Hub) decisionTimeout(pl *pipe) {
	h.mu.Lock()
	if pl.decided || pl.dead {
		h.mu.Unlock()
		return
	}
	pl.decided = true
	req, host := pl.req.DeskID, pl.host.DeskID
	h.endLocked(pl, nil, rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_TIMEOUT)
	h.mu.Unlock()
	h.cfg.Limiter.RecordPipeFailure(req, host)
	h.event("pipe_open", logx.Fields{Result: "timeout", DeskID: req})
}

// Decision handles the host's PipeDecision. Unknown ids and ids the sender is
// not the host of get the same PipeClose reply.
func (h *Hub) Decision(host *Peer, pipeID string, accept bool) {
	h.mu.Lock()
	pl := h.pipes[pipeID]
	if pl == nil || pl.host != host || pl.state != statePending || pl.decided {
		h.mu.Unlock()
		host.Send(closeFrame(pipeID))
		return
	}
	pl.decided = true
	if pl.timer != nil {
		pl.timer.Stop()
	}
	if !accept {
		// Same answer and delay as unknown/offline: UNAVAILABLE after the floor.
		wait := h.cfg.Floor - time.Since(pl.started)
		reqID, hostID := pl.req.DeskID, pl.host.DeskID
		fire := func() {
			h.mu.Lock()
			h.endLocked(pl, host, rendezvousv1.PipeFailReason_PIPE_FAIL_REASON_UNAVAILABLE)
			h.mu.Unlock()
			h.cfg.Limiter.RecordPipeFailure(reqID, hostID)
			h.event("pipe_open", logx.Fields{Result: "declined", DeskID: reqID})
		}
		if wait <= 0 {
			h.mu.Unlock()
			fire()
			return
		}
		pl.declineWait = time.AfterFunc(wait, fire)
		h.mu.Unlock()
		return
	}
	pl.state = stateOpen
	pl.lastActive = h.cfg.Clock()
	if pl.release != nil {
		pl.release()
		pl.release = nil
	}
	pl.req.Send(&pb{Seq: pl.reqSeq, Msg: &rendezvousv1.RendezvousFrame_PipeOpened{PipeOpened: &rendezvousv1.PipeOpened{PipeId: pl.id, PeerSpki: host.SPKI}}})
	host.Send(&pb{Msg: &rendezvousv1.RendezvousFrame_PipeOpened{PipeOpened: &rendezvousv1.PipeOpened{PipeId: pl.id, PeerSpki: pl.req.SPKI}}})
	reqID := pl.req.DeskID
	h.mu.Unlock()
	h.event("pipe_open", logx.Fields{Result: "ok", DeskID: reqID})
}

// Data forwards bytes from p to the other end of an open pipe. It blocks the
// caller (the sender's read loop) to enforce the rate and the receiver's queue,
// which is the backpressure. Returns ErrProtocol for an oversize frame.
func (h *Hub) Data(ctx context.Context, p *Peer, pipeID string, data []byte) error {
	if len(data) > MaxPipeData {
		return ErrProtocol
	}
	h.mu.Lock()
	pl := h.pipes[pipeID]
	if pl == nil || pl.state != stateOpen || (pl.req != p && pl.host != p) {
		h.mu.Unlock()
		p.Send(closeFrame(pipeID))
		return nil
	}
	now := h.cfg.Clock()
	if pl.total+len(data) > MaxPipeBytes {
		h.endLocked(pl, nil, 0)
		h.mu.Unlock()
		h.event("pipe_end", logx.Fields{Result: "byte_cap", DeskID: p.DeskID})
		return nil
	}
	pl.total += len(data)
	pl.lastActive = now
	delay := pl.reserve(len(data), now, h.cfg.PipeRate)
	other := pl.host
	if p == pl.host {
		other = pl.req
	}
	h.mu.Unlock()

	if !sleepCtx(ctx, delay) {
		return nil
	}
	f := &pb{Msg: &rendezvousv1.RendezvousFrame_PipeData{PipeData: &rendezvousv1.PipeData{PipeId: pipeID, Data: data}}}
	if !other.sendWait(ctx, f, forwardWait) && ctx.Err() == nil {
		h.mu.Lock()
		h.endLocked(pl, nil, 0)
		h.mu.Unlock()
		h.event("pipe_end", logx.Fields{Result: "receiver_slow", DeskID: p.DeskID})
	}
	return nil
}

// Close ends a pipe at the request of one of its ends; the other end is told.
func (h *Hub) Close(p *Peer, pipeID string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if pl := h.pipes[pipeID]; pl != nil && (pl.req == p || pl.host == p) && pl.state == stateOpen {
		h.endLocked(pl, p, 0)
	}
}

// Sweep ends idle/overlong pipes and expires slots. Run it every few seconds.
func (h *Hub) Sweep() {
	h.mu.Lock()
	defer h.mu.Unlock()
	now := h.cfg.Clock()
	for _, pl := range snapshot(h.pipes) {
		if pl.state == stateOpen && (now.Sub(pl.lastActive) >= PipeIdle || now.Sub(pl.created) >= PipeMaxLife) {
			h.endLocked(pl, nil, 0)
		}
	}
	for _, p := range h.online {
		if p.slot != nil && !now.Before(p.slot.expires) {
			p.slot = nil
			p.Send(slotStatus(0, true))
		}
	}
}

// Janitor runs Sweep every interval until ctx ends.
func (h *Hub) Janitor(ctx context.Context, interval time.Duration) {
	t := time.NewTicker(interval)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			h.Sweep()
		}
	}
}
