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
	"time"

	"clipbridge/server/internal/accounts"
)

const accessTokenLifetime = 15 * time.Minute

var ErrInvalidCredentials = errors.New("invalid credentials")

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
