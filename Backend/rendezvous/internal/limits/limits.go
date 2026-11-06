// Package limits holds the in-memory rate limits of spec 1.5 and 3.5 (single
// instance, no persistence). All methods are safe for concurrent use. Keys are
// opaque strings chosen by the caller: a normalised IP (NormalizeIP), a Desk ID
// or a key fingerprint hex for devices, a canonical Desk ID for targets.
package limits

import (
	"net/netip"
	"sync"
	"time"
)

// Spec 1.5 numbers.
const (
	RegPerHour = 20
	RegPerDay  = 100
	// Failures that carry a valid signature are counted per key (and per IP+Desk
	// ID), never per address: 5 in a minute block that key for 10 minutes.
	AuthFailMax    = 5
	AuthFailWindow = time.Minute
	AuthFailBlock  = 10 * time.Minute
	// Failures before a valid signature (garbage, wrong first frame, stale
	// challenge) draw from one soft token bucket per IP: burst 20, +1 per 6 s.
	// An empty bucket is answered in-protocol with RATE_LIMITED, never HTTP 429.
	PreAuthBurst     = 20
	PreAuthRefill    = 6 * time.Second
	PipeDistinctMax  = 10 // budget units per device per PipeWindow
	PipeWindow       = 10 * time.Minute
	PipePerDayMax    = 60
	PipeDeviceBlock  = time.Hour
	PipeTargetPerMin = 20
	PipeWaitingMax   = 4
	// FailWeight is what an unavailable/declined open costs (a normal open costs 1).
	FailWeight = 2

	ConnsPerIP = 200
	FrameRate  = 50  // frames per second per connection
	FrameBurst = 100 // burst per connection
	day        = 24 * time.Hour
)

// Clock returns the current time; tests inject a fake.
type Clock func() time.Time

// NormalizeIP maps an address to its rate-limit key: IPv4 as is, IPv6 to its /64
// (a single home network owns the whole /64). Unparseable input is returned
// unchanged so it still gets limited as one bucket.
func NormalizeIP(ip string) string {
	a, err := netip.ParseAddr(ip)
	if err != nil {
		return ip
	}
	a = a.Unmap()
	if a.Is6() {
		p, _ := a.Prefix(64)
		return p.String()
	}
	return a.String()
}

type event struct {
	at     time.Time
	target string
	w      int // weight in the 10 minute distinct-target budget
	dw     int // weight in the per-day count (pipes only)
}

type Limiter struct {
	mu    sync.Mutex
	clock Clock

	regs      map[string][]event
	authFails map[string][]event
	ipBlock   map[string]time.Time // per-key blocks (fingerprint, ip+desk id)
	preAuth   map[string]*preAuthState

	pipes       map[string][]event // per device: opens, last 24h
	devBlock    map[string]time.Time
	targetOpens map[string][]event
	waiting     map[string]int
	conns       map[string]int
}

// New returns a Limiter using clock (time.Now when nil).
func New(clock Clock) *Limiter {
	if clock == nil {
		clock = time.Now
	}
	return &Limiter{
		clock:       clock,
		regs:        map[string][]event{},
		authFails:   map[string][]event{},
		ipBlock:     map[string]time.Time{},
		preAuth:     map[string]*preAuthState{},
		pipes:       map[string][]event{},
		devBlock:    map[string]time.Time{},
		targetOpens: map[string][]event{},
		waiting:     map[string]int{},
		conns:       map[string]int{},
	}
}

func prune(ev []event, now time.Time, keep time.Duration) []event {
	i := 0
	for i < len(ev) && now.Sub(ev[i].at) >= keep {
		i++
	}
	return ev[i:]
}

func sum(ev []event, now time.Time, within time.Duration) int {
	n := 0
	for _, e := range ev {
		if now.Sub(e.at) < within {
			n += e.w
		}
	}
	return n
}

func dayUnits(ev []event) int {
	n := 0
	for _, e := range ev {
		n += e.dw
	}
	return n
}

func oldestWithin(ev []event, now time.Time, within time.Duration) time.Time {
	for _, e := range ev {
		if now.Sub(e.at) < within {
			return e.at
		}
	}
	return now
}

// ---- auth failures ----

type preAuthState struct {
	tokens float64
	last   time.Time
}

func (l *Limiter) preAuthLocked(ip string, now time.Time) *preAuthState {
	st, ok := l.preAuth[ip]
	if !ok {
		st = &preAuthState{tokens: PreAuthBurst, last: now}
		l.preAuth[ip] = st
		return st
	}
	if d := now.Sub(st.last); d > 0 {
		st.tokens += float64(d) / float64(PreAuthRefill)
		if st.tokens > PreAuthBurst {
			st.tokens = PreAuthBurst
		}
	}
	st.last = now
	return st
}

// PreAuthBlocked reports whether ip has used up its pre-signature failure
// bucket and how long until one token is back. It is a throttle, not a ban.
func (l *Limiter) PreAuthBlocked(ip string) (bool, time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	st := l.preAuthLocked(ip, l.clock())
	if st.tokens >= 1 {
		return false, 0
	}
	return true, time.Duration((1 - st.tokens) * float64(PreAuthRefill))
}

// RecordPreAuthFailure charges one failure that happened before any valid
// signature (garbage frame, wrong first frame, stale challenge). It returns
// true when the bucket is now empty.
func (l *Limiter) RecordPreAuthFailure(ip string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	st := l.preAuthLocked(ip, l.clock())
	st.tokens--
	if st.tokens < 0 {
		st.tokens = 0
	}
	return st.tokens < 1
}

// KeyBlocked reports whether key (a key fingerprint, or ip+desk id) is in its
// 10 minute block and for how long.
func (l *Limiter) KeyBlocked(key string) (bool, time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	if until, ok := l.ipBlock[key]; ok {
		if now.Before(until) {
			return true, until.Sub(now)
		}
		delete(l.ipBlock, key)
	}
	return false, 0
}

// RecordKeyFailure counts one failed authentication that carried a valid
// signature. The 5th inside a minute starts the 10 minute block of that key
// only (it returns true when that happens).
func (l *Limiter) RecordKeyFailure(key string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	ev := prune(l.authFails[key], now, AuthFailWindow)
	ev = append(ev, event{at: now, w: 1})
	l.authFails[key] = ev
	if len(ev) >= AuthFailMax {
		l.ipBlock[key] = now.Add(AuthFailBlock)
		delete(l.authFails, key)
		return true
	}
	return false
}

// ---- IP: registrations ----

// AllowRegistration charges one new registration for ip against 20/hour and
// 100/day. On refusal nothing is charged and retryAfter says when a slot frees.
func (l *Limiter) AllowRegistration(ip string) (ok bool, retryAfter time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	ev := prune(l.regs[ip], now, day)
	if sum(ev, now, time.Hour) >= RegPerHour {
		l.regs[ip] = ev
		return false, oldestWithin(ev, now, time.Hour).Add(time.Hour).Sub(now)
	}
	if sum(ev, now, day) >= RegPerDay {
		l.regs[ip] = ev
		return false, ev[0].at.Add(day).Sub(now)
	}
	l.regs[ip] = append(ev, event{at: now, w: 1})
	return true, 0
}

// ---- devices: pipe opens ----

// DeviceBlocked reports whether the device key is in its 1 hour block.
func (l *Limiter) DeviceBlocked(device string) (bool, time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.deviceBlockedLocked(device, l.clock())
}

func (l *Limiter) deviceBlockedLocked(device string, now time.Time) (bool, time.Duration) {
	if until, ok := l.devBlock[device]; ok {
		if now.Before(until) {
			return true, until.Sub(now)
		}
		delete(l.devBlock, device)
	}
	return false, 0
}

// windowUnits is the 10 minute budget: each distinct target counts its largest
// weight (1 for an open, 2 once a failure was recorded), repeats are free.
func windowUnits(ev []event, now time.Time) (units int, byTarget map[string]int) {
	byTarget = map[string]int{}
	for _, e := range ev {
		if now.Sub(e.at) < PipeWindow && e.w > byTarget[e.target] {
			byTarget[e.target] = e.w
		}
	}
	for _, w := range byTarget {
		units += w
	}
	return
}

// AllowPipeOpen charges one open from device to target: at most 10 distinct
// targets per 10 minutes and 60 opens per day. Exceeding either blocks the
// device for 1 hour (returned as retryAfter). A refused open charges nothing.
func (l *Limiter) AllowPipeOpen(device, target string) (ok bool, retryAfter time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	if blocked, d := l.deviceBlockedLocked(device, now); blocked {
		return false, d
	}
	ev := prune(l.pipes[device], now, day)
	l.pipes[device] = ev
	units, by := windowUnits(ev, now)
	_, seen := by[target]
	if (!seen && units+1 > PipeDistinctMax) || dayUnits(ev)+1 > PipePerDayMax {
		l.devBlock[device] = now.Add(PipeDeviceBlock)
		return false, PipeDeviceBlock
	}
	l.pipes[device] = append(ev, event{at: now, target: target, w: 1, dw: 1})
	return true, 0
}

// RecordPipeFailure adds the extra cost of an open that ended unknown, offline,
// revoked or declined, so such opens count 2 in total. It may start the block.
func (l *Limiter) RecordPipeFailure(device, target string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	ev := append(prune(l.pipes[device], now, day), event{at: now, target: target, w: FailWeight, dw: FailWeight - 1})
	l.pipes[device] = ev
	units, _ := windowUnits(ev, now)
	if units > PipeDistinctMax || dayUnits(ev) > PipePerDayMax {
		l.devBlock[device] = now.Add(PipeDeviceBlock)
	}
}

// ---- targets ----

// AllowTargetOpen charges one open against the target's 20 per minute.
func (l *Limiter) AllowTargetOpen(target string) (ok bool, retryAfter time.Duration) {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	ev := prune(l.targetOpens[target], now, time.Minute)
	if len(ev) >= PipeTargetPerMin {
		l.targetOpens[target] = ev
		return false, ev[0].at.Add(time.Minute).Sub(now)
	}
	l.targetOpens[target] = append(ev, event{at: now, w: 1})
	return true, 0
}

// AcquireWaiting reserves one of the 4 "waiting for host decision" places of
// target. The caller must call the returned release exactly once (safe to repeat).
func (l *Limiter) AcquireWaiting(target string) (release func(), ok bool) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.waiting[target] >= PipeWaitingMax {
		return nil, false
	}
	l.waiting[target]++
	var once sync.Once
	return func() {
		once.Do(func() {
			l.mu.Lock()
			defer l.mu.Unlock()
			if l.waiting[target]--; l.waiting[target] <= 0 {
				delete(l.waiting, target)
			}
		})
	}, true
}

// ---- connections ----

// AcquireConn counts one open connection for ip (max 200). Call release on close.
func (l *Limiter) AcquireConn(ip string) (release func(), ok bool) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.conns[ip] >= ConnsPerIP {
		return nil, false
	}
	l.conns[ip]++
	var once sync.Once
	return func() {
		once.Do(func() {
			l.mu.Lock()
			defer l.mu.Unlock()
			if l.conns[ip]--; l.conns[ip] <= 0 {
				delete(l.conns, ip)
			}
		})
	}, true
}

// Sweep drops expired state so memory stays bounded; call it every minute or so.
func (l *Limiter) Sweep() {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := l.clock()
	for _, m := range []map[string][]event{l.regs, l.pipes} {
		for k, ev := range m {
			if ev = prune(ev, now, day); len(ev) == 0 {
				delete(m, k)
			} else {
				m[k] = ev
			}
		}
	}
	for k, ev := range l.authFails {
		if ev = prune(ev, now, AuthFailWindow); len(ev) == 0 {
			delete(l.authFails, k)
		} else {
			l.authFails[k] = ev
		}
	}
	for k, ev := range l.targetOpens {
		if ev = prune(ev, now, time.Minute); len(ev) == 0 {
			delete(l.targetOpens, k)
		} else {
			l.targetOpens[k] = ev
		}
	}
	for k, st := range l.preAuth {
		if st.tokens+float64(now.Sub(st.last))/float64(PreAuthRefill) >= PreAuthBurst {
			delete(l.preAuth, k)
		}
	}
	for _, m := range []map[string]time.Time{l.ipBlock, l.devBlock} {
		for k, until := range m {
			if !now.Before(until) {
				delete(m, k)
			}
		}
	}
}

// Bucket is a token bucket (per-connection frame limit: 50/s, burst 100).
type Bucket struct {
	mu     sync.Mutex
	clock  Clock
	rate   float64
	burst  float64
	tokens float64
	last   time.Time
}

// NewFrameBucket returns the per-connection frame limiter (50/s, burst 100).
func NewFrameBucket(clock Clock) *Bucket { return NewBucket(FrameRate, FrameBurst, clock) }

// NewBucket returns a full bucket refilling rate tokens per second up to burst.
func NewBucket(rate, burst float64, clock Clock) *Bucket {
	if clock == nil {
		clock = time.Now
	}
	return &Bucket{clock: clock, rate: rate, burst: burst, tokens: burst, last: clock()}
}

// Allow takes one token; false means the connection is over its limit.
func (b *Bucket) Allow() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	now := b.clock()
	b.tokens += now.Sub(b.last).Seconds() * b.rate
	if b.tokens > b.burst {
		b.tokens = b.burst
	}
	b.last = now
	if b.tokens < 1 {
		return false
	}
	b.tokens--
	return true
}
