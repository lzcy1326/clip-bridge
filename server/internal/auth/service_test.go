package auth

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"strings"
	"testing"
	"time"

	"clipbridge/server/internal/accounts"
)

type userStoreStub struct {
	user accounts.AuthenticatedUser
	err  error
}

func (stub userStoreStub) Authenticate(context.Context, string, string) (accounts.AuthenticatedUser, error) {
	return stub.user, stub.err
}

func TestLoginIssuesSignedShortLivedAccessToken(t *testing.T) {
	secret := []byte(strings.Repeat("s", 32))
	service, err := NewService(userStoreStub{user: accounts.AuthenticatedUser{
		ID: 7, Username: "test-user", DisplayName: "Test User",
	}}, secret)
	if err != nil {
		t.Fatalf("NewService() error = %v", err)
	}
	now := time.Date(2026, time.September, 29, 12, 0, 0, 0, time.UTC)
	service.now = func() time.Time { return now }

	result, err := service.Login(context.Background(), "test-user", "password")
	if err != nil {
		t.Fatalf("Login() error = %v", err)
	}
	if result.TokenType != "Bearer" || result.ExpiresIn != 900 {
		t.Fatalf("unexpected token metadata: type=%q expires=%d", result.TokenType, result.ExpiresIn)
	}
	if result.User.ID != 7 || result.User.Username != "test-user" || result.User.DisplayName != "Test User" {
		t.Fatalf("unexpected user response: %+v", result.User)
	}

	parts := strings.Split(result.AccessToken, ".")
	if len(parts) != 3 {
		t.Fatalf("expected JWT with 3 parts, got %d", len(parts))
	}
	mac := hmac.New(sha256.New, secret)
	mac.Write([]byte(parts[0] + "." + parts[1]))
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil || !hmac.Equal(signature, mac.Sum(nil)) {
		t.Fatal("access token signature is invalid")
	}
	claimsJSON, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		t.Fatalf("decode claims: %v", err)
	}
	var claims accessClaims
	if err := json.Unmarshal(claimsJSON, &claims); err != nil {
		t.Fatalf("unmarshal claims: %v", err)
	}
	if claims.Issuer != "clipbridge" || claims.Subject != "7" || claims.IssuedAt != now.Unix() || claims.Expires != now.Add(15*time.Minute).Unix() {
		t.Fatalf("unexpected claims: %+v", claims)
	}
	userID, err := service.UserIDFromToken(result.AccessToken)
	if err != nil || userID != 7 {
		t.Fatalf("UserIDFromToken() = %d, %v; want 7, nil", userID, err)
	}
	if _, err := UserIDFromAccessToken(result.AccessToken, secret, now.Add(16*time.Minute)); !errors.Is(err, ErrInvalidAccessToken) {
		t.Fatalf("expired token error = %v, want ErrInvalidAccessToken", err)
	}
	if _, err := UserIDFromAccessToken(result.AccessToken, []byte(strings.Repeat("x", 32)), now); !errors.Is(err, ErrInvalidAccessToken) {
		t.Fatalf("wrong-secret token error = %v, want ErrInvalidAccessToken", err)
	}
	parts = strings.Split(result.AccessToken, ".")
	tampered := parts[0] + "." + parts[1] + "A." + parts[2]
	if _, err := UserIDFromAccessToken(tampered, secret, now); !errors.Is(err, ErrInvalidAccessToken) {
		t.Fatalf("tampered token error = %v, want ErrInvalidAccessToken", err)
	}
}

func TestLoginHidesInvalidCredentials(t *testing.T) {
	service, err := NewService(userStoreStub{err: accounts.ErrInvalidCredentials}, []byte(strings.Repeat("s", 32)))
	if err != nil {
		t.Fatalf("NewService() error = %v", err)
	}
	if _, err := service.Login(context.Background(), "missing", "wrong"); !errors.Is(err, ErrInvalidCredentials) {
		t.Fatalf("Login() error = %v, want generic invalid credentials", err)
	}
}

func TestNewServiceRequiresStrongSecret(t *testing.T) {
	if _, err := NewService(userStoreStub{}, []byte("short")); err == nil {
		t.Fatal("NewService() accepted a JWT secret shorter than 32 bytes")
	}
}
