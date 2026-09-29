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
	"clipbridge/server/internal/device"
)

type loginServiceStub struct {
	result auth.LoginResult
	err    error
	calls  int
}

type tokenVerifierStub struct {
	userID int64
	err    error
	token  string
	calls  int
}

func (stub *tokenVerifierStub) UserIDFromToken(token string) (int64, error) {
	stub.calls++
	if token != stub.token {
		return 0, auth.ErrInvalidAccessToken
	}
	return stub.userID, stub.err
}

type deviceRegistrarStub struct {
	device       device.Device
	err          error
	userID       int64
	registration device.Registration
	calls        int
}

func (stub *deviceRegistrarStub) Register(_ context.Context, userID int64, registration device.Registration) (device.Device, error) {
	stub.calls++
	stub.userID = userID
	stub.registration = registration
	return stub.device, stub.err
}

func (stub *loginServiceStub) Login(context.Context, string, string) (auth.LoginResult, error) {
	stub.calls++
	return stub.result, stub.err
}

func TestHealthReturnsOk(t *testing.T) {
	router := NewRouter(nil, nil, nil)
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
	router := NewRouter(service, nil, nil)
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
	router := NewRouter(&loginServiceStub{err: auth.ErrInvalidCredentials}, nil, nil)
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
		router := NewRouter(&loginServiceStub{}, nil, nil)
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
	router := NewRouter(service, nil, nil)
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

func TestRegisterDeviceUsesAuthenticatedUserFromToken(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	registrar := &deviceRegistrarStub{device: device.Device{ID: 9, Name: "ClipBridge", Status: "ACTIVE"}}
	router := NewRouter(nil, verifier, registrar)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"DEV-PC","platform":"windows","client_version":"1.0.0"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatalf("expected status 201, got %d: %s", response.Code, response.Body.String())
	}
	if verifier.calls != 1 || registrar.calls != 1 || registrar.userID != 42 {
		t.Fatalf("registration did not use verified user identity: verifier=%d registrar=%d user=%d", verifier.calls, registrar.calls, registrar.userID)
	}
	if registrar.registration.MachineName != "DEV-PC" || registrar.registration.Platform != "windows" {
		t.Fatalf("unexpected registration request: %+v", registrar.registration)
	}
}

func TestRegisterDeviceRequiresValidBearerToken(t *testing.T) {
	for _, authorization := range []string{"", "Basic valid-access-token", "Bearer invalid-access-token"} {
		verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
		registrar := &deviceRegistrarStub{}
		router := NewRouter(nil, verifier, registrar)
		request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"DEV-PC","platform":"windows","client_version":"1.0.0"}`))
		if authorization != "" {
			request.Header.Set("Authorization", authorization)
		}
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if response.Code != http.StatusUnauthorized {
			t.Errorf("authorization %q: expected status 401, got %d", authorization, response.Code)
		}
		if registrar.calls != 0 {
			t.Errorf("authorization %q: registrar was called", authorization)
		}
	}
}

func TestRegisterDeviceRejectsClientSuppliedUserID(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	registrar := &deviceRegistrarStub{}
	router := NewRouter(nil, verifier, registrar)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"user_id":7,"name":"ClipBridge","machine_name":"DEV-PC","platform":"windows","client_version":"1.0.0"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || registrar.calls != 0 {
		t.Fatalf("expected rejected user_id field, got status %d registrar calls %d", response.Code, registrar.calls)
	}
}

func TestRegisterDeviceRejectsInvalidMetadata(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	registrar := &deviceRegistrarStub{err: device.ErrInvalidRegistration}
	router := NewRouter(nil, verifier, registrar)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"","machine_name":"DEV-PC","platform":"windows","client_version":"1.0.0"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status 400, got %d: %s", response.Code, response.Body.String())
	}
}

func TestRegisterDeviceMapsInactiveUserToUnauthorized(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	registrar := &deviceRegistrarStub{err: device.ErrUserUnavailable}
	router := NewRouter(nil, verifier, registrar)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"DEV-PC","platform":"windows","client_version":"1.0.0"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status 401, got %d", response.Code)
	}
}

func TestParseBearerToken(t *testing.T) {
	token, ok := parseBearerToken("bEaReR abc.def.ghi")
	if !ok || token != "abc.def.ghi" {
		t.Fatalf("parseBearerToken() = %q, %v", token, ok)
	}
	if _, ok := parseBearerToken("Bearer token extra"); ok {
		t.Fatal("parseBearerToken() accepted multiple token values")
	}
	if _, ok := parseBearerToken("Bearer " + strings.Repeat("x", 4097)); ok {
		t.Fatal("parseBearerToken() accepted oversized token")
	}
}
