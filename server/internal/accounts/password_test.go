package accounts

import (
	"strings"
	"testing"
)

func TestNormalizeUsername(t *testing.T) {
	got, err := NormalizeUsername("  ClipBridge.Local  ")
	if err != nil {
		t.Fatalf("NormalizeUsername() error = %v", err)
	}
	if got != "clipbridge.local" {
		t.Fatalf("NormalizeUsername() = %q, want %q", got, "clipbridge.local")
	}

	for _, username := range []string{"ab", "has spaces", "contains/slash", strings.Repeat("a", 65)} {
		if _, err := NormalizeUsername(username); err == nil {
			t.Errorf("NormalizeUsername(%q) expected validation error", username)
		}
	}
}

func TestHashPasswordUsesArgon2idAndUniqueSalts(t *testing.T) {
	first, err := HashPassword("correct horse battery staple")
	if err != nil {
		t.Fatalf("HashPassword() error = %v", err)
	}
	second, err := HashPassword("correct horse battery staple")
	if err != nil {
		t.Fatalf("HashPassword() second call error = %v", err)
	}
	if !strings.HasPrefix(first, "$argon2id$v=19$") {
		t.Fatalf("HashPassword() format = %q, want Argon2id PHC string", first)
	}
	if first == second {
		t.Fatal("HashPassword() returned identical hashes; each password hash must have a random salt")
	}
}

func TestHashPasswordRejectsShortPassword(t *testing.T) {
	if _, err := HashPassword("too-short"); err == nil {
		t.Fatal("HashPassword() expected a validation error")
	}
}
