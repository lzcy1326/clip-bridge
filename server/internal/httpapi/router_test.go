package httpapi

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

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

type clipListerStub struct {
	page   clipboard.TextHistoryPage
	after  *clipboard.Cursor
	limit  int
	userID int64
	err    error
	calls  int
}

const validTestClipID = "550e8400-e29b-41d4-a716-446655440000"

type imageUploaderStub struct {
	clip      clipboard.ImageClip
	upload    clipboard.ImageUpload
	userID    int64
	deviceID  int64
	ownerID   int64
	duplicate bool
	err       error
	calls     int
}

func (stub *imageUploaderStub) UploadImage(_ context.Context, userID, deviceID int64, upload clipboard.ImageUpload) (clipboard.ImageClip, bool, error) {
	stub.calls++
	stub.userID = userID
	stub.deviceID = deviceID
	stub.upload = upload
	return stub.clip, stub.duplicate, stub.err
}

func (stub *imageUploaderStub) UploadImageForOwner(_ context.Context, ownerID, deviceID int64, upload clipboard.ImageUpload) (clipboard.ImageClip, bool, error) {
	stub.calls++
	stub.ownerID = ownerID
	stub.deviceID = deviceID
	stub.upload = upload
	return stub.clip, stub.duplicate, stub.err
}

func (stub *clipListerStub) ListText(_ context.Context, userID int64, after *clipboard.Cursor, limit int) (clipboard.TextHistoryPage, error) {
	stub.calls++
	stub.userID = userID
	stub.after = after
	stub.limit = limit
	return stub.page, stub.err
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

func TestTextUploadNotifiesSSESubscribersWithoutClipboardContent(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "signed-token"}
	uploader := &clipUploaderStub{}
	router := NewRouter(nil, verifier, nil, uploader)
	server := httptest.NewServer(router)
	defer server.Close()

	streamContext, cancelStream := context.WithCancel(context.Background())
	defer cancelStream()
	streamRequest, err := http.NewRequestWithContext(streamContext, http.MethodGet, server.URL+"/api/v1/events", nil)
	if err != nil {
		t.Fatalf("create stream request: %v", err)
	}
	streamRequest.Header.Set("Authorization", "Bearer signed-token")
	streamResponse, err := server.Client().Do(streamRequest)
	if err != nil {
		t.Fatalf("open event stream: %v", err)
	}
	defer streamResponse.Body.Close()
	if streamResponse.StatusCode != http.StatusOK || streamResponse.Header.Get("Content-Type") != "text/event-stream" {
		t.Fatalf("unexpected stream response: %d %q", streamResponse.StatusCode, streamResponse.Header.Get("Content-Type"))
	}

	request, err := http.NewRequest(http.MethodPost, server.URL+"/api/v1/clips", strings.NewReader(`{"id":"`+validTestClipID+`","text":"private clipboard payload"}`))
	if err != nil {
		t.Fatalf("create upload request: %v", err)
	}
	request.Header.Set("Authorization", "Bearer signed-token")
	request.Header.Set("Content-Type", "application/json")
	uploadResponse, err := server.Client().Do(request)
	if err != nil {
		t.Fatalf("upload clipboard text: %v", err)
	}
	_ = uploadResponse.Body.Close()
	if uploadResponse.StatusCode != http.StatusCreated {
		t.Fatalf("upload status = %d, want %d", uploadResponse.StatusCode, http.StatusCreated)
	}

	lines := make(chan string, 8)
	go func() {
		scanner := bufio.NewScanner(streamResponse.Body)
		for scanner.Scan() {
			lines <- scanner.Text()
			if scanner.Text() == "data: changed" {
				return
			}
		}
	}()
	seenEvent := false
	seenData := false
	deadline := time.After(2 * time.Second)
	for !seenData {
		select {
		case line := <-lines:
			if strings.HasPrefix(line, "event: ") {
				seenEvent = line == "event: clipboard"
			}
			if strings.HasPrefix(line, "data: ") {
				seenData = line == "data: changed"
				if strings.Contains(line, "private clipboard payload") {
					t.Fatal("SSE notification leaked clipboard content")
				}
			}
		case <-deadline:
			t.Fatal("timed out waiting for SSE notification")
		}
	}
	if !seenEvent {
		t.Fatal("SSE notification did not use clipboard event type")
	}
}

func TestImageUploadUsesPairedDeviceIdentityAndRejectsOversizedBody(t *testing.T) {
	pairing := &pairingServiceStub{
		principal:  device.Principal{DeviceID: 7, OwnerID: 11, Name: "HOST-PC"},
		credential: "device-secret",
	}
	uploader := &imageUploaderStub{clip: clipboard.ImageClip{ID: validTestClipID, OriginDeviceID: 7, Type: "IMAGE", ContentType: "image/png"}}
	router := NewRouterWithImages(nil, nil, nil, nil, pairing, uploader)
	imageData := validTestImagePNG(t)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/images", bytes.NewReader(imageData))
	request.Header.Set("Authorization", "Bearer device-secret")
	request.Header.Set("X-Clip-ID", validTestClipID)
	request.Header.Set("Content-Type", "image/png")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatalf("expected status %d, got %d: %s", http.StatusCreated, response.Code, response.Body.String())
	}
	if uploader.calls != 1 || uploader.ownerID != 11 || uploader.deviceID != 7 || uploader.upload.ID != validTestClipID || !bytes.Equal(uploader.upload.Data, imageData) {
		t.Fatalf("image upload was not scoped to authenticated device: %+v", uploader)
	}

	uploader.duplicate = true
	duplicateRequest := httptest.NewRequest(http.MethodPost, "/api/v1/images", bytes.NewReader(imageData))
	duplicateRequest.Header.Set("Authorization", "Bearer device-secret")
	duplicateRequest.Header.Set("X-Clip-ID", validTestClipID)
	duplicateRequest.Header.Set("Content-Type", "image/png")
	duplicateResponse := httptest.NewRecorder()
	router.ServeHTTP(duplicateResponse, duplicateRequest)
	if duplicateResponse.Code != http.StatusOK {
		t.Fatalf("duplicate upload status = %d, want %d", duplicateResponse.Code, http.StatusOK)
	}

	oversizedRequest := httptest.NewRequest(http.MethodPost, "/api/v1/images", bytes.NewReader(make([]byte, clipboard.MaxImageUploadBytes+1)))
	oversizedRequest.Header.Set("Authorization", "Bearer device-secret")
	oversizedRequest.Header.Set("X-Clip-ID", validTestClipID)
	oversizedRequest.Header.Set("Content-Type", "image/png")
	oversizedResponse := httptest.NewRecorder()
	router.ServeHTTP(oversizedResponse, oversizedRequest)
	if oversizedResponse.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("oversized upload status = %d, want %d: %s", oversizedResponse.Code, http.StatusRequestEntityTooLarge, oversizedResponse.Body.String())
	}
	if uploader.calls != 2 {
		t.Fatalf("oversized upload reached store: got %d calls, want 2", uploader.calls)
	}
}

func validTestImagePNG(t *testing.T) []byte {
	t.Helper()
	var output bytes.Buffer
	img := image.NewRGBA(image.Rect(0, 0, 2, 2))
	img.Set(0, 0, color.RGBA{B: 255, A: 255})
	if err := png.Encode(&output, img); err != nil {
		t.Fatalf("encode test PNG: %v", err)
	}
	return output.Bytes()
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

func TestListTextHistoryUsesTokenIdentityAndCursorPage(t *testing.T) {
	verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
	clipID := "550e8400-e29b-41d4-a716-446655440000"
	lister := &clipListerStub{page: clipboard.TextHistoryPage{Items: []clipboard.TextHistoryItem{{ID: clipID, Text: "history"}}, HasMore: true, NextCursor: "next-page"}}
	cursor, err := clipboard.EncodeCursor(clipboard.Cursor{ID: clipID, CreatedAt: time.Date(2026, 9, 30, 1, 2, 3, 0, time.UTC)})
	if err != nil {
		t.Fatal(err)
	}
	router := NewRouter(nil, verifier, nil, nil, lister)
	request := httptest.NewRequest(http.MethodGet, "/api/v1/clips?after="+cursor+"&limit=25", nil)
	request.Header.Set("Authorization", "Bearer valid-access-token")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusOK {
		t.Fatalf("expected status 200, got %d: %s", response.Code, response.Body.String())
	}
	if timing := response.Header().Get("Server-Timing"); !strings.Contains(timing, "auth;dur=") || !strings.Contains(timing, "history;dur=") {
		t.Fatalf("expected authentication and history timings, got %q", timing)
	}
	if lister.calls != 1 || lister.userID != 42 || lister.limit != 25 || lister.after == nil || lister.after.ID != clipID {
		t.Fatalf("history query did not use authenticated user/cursor: %+v", lister)
	}
	if !strings.Contains(response.Body.String(), `"next_cursor":"next-page"`) || !strings.Contains(response.Body.String(), `"has_more":true`) {
		t.Fatalf("history page response is incomplete: %s", response.Body.String())
	}
}

func TestListTextHistoryRequiresAuthenticationAndRejectsUserOverride(t *testing.T) {
	for _, test := range []struct {
		name          string
		authorization string
		query         string
		wantStatus    int
	}{
		{name: "missing token", query: "", wantStatus: http.StatusUnauthorized},
		{name: "unknown token", authorization: "Bearer wrong", wantStatus: http.StatusUnauthorized},
		{name: "client user id", authorization: "Bearer valid-access-token", query: "?user_id=7", wantStatus: http.StatusBadRequest},
		{name: "duplicate cursor", authorization: "Bearer valid-access-token", query: "?after=a&after=b", wantStatus: http.StatusBadRequest},
		{name: "invalid limit", authorization: "Bearer valid-access-token", query: "?limit=101", wantStatus: http.StatusBadRequest},
		{name: "invalid cursor", authorization: "Bearer valid-access-token", query: "?after=invalid!", wantStatus: http.StatusBadRequest},
	} {
		t.Run(test.name, func(t *testing.T) {
			verifier := &tokenVerifierStub{userID: 42, token: "valid-access-token"}
			lister := &clipListerStub{}
			router := NewRouter(nil, verifier, nil, nil, lister)
			request := httptest.NewRequest(http.MethodGet, "/api/v1/clips"+test.query, nil)
			if test.authorization != "" {
				request.Header.Set("Authorization", test.authorization)
			}
			response := httptest.NewRecorder()
			router.ServeHTTP(response, request)
			if response.Code != test.wantStatus || lister.calls != 0 {
				t.Fatalf("got status %d and %d lister calls, want status %d and no call", response.Code, lister.calls, test.wantStatus)
			}
		})
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
