package httpapi

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"clipbridge/server/internal/device"
)

type pairingServiceStub struct {
	registration  device.Registration
	principal     device.Principal
	pending       device.PendingPairing
	requesterID   int64
	requesterCode string
	targetCode    string
	decisionID    string
	accepted      bool
	credential    string
	registrations int
	err           error
}

func (stub *pairingServiceStub) RegisterUnpaired(_ context.Context, registration device.Registration) (device.CredentialResult, error) {
	stub.registration = registration
	stub.registrations++
	return device.CredentialResult{Device: device.Device{ID: 9, MachineName: registration.MachineName, Status: "ACTIVE"}, Credential: "one-time-device-secret"}, stub.err
}

func TestAnonymousDeviceRegistrationIsRateLimitedPerAddress(t *testing.T) {
	service := &pairingServiceStub{}
	router := NewRouterWithDevicePairing(nil, nil, nil, nil, service)
	for attempt := 0; attempt < loginAttemptLimit; attempt++ {
		request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"HOST-PC","platform":"windows","client_version":"1.0.0","device_code":"A1B2C3"}`))
		response := httptest.NewRecorder()
		router.ServeHTTP(response, request)
		if response.Code != http.StatusCreated {
			t.Fatalf("registration %d: expected status %d, got %d: %s", attempt+1, http.StatusCreated, response.Code, response.Body.String())
		}
	}
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"HOST-PC","platform":"windows","client_version":"1.0.0","device_code":"A1B2C3"}`))
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusTooManyRequests {
		t.Fatalf("expected rate limit status %d, got %d: %s", http.StatusTooManyRequests, response.Code, response.Body.String())
	}
	if service.registrations != loginAttemptLimit {
		t.Fatalf("rate limited request reached registration service: got %d calls", service.registrations)
	}
}

func (stub *pairingServiceStub) AuthenticateCredential(_ context.Context, credential string) (device.Principal, error) {
	stub.credential = credential
	if credential != "device-secret" {
		return device.Principal{}, device.ErrInvalidCredential
	}
	return stub.principal, stub.err
}

func (stub *pairingServiceStub) ChangeDeviceCode(context.Context, int64, string) error {
	return stub.err
}

func (stub *pairingServiceStub) CreatePairingRequest(_ context.Context, requesterID int64, requesterCode, targetCode, _ string) (device.PendingPairing, error) {
	stub.requesterID = requesterID
	stub.requesterCode = requesterCode
	stub.targetCode = targetCode
	return stub.pending, stub.err
}

func (stub *pairingServiceStub) ListPendingPairings(context.Context, int64) ([]device.PendingPairing, error) {
	return []device.PendingPairing{stub.pending}, stub.err
}

func (stub *pairingServiceStub) DecidePairingRequest(_ context.Context, _ int64, requestID string, accept bool) error {
	stub.decisionID = requestID
	stub.accepted = accept
	return stub.err
}

func (stub *pairingServiceStub) UnpairDevice(context.Context, int64, int64) error { return stub.err }

func TestAnonymousDeviceRegistrationReturnsCredentialOnce(t *testing.T) {
	service := &pairingServiceStub{}
	router := NewRouterWithDevicePairing(nil, nil, nil, nil, service)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/devices", strings.NewReader(`{"name":"ClipBridge","machine_name":"HOST-PC","platform":"windows","client_version":"1.0.0","device_code":"A1B2C3"}`))
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusCreated {
		t.Fatalf("expected status %d, got %d: %s", http.StatusCreated, response.Code, response.Body.String())
	}
	var result device.CredentialResult
	if err := json.Unmarshal(response.Body.Bytes(), &result); err != nil {
		t.Fatalf("decode registration response: %v", err)
	}
	if result.Device.ID != 9 || result.Credential != "one-time-device-secret" {
		t.Fatalf("unexpected registration result: %+v", result)
	}
	if service.registration.DeviceCode != "A1B2C3" {
		t.Fatalf("device code was not forwarded: %+v", service.registration)
	}
}

func TestPairingRequestUsesAuthenticatedDeviceAndCodes(t *testing.T) {
	service := &pairingServiceStub{
		principal: device.Principal{DeviceID: 7, OwnerID: 11, Name: "HOST-PC"},
		pending:   device.PendingPairing{ID: "request-1", RequesterName: "HOST-PC", RequesterCode: "A1B2C3", ExpiresAt: time.Now().Add(time.Minute)},
	}
	router := NewRouterWithDevicePairing(nil, nil, nil, nil, service)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/pairing-requests", strings.NewReader(`{"requester_code":"A1B2C3","target_code":"Z9Y8X7"}`))
	request.Header.Set("Authorization", "Bearer device-secret")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusAccepted {
		t.Fatalf("expected status %d, got %d: %s", http.StatusAccepted, response.Code, response.Body.String())
	}
	if service.requesterID != 7 || service.requesterCode != "A1B2C3" || service.targetCode != "Z9Y8X7" {
		t.Fatalf("pair request was not scoped to the authenticated device: %+v", service)
	}
}

func TestPendingPairingRequiresDeviceCredential(t *testing.T) {
	service := &pairingServiceStub{principal: device.Principal{DeviceID: 8}}
	router := NewRouterWithDevicePairing(nil, nil, nil, nil, service)
	request := httptest.NewRequest(http.MethodGet, "/api/v1/pairing-requests/pending", nil)
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status %d, got %d", http.StatusUnauthorized, response.Code)
	}
}

func TestPairingDecisionIsBoundToAuthenticatedTarget(t *testing.T) {
	service := &pairingServiceStub{principal: device.Principal{DeviceID: 8}}
	router := NewRouterWithDevicePairing(nil, nil, nil, nil, service)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/pairing-requests/abc/decision", strings.NewReader(`{"accept":true}`))
	request.Header.Set("Authorization", "Bearer device-secret")
	response := httptest.NewRecorder()
	router.ServeHTTP(response, request)
	if response.Code != http.StatusNoContent {
		t.Fatalf("expected status %d, got %d: %s", http.StatusNoContent, response.Code, response.Body.String())
	}
	if service.decisionID != "abc" || !service.accepted {
		t.Fatalf("unexpected pairing decision: %+v", service)
	}
}
