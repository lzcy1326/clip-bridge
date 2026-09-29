package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"clipbridge/server/internal/auth"
)

type loginServiceStub struct {
	result auth.LoginResult
	err    error
	calls  int
}

func (stub *loginServiceStub) Login(context.Context, string, string) (auth.LoginResult, error) {
	stub.calls++
	return stub.result, stub.err
}

func TestHealthReturnsOk(t *testing.T) {
	router := NewRouter(nil)
	request := httptest.NewRequest(http.MethodGet, "/health", nil)
	response := httptest.NewRecorder()

	router.ServeHTTP(response, request)

	if response.Code != http.StatusOK {
		t.Fatalf("expected status %d, got %d", http.StatusOK, response.Code)
	}

	var body struct {
		Status string `json:"status"`
	}
	if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
		t.Fatalf("decode response: %v", err)
	}
	if body.Status != "ok" {
		t.Fatalf("expected status body %q, got %q", "ok", body.Status)
	}
}

func TestLoginReturnsTokenResponse(t *testing.T) {
	service := &loginServiceStub{result: auth.LoginResult{
		AccessToken: "signed-token", TokenType: "Bearer", ExpiresIn: 900,
		User: auth.User{ID: 1, Username: "clipbridge-local", DisplayName: "Local"},
	}}
	router := NewRouter(service)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/login", strings.NewReader(`{"username":"clipbridge-local","password":"a-long-test-password"}`))
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("expected status 200, got %d: %s", response.Code, response.Body.String())
	}
	var body auth.LoginResult
	if err := json.Unmarshal(response.Body.Bytes(), &body); err != nil {
		t.Fatalf("decode login response: %v", err)
	}
	if body.AccessToken != "signed-token" || body.User.ID != 1 || service.calls != 1 {
		t.Fatalf("unexpected login response or call count: body=%+v calls=%d", body, service.calls)
	}
}

func TestLoginReturnsGenericUnauthorizedForInvalidCredentials(t *testing.T) {
	router := NewRouter(&loginServiceStub{err: auth.ErrInvalidCredentials})
	request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/login", strings.NewReader(`{"username":"unknown-user","password":"incorrect"}`))
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusUnauthorized || !strings.Contains(response.Body.String(), `"invalid_credentials"`) {
		t.Fatalf("expected generic unauthorized response, got %d: %s", response.Code, response.Body.String())
	}
	if strings.Contains(response.Body.String(), "unknown-user") || strings.Contains(response.Body.String(), "incorrect") {
		t.Fatal("login response leaked submitted credentials")
	}
}

func TestLoginRejectsMalformedAndOversizedBodies(t *testing.T) {
	for _, body := range []string{"{", fmt.Sprintf(`{"username":"test","password":"%s"}`, strings.Repeat("x", maxLoginBodyBytes))} {
		router := NewRouter(&loginServiceStub{})
		request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/login", strings.NewReader(body))
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if response.Code != http.StatusBadRequest {
			t.Errorf("expected malformed/oversized request status 400, got %d", response.Code)
		}
	}
}

func TestLoginRateLimitAppliesPerRemoteAddress(t *testing.T) {
	service := &loginServiceStub{err: errors.New("internal test error")}
	router := NewRouter(service)
	for i := 0; i < loginAttemptLimit+1; i++ {
		request := httptest.NewRequest(http.MethodPost, "/api/v1/auth/login", strings.NewReader(`{"username":"test-user","password":"some-password"}`))
		request.RemoteAddr = "192.0.2.10:12345"
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if i < loginAttemptLimit && response.Code != http.StatusInternalServerError {
			t.Fatalf("attempt %d: expected request to reach service, got status %d", i+1, response.Code)
		}
		if i == loginAttemptLimit && response.Code != http.StatusTooManyRequests {
			t.Fatalf("expected rate limited status, got %d", response.Code)
		}
	}
	if service.calls != loginAttemptLimit {
		t.Fatalf("service called %d times, want %d", service.calls, loginAttemptLimit)
	}
}
