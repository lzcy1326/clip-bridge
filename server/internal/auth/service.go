package auth

import (
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"strconv"
	"strings"
	"time"

	"clipbridge/server/internal/accounts"
)

const accessTokenLifetime = 15 * time.Minute

var ErrInvalidCredentials = errors.New("invalid credentials")
var ErrInvalidAccessToken = errors.New("invalid access token")

type UserStore interface {
	Authenticate(ctx context.Context, username, password string) (accounts.AuthenticatedUser, error)
}

type Service struct {
	users  UserStore
	secret []byte
	now    func() time.Time
}

type LoginResult struct {
	AccessToken string `json:"access_token"`
	TokenType   string `json:"token_type"`
	ExpiresIn   int64  `json:"expires_in"`
	User        User   `json:"user"`
}

type User struct {
	ID          int64  `json:"id"`
	Username    string `json:"username"`
	DisplayName string `json:"display_name"`
}

type accessClaims struct {
	Issuer   string `json:"iss"`
	Subject  string `json:"sub"`
	IssuedAt int64  `json:"iat"`
	Expires  int64  `json:"exp"`
}

func NewService(users UserStore, secret []byte) (*Service, error) {
	if users == nil {
		return nil, errors.New("user store is required")
	}
	if len(secret) < 32 {
		return nil, errors.New("JWT secret must contain at least 32 bytes")
	}
	return &Service{users: users, secret: append([]byte(nil), secret...), now: time.Now}, nil
}

func (service *Service) Login(ctx context.Context, username, password string) (LoginResult, error) {
	user, err := service.users.Authenticate(ctx, username, password)
	if err != nil {
		if errors.Is(err, accounts.ErrInvalidCredentials) {
			return LoginResult{}, ErrInvalidCredentials
		}
		return LoginResult{}, fmt.Errorf("authenticate user: %w", err)
	}

	now := service.now().UTC()
	claims := accessClaims{
		Issuer:   "clipbridge",
		Subject:  strconv.FormatInt(user.ID, 10),
		IssuedAt: now.Unix(),
		Expires:  now.Add(accessTokenLifetime).Unix(),
	}
	token, err := service.sign(claims)
	if err != nil {
		return LoginResult{}, fmt.Errorf("sign access token: %w", err)
	}
	return LoginResult{
		AccessToken: token,
		TokenType:   "Bearer",
		ExpiresIn:   int64(accessTokenLifetime.Seconds()),
		User: User{
			ID:          user.ID,
			Username:    user.Username,
			DisplayName: user.DisplayName,
		},
	}, nil
}

func (service *Service) UserIDFromToken(token string) (int64, error) {
	return UserIDFromAccessToken(token, service.secret, service.now())
}

func UserIDFromAccessToken(token string, secret []byte, now time.Time) (int64, error) {
	if len(secret) < 32 || len(token) == 0 || len(token) > 4096 {
		return 0, ErrInvalidAccessToken
	}
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return 0, ErrInvalidAccessToken
	}

	mac := hmac.New(sha256.New, secret)
	if _, err := mac.Write([]byte(parts[0] + "." + parts[1])); err != nil {
		return 0, ErrInvalidAccessToken
	}
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil || !hmac.Equal(signature, mac.Sum(nil)) {
		return 0, ErrInvalidAccessToken
	}

	headerJSON, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return 0, ErrInvalidAccessToken
	}
	var header struct {
		Algorithm string `json:"alg"`
		Type      string `json:"typ"`
	}
	if err := json.Unmarshal(headerJSON, &header); err != nil || header.Algorithm != "HS256" || header.Type != "JWT" {
		return 0, ErrInvalidAccessToken
	}

	claimsJSON, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return 0, ErrInvalidAccessToken
	}
	var claims accessClaims
	if err := json.Unmarshal(claimsJSON, &claims); err != nil || claims.Issuer != "clipbridge" {
		return 0, ErrInvalidAccessToken
	}
	if claims.IssuedAt <= 0 || claims.Expires <= now.Unix() || claims.Expires <= claims.IssuedAt || claims.Expires-claims.IssuedAt > int64(accessTokenLifetime.Seconds()) || claims.IssuedAt > now.Add(30*time.Second).Unix() {
		return 0, ErrInvalidAccessToken
	}
	userID, err := strconv.ParseInt(claims.Subject, 10, 64)
	if err != nil || userID <= 0 {
		return 0, ErrInvalidAccessToken
	}
	return userID, nil
}

func (service *Service) sign(claims accessClaims) (string, error) {
	header, err := json.Marshal(struct {
		Algorithm string `json:"alg"`
		Type      string `json:"typ"`
	}{Algorithm: "HS256", Type: "JWT"})
	if err != nil {
		return "", err
	}
	body, err := json.Marshal(claims)
	if err != nil {
		return "", err
	}
	encoded := base64.RawURLEncoding.EncodeToString(header) + "." + base64.RawURLEncoding.EncodeToString(body)
	mac := hmac.New(sha256.New, service.secret)
	if _, err := mac.Write([]byte(encoded)); err != nil {
		return "", err
	}
	return encoded + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil)), nil
}
