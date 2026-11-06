// Package ids implements ReyDesk ID (Desk ID) encoding, parsing and allocation
// (spec 1.3): 11 Crockford base32 chars of 55 random bits plus one check char.
package ids

import (
	"context"
	"crypto/rand"
	"errors"
	"fmt"
	"io"
	"strings"
)

const (
	Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
	Length   = 12
	// MaxAllocTries is the number of draws before allocation gives up (spec: 5, then 503).
	MaxAllocTries = 5
)

var (
	ErrBadChar   = errors.New("ids: bad character")
	ErrBadLength = errors.New("ids: bad length")
	ErrBadCheck  = errors.New("ids: bad check character")
	ErrExhausted = errors.New("ids: could not allocate a unique id")
)

var alphabetIndex = func() (t [256]int8) {
	for i := range t {
		t[i] = -1
	}
	for i := 0; i < len(Alphabet); i++ {
		t[Alphabet[i]] = int8(i)
	}
	return
}()

// checkValue = (sum_{i=0..10} (i+1) * v_i) mod 31 over the 11 data chars.
func checkValue(data string) int {
	sum := 0
	for i := 0; i < len(data); i++ {
		sum += (i + 1) * int(alphabetIndex[data[i]])
	}
	return sum % 31
}

// FromBits encodes the low 55 bits of bits (char 0 = top 5 bits) plus the check char.
func FromBits(bits uint64) string {
	bits &= (1 << 55) - 1
	var b [Length]byte
	for i := 0; i < 11; i++ {
		b[i] = Alphabet[(bits>>(5*(10-i)))&31]
	}
	b[11] = Alphabet[checkValue(string(b[:11]))]
	return string(b[:])
}

// Generate draws 55 bits from r (crypto/rand when nil) and encodes them.
func Generate(r io.Reader) (string, error) {
	if r == nil {
		r = rand.Reader
	}
	var buf [8]byte
	if _, err := io.ReadFull(r, buf[1:]); err != nil {
		return "", err
	}
	var bits uint64
	for _, c := range buf {
		bits = bits<<8 | uint64(c)
	}
	return FromBits(bits), nil
}

// Parse normalises user input per the shared vectors and validates the check
// char. It returns the canonical 12-char form. Error precedence: bad char,
// bad length, bad check.
func Parse(input string) (string, error) {
	out := make([]byte, 0, Length+2)
	for _, r := range input {
		switch r {
		case '-', ' ', 0x200E, 0x200F, 0x2066, 0x2067, 0x2068, 0x2069:
			continue
		}
		switch {
		case r >= 0x06F0 && r <= 0x06F9:
			r = '0' + (r - 0x06F0)
		case r >= 0x0660 && r <= 0x0669:
			r = '0' + (r - 0x0660)
		case r >= 'a' && r <= 'z':
			r -= 'a' - 'A'
		}
		switch r {
		case 'I', 'L':
			r = '1'
		case 'O':
			r = '0'
		case 'U':
			continue
		}
		if r >= 128 || alphabetIndex[r] < 0 {
			return "", ErrBadChar
		}
		out = append(out, byte(r))
	}
	if len(out) != Length {
		return "", ErrBadLength
	}
	s := string(out)
	if Alphabet[checkValue(s[:11])] != s[11] {
		return "", ErrBadCheck
	}
	return s, nil
}

// IsCanonical reports whether s is exactly a valid canonical Desk ID (what the
// wire carries): 12 uppercase alphabet chars with a correct check char.
func IsCanonical(s string) bool {
	c, err := Parse(s)
	return err == nil && c == s
}

// Display formats a canonical ID as XXXX-XXXX-XXXX.
func Display(canonical string) string {
	if len(canonical) != Length {
		return canonical
	}
	return strings.Join([]string{canonical[:4], canonical[4:8], canonical[8:]}, "-")
}

// Reserve tries to claim id in the database (INSERT ... ON CONFLICT DO NOTHING).
// It returns true when the id was newly claimed, false on a collision.
type Reserve func(ctx context.Context, id string) (bool, error)

// Allocate draws random IDs from rng (crypto/rand when nil) until reserve
// claims one, at most MaxAllocTries times. Revoked IDs keep their row, so
// reserve can never succeed for an ID that was issued before.
func Allocate(ctx context.Context, rng io.Reader, reserve Reserve) (string, error) {
	for i := 0; i < MaxAllocTries; i++ {
		id, err := Generate(rng)
		if err != nil {
			return "", err
		}
		ok, err := reserve(ctx, id)
		if err != nil {
			return "", fmt.Errorf("ids: reserve: %w", err)
		}
		if ok {
			return id, nil
		}
	}
	return "", ErrExhausted
}
