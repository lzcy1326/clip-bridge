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

func TestVerifyPassword(t *testing.T) {
	encoded, err := HashPassword("correct horse battery staple")
	if err != nil {
		t.Fatalf("HashPassword() error = %v", err)
	}
	if !VerifyPassword("correct horse battery staple", encoded) {
		t.Fatal("VerifyPassword() rejected the correct password")
	}
	if VerifyPassword("incorrect password", encoded) {
		t.Fatal("VerifyPassword() accepted an incorrect password")
	}
}

func TestVerifyPasswordRejectsMalformedOrExpensiveParameters(t *testing.T) {
	for _, encoded := range []string{
		"",
		"$argon2id$v=19$m=999999999,t=3,p=4$YWJj$YWJj",
		"$argon2id$v=19$m=65536,t=3,p=4$invalid$invalid",
		"$argon2i$v=19$m=65536,t=3,p=4$YWJj$YWJj",
	} {
		if VerifyPassword("some password", encoded) {
			t.Errorf("VerifyPassword() accepted malformed hash %q", encoded)
		}
	}
}
