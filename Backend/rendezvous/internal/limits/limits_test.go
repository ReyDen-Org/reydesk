package limits

import (
	"fmt"
	"testing"
	"time"
)

type fakeClock struct{ t time.Time }

func (c *fakeClock) now() time.Time          { return c.t }
func (c *fakeClock) advance(d time.Duration) { c.t = c.t.Add(d) }

func newTest() (*Limiter, *fakeClock) {
	c := &fakeClock{t: time.Date(2026, 11, 5, 12, 0, 0, 0, time.UTC)}
	return New(c.now), c
}

func TestNormalizeIP(t *testing.T) {
	cases := map[string]string{
		"203.0.113.9":          "203.0.113.9",
		"::ffff:203.0.113.9":   "203.0.113.9",
		"2001:db8:1:2:3:4:5:6": "2001:db8:1:2::/64",
		"2001:db8:1:2::ffff":   "2001:db8:1:2::/64",
		"not-an-ip":            "not-an-ip",
	}
	for in, want := range cases {
		if got := NormalizeIP(in); got != want {
			t.Errorf("%s -> %s want %s", in, got, want)
		}
	}
}

func TestRegistrationHourlyAndDaily(t *testing.T) {
	l, c := newTest()
	for i := 0; i < RegPerHour; i++ {
		if ok, _ := l.AllowRegistration("1.1.1.1"); !ok {
			t.Fatalf("registration %d refused", i)
		}
	}
	ok, retry := l.AllowRegistration("1.1.1.1")
	if ok || retry <= 0 || retry > time.Hour {
		t.Fatalf("21st: ok=%v retry=%v", ok, retry)
	}
	if ok, _ := l.AllowRegistration("2.2.2.2"); !ok {
		t.Fatal("other IP must be unaffected")
	}
	// 5 hourly batches of 20 reach the daily cap of 100.
	for batch := 1; batch < 5; batch++ {
		c.advance(time.Hour + time.Second)
		for i := 0; i < RegPerHour; i++ {
			if ok, _ := l.AllowRegistration("1.1.1.1"); !ok {
				t.Fatalf("batch %d reg %d refused", batch, i)
			}
		}
	}
	c.advance(time.Hour + time.Second)
	if ok, retry := l.AllowRegistration("1.1.1.1"); ok || retry <= 0 {
		t.Fatalf("daily cap not enforced: ok=%v retry=%v", ok, retry)
	}
	c.advance(day)
	if ok, _ := l.AllowRegistration("1.1.1.1"); !ok {
		t.Fatal("must recover after a day")
	}
}

func TestAuthFailureBlock(t *testing.T) {
	l, c := newTest()
	for i := 0; i < AuthFailMax-1; i++ {
		if l.RecordKeyFailure("9.9.9.9") {
			t.Fatal("blocked too early")
		}
	}
	if blocked, _ := l.KeyBlocked("9.9.9.9"); blocked {
		t.Fatal("blocked after 4")
	}
	if !l.RecordKeyFailure("9.9.9.9") {
		t.Fatal("5th failure must block")
	}
	blocked, d := l.KeyBlocked("9.9.9.9")
	if !blocked || d != AuthFailBlock {
		t.Fatalf("blocked=%v d=%v", blocked, d)
	}
	c.advance(AuthFailBlock + time.Second)
	if blocked, _ := l.KeyBlocked("9.9.9.9"); blocked {
		t.Fatal("block must expire")
	}
}

func TestAuthFailuresOutsideWindowDontAccumulate(t *testing.T) {
	l, c := newTest()
	for i := 0; i < 20; i++ {
		l.RecordKeyFailure("9.9.9.9")
		c.advance(20 * time.Second)
		if i%5 == 4 { // 4 failures per 80s never fit 5 in 60s
			c.advance(0)
		}
	}
	// 1 failure per 20s = 3 per minute, never 5.
	if blocked, _ := l.KeyBlocked("9.9.9.9"); blocked {
		t.Fatal("slow failures must not block")
	}
}

func TestPipeDistinctTargetsBlockDevice(t *testing.T) {
	l, c := newTest()
	for i := 0; i < PipeDistinctMax; i++ {
		if ok, _ := l.AllowPipeOpen("dev", fmt.Sprintf("T%02d", i)); !ok {
			t.Fatalf("target %d refused", i)
		}
	}
	// repeats of a known target are free for the distinct budget
	if ok, _ := l.AllowPipeOpen("dev", "T00"); !ok {
		t.Fatal("repeat target refused")
	}
	ok, retry := l.AllowPipeOpen("dev", "T-new")
	if ok || retry != PipeDeviceBlock {
		t.Fatalf("11th distinct: ok=%v retry=%v", ok, retry)
	}
	if blocked, _ := l.DeviceBlocked("dev"); !blocked {
		t.Fatal("device must be blocked")
	}
	if ok, _ := l.AllowPipeOpen("dev", "T00"); ok {
		t.Fatal("blocked device must be refused even for a known target")
	}
	if ok, _ := l.AllowPipeOpen("other", "T-new"); !ok {
		t.Fatal("other device unaffected")
	}
	c.advance(PipeDeviceBlock + time.Second)
	if ok, _ := l.AllowPipeOpen("dev", "T-new"); !ok {
		t.Fatal("must recover after the 1 hour block")
	}
}

func TestPipeFailuresCountTwice(t *testing.T) {
	l, _ := newTest()
	for i := 0; i < 5; i++ { // 5 targets, each failing = 10 units
		tgt := fmt.Sprintf("T%d", i)
		if ok, _ := l.AllowPipeOpen("dev", tgt); !ok {
			t.Fatalf("open %d refused", i)
		}
		l.RecordPipeFailure("dev", tgt)
	}
	if blocked, _ := l.DeviceBlocked("dev"); blocked {
		t.Fatal("exactly 10 units must still be allowed")
	}
	if ok, _ := l.AllowPipeOpen("dev", "T-extra"); ok {
		t.Fatal("6th failing-weight target must exceed the budget")
	}
}

func TestPipeDailyCap(t *testing.T) {
	l, c := newTest()
	n := 0
	for ; n < PipePerDayMax; n++ {
		if n%9 == 0 && n > 0 {
			c.advance(PipeWindow + time.Second) // keep the 10 minute budget out of the way
		}
		if ok, _ := l.AllowPipeOpen("dev", "same"); !ok {
			t.Fatalf("open %d refused", n)
		}
	}
	if ok, retry := l.AllowPipeOpen("dev", "same"); ok || retry != PipeDeviceBlock {
		t.Fatalf("61st open: ok=%v retry=%v", ok, retry)
	}
}

func TestTargetPerMinute(t *testing.T) {
	l, c := newTest()
	for i := 0; i < PipeTargetPerMin; i++ {
		if ok, _ := l.AllowTargetOpen("host"); !ok {
			t.Fatalf("open %d refused", i)
		}
	}
	if ok, retry := l.AllowTargetOpen("host"); ok || retry <= 0 {
		t.Fatalf("21st: ok=%v retry=%v", ok, retry)
	}
	c.advance(61 * time.Second)
	if ok, _ := l.AllowTargetOpen("host"); !ok {
		t.Fatal("must recover")
	}
}

func TestWaitingMax(t *testing.T) {
	l, _ := newTest()
	var rel []func()
	for i := 0; i < PipeWaitingMax; i++ {
		r, ok := l.AcquireWaiting("host")
		if !ok {
			t.Fatalf("slot %d refused", i)
		}
		rel = append(rel, r)
	}
	if _, ok := l.AcquireWaiting("host"); ok {
		t.Fatal("5th waiting must be refused")
	}
	rel[0]()
	rel[0]() // double release must not free a second slot
	if _, ok := l.AcquireWaiting("host"); !ok {
		t.Fatal("released slot must be reusable")
	}
	if _, ok := l.AcquireWaiting("host"); ok {
		t.Fatal("double release freed two slots")
	}
}

func TestConnsPerIP(t *testing.T) {
	l, _ := newTest()
	var rel []func()
	for i := 0; i < ConnsPerIP; i++ {
		r, ok := l.AcquireConn("ip")
		if !ok {
			t.Fatalf("conn %d refused", i)
		}
		rel = append(rel, r)
	}
	if _, ok := l.AcquireConn("ip"); ok {
		t.Fatal("201st must be refused")
	}
	rel[0]()
	if _, ok := l.AcquireConn("ip"); !ok {
		t.Fatal("must accept after release")
	}
}

func TestFrameBucket(t *testing.T) {
	c := &fakeClock{t: time.Unix(1000, 0)}
	b := NewFrameBucket(c.now)
	for i := 0; i < FrameBurst; i++ {
		if !b.Allow() {
			t.Fatalf("burst frame %d refused", i)
		}
	}
	if b.Allow() {
		t.Fatal("frame beyond burst must be refused")
	}
	c.advance(100 * time.Millisecond) // 5 tokens
	for i := 0; i < 5; i++ {
		if !b.Allow() {
			t.Fatalf("refilled frame %d refused", i)
		}
	}
	if b.Allow() {
		t.Fatal("only 5 tokens expected")
	}
}

func TestSweepBoundsMemory(t *testing.T) {
	l, c := newTest()
	l.AllowRegistration("a")
	l.RecordKeyFailure("a")
	l.RecordPreAuthFailure("a")
	l.AllowPipeOpen("d", "t")
	l.AllowTargetOpen("t")
	c.advance(2 * day)
	l.Sweep()
	if len(l.regs)+len(l.authFails)+len(l.pipes)+len(l.targetOpens)+len(l.preAuth) != 0 {
		t.Fatal("sweep left state behind")
	}
}

func TestPreAuthBucketIsSoft(t *testing.T) {
	l, c := newTest()
	for i := 0; i < PreAuthBurst-1; i++ {
		if l.RecordPreAuthFailure("9.9.9.9") {
			t.Fatalf("empty after %d", i+1)
		}
	}
	if blocked, _ := l.PreAuthBlocked("9.9.9.9"); blocked {
		t.Fatal("must not throttle inside the burst")
	}
	if !l.RecordPreAuthFailure("9.9.9.9") {
		t.Fatal("burst must empty the bucket")
	}
	blocked, d := l.PreAuthBlocked("9.9.9.9")
	if !blocked || d <= 0 || d > PreAuthRefill {
		t.Fatalf("blocked=%v d=%v", blocked, d)
	}
	if blocked, _ := l.PreAuthBlocked("9.9.9.10"); blocked {
		t.Fatal("other IP unaffected")
	}
	c.advance(PreAuthRefill + time.Millisecond)
	if blocked, _ := l.PreAuthBlocked("9.9.9.9"); blocked {
		t.Fatal("one token must be back after one refill interval")
	}
	// the bucket never becomes a long ban: 10 minutes of idle refills it fully
	l.RecordPreAuthFailure("9.9.9.9")
	c.advance(10 * time.Minute)
	for i := 0; i < PreAuthBurst-1; i++ {
		l.RecordPreAuthFailure("9.9.9.9")
	}
	if blocked, _ := l.PreAuthBlocked("9.9.9.9"); blocked {
		t.Fatal("bucket must refill to the full burst")
	}
}
