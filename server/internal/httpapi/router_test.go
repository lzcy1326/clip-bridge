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
	"clipbridge/server/internal/clipboard"
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

type clipUploaderStub struct {
	clip      clipboard.TextClip
	upload    clipboard.TextUpload
	userID    int64
	duplicate bool
	err       error
	calls     int
}

func (stub *clipUploaderStub) UploadText(_ context.Context, userID int64, upload clipboard.TextUpload) (clipboard.TextClip, bool, error) {
	stub.calls++
	stub.userID = userID
	stub.upload = upload
	return stub.clip, stub.duplicate, stub.err
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
	router := NewRouter(nil, nil, nil, nil)
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
	router := NewRouter(service, nil, nil, nil)
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
	router := NewRouter(&loginServiceStub{err: auth.ErrInvalidCredentials}, nil, nil, nil)
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
		router := NewRouter(&loginServiceStub{}, nil, nil, nil)
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
	router := NewRouter(service, nil, nil, nil)
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
	router := NewRouter(nil, verifier, registrar, nil)
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
		router := NewRouter(nil, verifier, registrar, nil)
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
	router := NewRouter(nil, verifier, registrar, nil)
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
	router := NewRouter(nil, verifier, registrar, nil)
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
	router := NewRouter(nil, verifier, registrar, nil)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"DEV-PC","platform":"windows","client_version":"1.0.0"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status 401, got %d", response.Code)
	}
}

func TestUploadTextUsesAuthenticatedUserAndReturnsStoredClip(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	uploader := &clipUploaderStub{clip: clipboard.TextClip{
		ID: "550e8400-e29b-41d4-a716-446655440000", OriginDeviceID: 9, Type: "TEXT", ContentHash: "hash",
	}}
	router := NewRouter(nil, verifier, nil, uploader)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/clips", strings.NewReader(`{"id":"550e8400-e29b-41d4-a716-446655440000","device_id":9,"text":"hello from ClipBridge"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatalf("expected status 201, got %d: %s", response.Code, response.Body.String())
	}
	if verifier.calls != 1 || uploader.calls != 1 || uploader.userID != 42 || uploader.upload.Text != "hello from ClipBridge" || uploader.upload.DeviceID != 9 {
		t.Fatalf("upload did not use verified identity or expected body: verifier=%d uploader=%+v", verifier.calls, uploader)
	}
}

func TestUploadTextRequiresAuthenticationAndRejectsOwnershipFields(t *testing.T) {
	for _, authorization := range []string{"", "Basic valid-access-token", "Bearer invalid-access-token"} {
		verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
		uploader := &clipUploaderStub{}
		router := NewRouter(nil, verifier, nil, uploader)
		request := httptest.NewRequest(http.MethodPost, "/api/v1/clips", strings.NewReader(`{"id":"550e8400-e29b-41d4-a716-446655440000","device_id":9,"text":"hello"}`))
		if authorization != "" {
			request.Header.Set("Authorization", authorization)
		}
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if response.Code != http.StatusUnauthorized || uploader.calls != 0 {
			t.Errorf("authorization %q: expected 401 without upload, got %d and %d calls", authorization, response.Code, uploader.calls)
		}
	}

	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	uploader := &clipUploaderStub{}
	router := NewRouter(nil, verifier, nil, uploader)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/clips", strings.NewReader(`{"id":"550e8400-e29b-41d4-a716-446655440000","device_id":9,"user_id":999,"text":"hello"}`))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusBadRequest || uploader.calls != 0 {
		t.Fatalf("expected client-supplied owner to be rejected, got %d and %d calls", response.Code, uploader.calls)
	}
}

func TestUploadTextMapsDuplicateAndValidationErrors(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	validBody := `{"id":"550e8400-e29b-41d4-a716-446655440000","device_id":9,"text":"hello"}`
	router := NewRouter(nil, verifier, nil, &clipUploaderStub{duplicate: true})
	request := httptest.NewRequest(http.MethodPost, "/api/v1/clips", strings.NewReader(validBody))
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("expected idempotent retry status 200, got %d: %s", response.Code, response.Body.String())
	}

	for _, test := range []struct {
		name string
		err  error
		want int
	}{
		{name: "invalid text", err: clipboard.ErrInvalidTextUpload, want: http.StatusBadRequest},
		{name: "unavailable device", err: clipboard.ErrDeviceUnavailable, want: http.StatusForbidden},
		{name: "id conflict", err: clipboard.ErrClipConflict, want: http.StatusConflict},
	} {
		t.Run(test.name, func(t *testing.T) {
			router := NewRouter(nil, verifier, nil, &clipUploaderStub{err: test.err})
			request := httptest.NewRequest(http.MethodPost, "/api/v1/clips", strings.NewReader(validBody))
			request.Header.Set("Authorization", "Bearer valid-access-token")
			response := httptest.NewRecorder()
			router.ServeHTTP(response, request)
			if response.Code != test.want {
				t.Fatalf("expected status %d, got %d: %s", test.want, response.Code, response.Body.String())
			}
		})
	}
}

func TestUploadTextRejectsMalformedOversizedAndTrailingBodies(t *testing.T) {
	for _, body := range []string{
		"{",
		`{"id":"550e8400-e29b-41d4-a716-446655440000","device_id":9,"text":"hello"} {}`,
		fmt.Sprintf(`{"id":"550e8400-e29b-41d4-a716-446655440000","device_id":9,"text":"%s"}`, strings.Repeat("x", clipboard.MaxUploadBodyBytes)),
	} {
		verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
		uploader := &clipUploaderStub{}
		router := NewRouter(nil, verifier, nil, uploader)
		request := httptest.NewRequest(http.MethodPost, "/api/v1/clips", strings.NewReader(body))
		request.Header.Set("Authorization", "Bearer valid-access-token")
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if response.Code != http.StatusBadRequest || uploader.calls != 0 {
			t.Errorf("malformed or oversized request: expected 400 without upload, got %d and %d calls", response.Code, uploader.calls)
		}
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
