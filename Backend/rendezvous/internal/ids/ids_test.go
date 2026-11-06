package ids

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"strconv"
	"testing"
)

type vectors struct {
	FromBits []struct {
		DataBitsHex string `json:"data_bits_hex"`
		DeskID      string `json:"desk_id"`
		Display     string `json:"display"`
	} `json:"desk_id_from_bits"`
	Parse []struct {
		Label     string  `json:"label"`
		Input     string  `json:"input"`
		OK        bool    `json:"ok"`
		Canonical *string `json:"canonical"`
		Error     *string `json:"error"`
	} `json:"desk_id_parse"`
}

func load(t *testing.T) vectors {
	t.Helper()
	raw, err := os.ReadFile("../../../../Tests/Vectors/identity-v2.json")
	if err != nil {
		t.Fatal(err)
	}
	var v vectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatal(err)
	}
	return v
}

func TestFromBitsVectors(t *testing.T) {
	for _, e := range load(t).FromBits {
		n, err := strconv.ParseUint(e.DataBitsHex, 16, 64)
		if err != nil {
			t.Fatal(err)
		}
		if got := FromBits(n); got != e.DeskID {
			t.Errorf("FromBits(%s) = %s want %s", e.DataBitsHex, got, e.DeskID)
		}
		if got := Display(e.DeskID); got != e.Display {
			t.Errorf("Display = %s want %s", got, e.Display)
		}
	}
}

func TestParseVectors(t *testing.T) {
	codes := map[string]error{"BAD_CHAR": ErrBadChar, "BAD_LENGTH": ErrBadLength, "BAD_CHECK": ErrBadCheck}
	for _, e := range load(t).Parse {
		got, err := Parse(e.Input)
		if e.OK {
			if err != nil || got != *e.Canonical {
				t.Errorf("%s: got %q,%v want %q", e.Label, got, err, *e.Canonical)
			}
			continue
		}
		if !errors.Is(err, codes[*e.Error]) {
			t.Errorf("%s: err %v want %s", e.Label, err, *e.Error)
		}
	}
}

func TestIsCanonical(t *testing.T) {
	if !IsCanonical("JGCEXMGVAQ1C") {
		t.Error("canonical rejected")
	}
	for _, s := range []string{"jgcexmgvaq1c", "JGCE-XMGV-AQ1C", "JGCEXMGVAQ1D", ""} {
		if IsCanonical(s) {
			t.Errorf("%q accepted", s)
		}
	}
}

func TestGenerateAlwaysValid(t *testing.T) {
	for i := 0; i < 2000; i++ {
		id, err := Generate(nil)
		if err != nil || !IsCanonical(id) {
			t.Fatalf("bad id %q err %v", id, err)
		}
	}
}

func TestGenerateMasksTo55Bits(t *testing.T) {
	id, err := Generate(bytes.NewReader(bytes.Repeat([]byte{0xFF}, 7)))
	if err != nil || id != "ZZZZZZZZZZZ0" {
		t.Fatalf("got %q %v", id, err)
	}
}

// rngFor returns a reader that makes Generate yield the given data bits in order.
func rngFor(bits ...uint64) *bytes.Reader {
	var b []byte
	for _, v := range bits {
		for i := 6; i >= 0; i-- {
			b = append(b, byte(v>>(8*i)))
		}
	}
	return bytes.NewReader(b)
}

func TestAllocateRetriesOnCollision(t *testing.T) {
	const a, b = 0x4a0c77690daae1, 0x1bb7a0e4a443ef
	taken := map[string]bool{FromBits(a): true}
	id, err := Allocate(context.Background(), rngFor(a, a, b), func(_ context.Context, id string) (bool, error) {
		if taken[id] {
			return false, nil
		}
		taken[id] = true
		return true, nil
	})
	if err != nil || id != FromBits(b) {
		t.Fatalf("got %q %v", id, err)
	}
}

func TestAllocateGivesUpAfterFive(t *testing.T) {
	calls := 0
	_, err := Allocate(context.Background(), rngFor(1, 1, 1, 1, 1, 1, 1), func(context.Context, string) (bool, error) {
		calls++
		return false, nil
	})
	if !errors.Is(err, ErrExhausted) || calls != MaxAllocTries {
		t.Fatalf("err %v calls %d", err, calls)
	}
}

func TestAllocateReserveErrorPropagates(t *testing.T) {
	boom := errors.New("db down")
	_, err := Allocate(context.Background(), nil, func(context.Context, string) (bool, error) { return false, boom })
	if !errors.Is(err, boom) {
		t.Fatalf("got %v", err)
	}
}
