package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"clipbridge/server/internal/auth"
	"clipbridge/server/internal/clipboard"
	"clipbridge/server/internal/device"
	"github.com/gin-gonic/gin"
)

type LoginService interface {
	Login(ctx context.Context, username, password string) (auth.LoginResult, error)
}

type AccessTokenVerifier interface {
	UserIDFromToken(token string) (int64, error)
}

type DeviceRegistrar interface {
	Register(ctx context.Context, userID int64, registration device.Registration) (device.Device, error)
}

type DevicePairingService interface {
	RegisterUnpaired(ctx context.Context, registration device.Registration) (device.CredentialResult, error)
	AuthenticateCredential(ctx context.Context, credential string) (device.Principal, error)
	ChangeDeviceCode(ctx context.Context, deviceID int64, code string) error
	CreatePairingRequest(ctx context.Context, requesterID int64, requesterCode, targetCode, sourceAddress string) (device.PendingPairing, error)
	ListPendingPairings(ctx context.Context, targetID int64) ([]device.PendingPairing, error)
	DecidePairingRequest(ctx context.Context, targetID int64, requestID string, accept bool) error
	UnpairDevice(ctx context.Context, deviceID, peerID int64) error
}

type ClipUploader interface {
	UploadText(ctx context.Context, userID int64, upload clipboard.TextUpload) (clipboard.TextClip, bool, error)
}

type ClipLister interface {
	ListText(ctx context.Context, userID int64, after *clipboard.Cursor, limit int) (clipboard.TextHistoryPage, error)
}

type PairedClipUploader interface {
	UploadTextForOwner(ctx context.Context, ownerID, authenticatedDeviceID int64, upload clipboard.TextUpload) (clipboard.TextClip, bool, error)
}

type PairedClipLister interface {
	ListTextForOwner(ctx context.Context, ownerID int64, after *clipboard.Cursor, limit int) (clipboard.TextHistoryPage, error)
}

type ImageUploader interface {
	UploadImage(ctx context.Context, userID, deviceID int64, upload clipboard.ImageUpload) (clipboard.ImageClip, bool, error)
}

type PairedImageUploader interface {
	UploadImageForOwner(ctx context.Context, ownerID, authenticatedDeviceID int64, upload clipboard.ImageUpload) (clipboard.ImageClip, bool, error)
}

type ImageLister interface {
	ListImages(ctx context.Context, userID int64, after *clipboard.Cursor, limit int) (clipboard.ImageHistoryPage, error)
}

type PairedImageLister interface {
	ListImagesForOwner(ctx context.Context, ownerID int64, after *clipboard.Cursor, limit int) (clipboard.ImageHistoryPage, error)
}

type ImageReader interface {
	ReadImage(ctx context.Context, userID int64, id string) (clipboard.ImageClip, []byte, error)
}

type PairedImageReader interface {
	ReadImageForOwner(ctx context.Context, ownerID int64, id string) (clipboard.ImageClip, []byte, error)
}

const (
	maxLoginBodyBytes = 16 * 1024
	loginAttemptLimit = 10
	loginWindow       = time.Minute
)

type loginAttempt struct {
	started time.Time
	count   int
}

type loginLimiter struct {
	mu       sync.Mutex
	attempts map[string]loginAttempt
	now      func() time.Time
}

func newLoginLimiter() *loginLimiter {
	return &loginLimiter{attempts: make(map[string]loginAttempt), now: time.Now}
}

func (limiter *loginLimiter) allow(remoteAddress string) bool {
	host, _, err := net.SplitHostPort(remoteAddress)
	if err != nil {
		host = remoteAddress
	}
	now := limiter.now()
	limiter.mu.Lock()
	defer limiter.mu.Unlock()
	for address, attempt := range limiter.attempts {
		if now.Sub(attempt.started) >= loginWindow {
			delete(limiter.attempts, address)
		}
	}
	attempt := limiter.attempts[host]
	if _, exists := limiter.attempts[host]; !exists && len(limiter.attempts) >= 10000 {
		return false
	}
	if attempt.started.IsZero() || now.Sub(attempt.started) >= loginWindow {
		limiter.attempts[host] = loginAttempt{started: now, count: 1}
		return true
	}
	if attempt.count >= loginAttemptLimit {
		return false
	}
	attempt.count++
	limiter.attempts[host] = attempt
	return true
}

func NewRouter(loginService LoginService, tokenVerifier AccessTokenVerifier, deviceRegistrar DeviceRegistrar, clipUploader ClipUploader, clipListers ...ClipLister) *gin.Engine {
	return NewRouterWithDevicePairing(loginService, tokenVerifier, deviceRegistrar, clipUploader, nil, clipListers...)
}

func NewRouterWithDevicePairing(loginService LoginService, tokenVerifier AccessTokenVerifier, deviceRegistrar DeviceRegistrar, clipUploader ClipUploader, pairingService DevicePairingService, clipListers ...ClipLister) *gin.Engine {
	return NewRouterWithImages(loginService, tokenVerifier, deviceRegistrar, clipUploader, pairingService, nil, clipListers...)
}

func NewRouterWithImages(
	loginService LoginService,
	tokenVerifier AccessTokenVerifier,
	deviceRegistrar DeviceRegistrar,
	clipUploader ClipUploader,
	pairingService DevicePairingService,
	imageUploader ImageUploader,
	clipListers ...ClipLister,
) *gin.Engine {
	router := gin.New()
	router.Use(gin.Logger(), gin.Recovery())
	notificationHub := newClipNotificationHub()
	router.GET("/health", func(context *gin.Context) {
		context.JSON(http.StatusOK, gin.H{"status": "ok"})
	})
	router.GET("/api/v1/events", func(context *gin.Context) {
		userID, principal, deviceAuthenticated, ok := resolveRequestIdentity(context, tokenVerifier, pairingService)
		if !ok {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		ownerID := userID
		if deviceAuthenticated {
			ownerID = principal.OwnerID
		}
		flusher, supported := context.Writer.(http.Flusher)
		if !supported {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "streaming_unavailable"})
			return
		}
		context.Header("Content-Type", "text/event-stream")
		context.Header("Cache-Control", "no-cache, no-transform")
		context.Header("Connection", "keep-alive")
		context.Header("X-Accel-Buffering", "no")
		context.Status(http.StatusOK)
		channel, unsubscribe := notificationHub.subscribe(ownerID)
		defer unsubscribe()
		_, _ = context.Writer.Write([]byte(": connected\n\n"))
		flusher.Flush()
		heartbeat := time.NewTicker(20 * time.Second)
		defer heartbeat.Stop()
		maxStreamAge := time.NewTimer(5 * time.Minute)
		defer maxStreamAge.Stop()
		for {
			select {
			case notification, open := <-channel:
				if !open {
					return
				}
				if _, err := fmt.Fprintf(context.Writer, "id: %d\nevent: clipboard\ndata: changed\n\n", notification.id); err != nil {
					return
				}
				flusher.Flush()
			case <-heartbeat.C:
				if _, err := context.Writer.Write([]byte(": heartbeat\n\n")); err != nil {
					return
				}
				flusher.Flush()
			case <-maxStreamAge.C:
				return
			case <-context.Request.Context().Done():
				return
			}
		}
	})
	limiter := newLoginLimiter()
	deviceRegistrationLimiter := newLoginLimiter()
	router.POST("/api/v1/auth/login", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		context.Header("Pragma", "no-cache")
		if !limiter.allow(context.Request.RemoteAddr) {
			context.JSON(http.StatusTooManyRequests, gin.H{"error": "rate_limited"})
			return
		}
		if loginService == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, maxLoginBodyBytes)
		decoder := json.NewDecoder(context.Request.Body)
		decoder.DisallowUnknownFields()
		var request struct {
			Username string `json:"username"`
			Password string `json:"password"`
		}
		if err := decoder.Decode(&request); err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if strings.TrimSpace(request.Username) == "" || request.Password == "" || len(request.Password) > 1024 {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		result, err := loginService.Login(context.Request.Context(), request.Username, request.Password)
		if errors.Is(err, auth.ErrInvalidCredentials) {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "invalid_credentials"})
			return
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		context.JSON(http.StatusOK, result)
	})
	router.POST("/api/v1/devices", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, 8*1024)
		decoder := json.NewDecoder(context.Request.Body)
		decoder.DisallowUnknownFields()
		var registration device.Registration
		if err := decoder.Decode(&registration); err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		token, hasToken := parseBearerToken(context.GetHeader("Authorization"))
		if !hasToken {
			if pairingService == nil {
				context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
				return
			}
			if !deviceRegistrationLimiter.allow(context.Request.RemoteAddr) {
				context.JSON(http.StatusTooManyRequests, gin.H{"error": "rate_limited"})
				return
			}
			registered, err := pairingService.RegisterUnpaired(context.Request.Context(), registration)
			if errors.Is(err, device.ErrInvalidRegistration) {
				context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
				return
			}
			if errors.Is(err, device.ErrDeviceCodeTaken) {
				context.JSON(http.StatusConflict, gin.H{"error": "device_code_taken"})
				return
			}
			if err != nil {
				context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
				return
			}
			context.JSON(http.StatusCreated, registered)
			return
		}
		if tokenVerifier == nil {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		userID, err := tokenVerifier.UserIDFromToken(token)
		if err != nil || userID <= 0 {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		if deviceRegistrar == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		registered, err := deviceRegistrar.Register(context.Request.Context(), userID, registration)
		if errors.Is(err, device.ErrInvalidRegistration) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if errors.Is(err, device.ErrUserUnavailable) {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		context.JSON(http.StatusCreated, registered)
	})
	router.POST("/api/v1/pairing-requests", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		principal, ok := authenticateDeviceRequest(context, pairingService)
		if !ok {
			return
		}
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, 4*1024)
		decoder := json.NewDecoder(context.Request.Body)
		decoder.DisallowUnknownFields()
		var request struct {
			RequesterCode string `json:"requester_code"`
			TargetCode    string `json:"target_code"`
		}
		if err := decoder.Decode(&request); err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		pending, err := pairingService.CreatePairingRequest(context.Request.Context(), principal.DeviceID, request.RequesterCode, request.TargetCode, context.ClientIP())
		writePairingError(context, err)
		if err != nil {
			return
		}
		context.JSON(http.StatusAccepted, pending)
	})
	router.GET("/api/v1/pairing-requests/pending", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		principal, ok := authenticateDeviceRequest(context, pairingService)
		if !ok {
			return
		}
		pending, err := pairingService.ListPendingPairings(context.Request.Context(), principal.DeviceID)
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		context.JSON(http.StatusOK, gin.H{"items": pending})
	})
	router.POST("/api/v1/pairing-requests/:id/decision", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		principal, ok := authenticateDeviceRequest(context, pairingService)
		if !ok {
			return
		}
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, 2*1024)
		decoder := json.NewDecoder(context.Request.Body)
		decoder.DisallowUnknownFields()
		var request struct {
			Accept bool `json:"accept"`
		}
		if err := decoder.Decode(&request); err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		err := pairingService.DecidePairingRequest(context.Request.Context(), principal.DeviceID, context.Param("id"), request.Accept)
		writePairingError(context, err)
		if err == nil {
			context.Status(http.StatusNoContent)
		}
	})
	router.PATCH("/api/v1/devices/me/code", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		principal, ok := authenticateDeviceRequest(context, pairingService)
		if !ok {
			return
		}
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, 2*1024)
		decoder := json.NewDecoder(context.Request.Body)
		decoder.DisallowUnknownFields()
		var request struct {
			DeviceCode string `json:"device_code"`
		}
		if err := decoder.Decode(&request); err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		err := pairingService.ChangeDeviceCode(context.Request.Context(), principal.DeviceID, request.DeviceCode)
		writePairingError(context, err)
		if err == nil {
			context.Status(http.StatusNoContent)
		}
	})
	router.DELETE("/api/v1/devices/:peerID/pairing", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		principal, ok := authenticateDeviceRequest(context, pairingService)
		if !ok {
			return
		}
		peerID, err := strconv.ParseInt(context.Param("peerID"), 10, 64)
		if err != nil || peerID <= 0 {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		err = pairingService.UnpairDevice(context.Request.Context(), principal.DeviceID, peerID)
		writePairingError(context, err)
		if err == nil {
			context.Status(http.StatusNoContent)
		}
	})
	router.POST("/api/v1/clips", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		userID, principal, deviceAuthenticated, ok := resolveRequestIdentity(context, tokenVerifier, pairingService)
		if !ok {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		if clipUploader == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, clipboard.MaxUploadBodyBytes)
		decoder := json.NewDecoder(context.Request.Body)
		decoder.DisallowUnknownFields()
		var upload clipboard.TextUpload
		if err := decoder.Decode(&upload); err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if err := decoder.Decode(&struct{}{}); !errors.Is(err, io.EOF) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		var uploaded clipboard.TextClip
		var duplicate bool
		var err error
		if deviceAuthenticated {
			pairedUploader, supported := clipUploader.(PairedClipUploader)
			if !supported {
				context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
				return
			}
			uploaded, duplicate, err = pairedUploader.UploadTextForOwner(context.Request.Context(), principal.OwnerID, principal.DeviceID, upload)
		} else {
			uploaded, duplicate, err = clipUploader.UploadText(context.Request.Context(), userID, upload)
		}
		if errors.Is(err, clipboard.ErrInvalidTextUpload) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if errors.Is(err, clipboard.ErrDeviceUnavailable) {
			context.JSON(http.StatusForbidden, gin.H{"error": "device_unavailable"})
			return
		}
		if errors.Is(err, clipboard.ErrClipConflict) {
			context.JSON(http.StatusConflict, gin.H{"error": "clip_conflict"})
			return
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		status := http.StatusCreated
		if duplicate {
			status = http.StatusOK
		} else {
			ownerID := userID
			if deviceAuthenticated {
				ownerID = principal.OwnerID
			}
			notificationHub.publish(ownerID)
		}
		context.JSON(status, uploaded)
	})
	router.POST("/api/v1/images", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		userID, principal, deviceAuthenticated, ok := resolveRequestIdentity(context, tokenVerifier, pairingService)
		if !ok {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		if imageUploader == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		deviceID := principal.DeviceID
		if !deviceAuthenticated {
			parsedDeviceID, err := strconv.ParseInt(context.GetHeader("X-Device-ID"), 10, 64)
			if err != nil || parsedDeviceID <= 0 {
				context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
				return
			}
			deviceID = parsedDeviceID
		}
		context.Request.Body = http.MaxBytesReader(context.Writer, context.Request.Body, clipboard.MaxImageUploadBytes)
		data, err := io.ReadAll(context.Request.Body)
		if err != nil {
			var tooLarge *http.MaxBytesError
			if errors.As(err, &tooLarge) {
				context.JSON(http.StatusRequestEntityTooLarge, gin.H{"error": "image_too_large"})
				return
			}
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		upload := clipboard.ImageUpload{
			ID:          context.GetHeader("X-Clip-ID"),
			ContentType: context.GetHeader("Content-Type"),
			Data:        data,
		}
		var uploaded clipboard.ImageClip
		var duplicate bool
		if deviceAuthenticated {
			pairedUploader, supported := imageUploader.(PairedImageUploader)
			if !supported {
				context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
				return
			}
			uploaded, duplicate, err = pairedUploader.UploadImageForOwner(context.Request.Context(), principal.OwnerID, principal.DeviceID, upload)
		} else {
			uploaded, duplicate, err = imageUploader.UploadImage(context.Request.Context(), userID, deviceID, upload)
		}
		if errors.Is(err, clipboard.ErrImageTooLarge) {
			context.JSON(http.StatusRequestEntityTooLarge, gin.H{"error": "image_too_large"})
			return
		}
		if errors.Is(err, clipboard.ErrInvalidImageUpload) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		if errors.Is(err, clipboard.ErrDeviceUnavailable) {
			context.JSON(http.StatusForbidden, gin.H{"error": "device_unavailable"})
			return
		}
		if errors.Is(err, clipboard.ErrClipConflict) {
			context.JSON(http.StatusConflict, gin.H{"error": "clip_conflict"})
			return
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		status := http.StatusCreated
		if duplicate {
			status = http.StatusOK
		} else {
			ownerID := userID
			if deviceAuthenticated {
				ownerID = principal.OwnerID
			}
			notificationHub.publish(ownerID)
		}
		context.JSON(status, uploaded)
	})
	router.GET("/api/v1/images", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		userID, principal, deviceAuthenticated, ok := resolveRequestIdentity(context, tokenVerifier, pairingService)
		if !ok {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		lister, supported := imageUploader.(ImageLister)
		if !supported || lister == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		query := context.Request.URL.Query()
		if !validClipListQuery(query) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		limit := 100
		if rawLimit := query.Get("limit"); rawLimit != "" {
			parsedLimit, err := strconv.Atoi(rawLimit)
			if err != nil || parsedLimit < 1 || parsedLimit > 100 {
				context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
				return
			}
			limit = parsedLimit
		}
		after, err := clipboard.DecodeCursor(query.Get("after"))
		if err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_cursor"})
			return
		}
		var page clipboard.ImageHistoryPage
		if deviceAuthenticated {
			pairedLister, supported := imageUploader.(PairedImageLister)
			if !supported {
				context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
				return
			}
			page, err = pairedLister.ListImagesForOwner(context.Request.Context(), principal.OwnerID, after, limit)
		} else {
			page, err = lister.ListImages(context.Request.Context(), userID, after, limit)
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		context.JSON(http.StatusOK, page)
	})
	router.GET("/api/v1/images/:id", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		userID, principal, deviceAuthenticated, ok := resolveRequestIdentity(context, tokenVerifier, pairingService)
		if !ok {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		reader, supported := imageUploader.(ImageReader)
		if !supported || reader == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		var clip clipboard.ImageClip
		var data []byte
		var err error
		if deviceAuthenticated {
			pairedReader, supported := imageUploader.(PairedImageReader)
			if !supported {
				context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
				return
			}
			clip, data, err = pairedReader.ReadImageForOwner(context.Request.Context(), principal.OwnerID, context.Param("id"))
		} else {
			clip, data, err = reader.ReadImage(context.Request.Context(), userID, context.Param("id"))
		}
		if errors.Is(err, clipboard.ErrImageNotFound) {
			context.JSON(http.StatusNotFound, gin.H{"error": "not_found"})
			return
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		context.Header("Content-Type", clip.ContentType)
		context.Header("X-Content-Hash", clip.ContentHash)
		context.Header("X-Created-At", clip.CreatedAt.UTC().Format(time.RFC3339Nano))
		context.Data(http.StatusOK, clip.ContentType, data)
	})
	router.GET("/api/v1/clips", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		authStarted := time.Now()
		userID, principal, deviceAuthenticated, ok := resolveRequestIdentity(context, tokenVerifier, pairingService)
		authDuration := time.Since(authStarted)
		if !ok {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		if len(clipListers) == 0 || clipListers[0] == nil {
			context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
			return
		}
		query := context.Request.URL.Query()
		if !validClipListQuery(query) {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
			return
		}
		limit := 100
		if rawLimit := query.Get("limit"); rawLimit != "" {
			parsedLimit, err := strconv.Atoi(rawLimit)
			if err != nil || parsedLimit < 1 || parsedLimit > 100 {
				context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
				return
			}
			limit = parsedLimit
		}
		after, err := clipboard.DecodeCursor(query.Get("after"))
		if err != nil {
			context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_cursor"})
			return
		}
		var page clipboard.TextHistoryPage
		historyStarted := time.Now()
		if deviceAuthenticated {
			pairedLister, supported := clipListers[0].(PairedClipLister)
			if !supported {
				context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
				return
			}
			page, err = pairedLister.ListTextForOwner(context.Request.Context(), principal.OwnerID, after, limit)
		} else {
			page, err = clipListers[0].ListText(context.Request.Context(), userID, after, limit)
		}
		if err != nil {
			context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
			return
		}
		context.Header("Server-Timing", fmt.Sprintf("auth;dur=%.2f, history;dur=%.2f", float64(authDuration.Microseconds())/1000, float64(time.Since(historyStarted).Microseconds())/1000))
		context.JSON(http.StatusOK, page)
	})
	return router
}

func validClipListQuery(query url.Values) bool {
	for key, values := range query {
		if (key != "after" && key != "limit") || len(values) != 1 {
			return false
		}
	}
	return true
}

func parseBearerToken(header string) (string, bool) {
	parts := strings.Fields(header)
	if len(parts) != 2 || !strings.EqualFold(parts[0], "Bearer") || len(parts[1]) > 4096 {
		return "", false
	}
	return parts[1], true
}

func authenticateDeviceRequest(context *gin.Context, pairingService DevicePairingService) (device.Principal, bool) {
	if pairingService == nil {
		context.JSON(http.StatusServiceUnavailable, gin.H{"error": "service_unavailable"})
		return device.Principal{}, false
	}
	token, ok := parseBearerToken(context.GetHeader("Authorization"))
	if !ok {
		context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
		return device.Principal{}, false
	}
	principal, err := pairingService.AuthenticateCredential(context.Request.Context(), token)
	if errors.Is(err, device.ErrInvalidCredential) {
		context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
		return device.Principal{}, false
	}
	if err != nil {
		context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
		return device.Principal{}, false
	}
	return principal, true
}

func resolveRequestIdentity(context *gin.Context, tokenVerifier AccessTokenVerifier, pairingService DevicePairingService) (int64, device.Principal, bool, bool) {
	token, ok := parseBearerToken(context.GetHeader("Authorization"))
	if !ok {
		return 0, device.Principal{}, false, false
	}
	if tokenVerifier != nil {
		userID, err := tokenVerifier.UserIDFromToken(token)
		if err == nil && userID > 0 {
			return userID, device.Principal{}, false, true
		}
	}
	if pairingService == nil {
		return 0, device.Principal{}, false, false
	}
	principal, err := pairingService.AuthenticateCredential(context.Request.Context(), token)
	if err != nil {
		return 0, device.Principal{}, false, false
	}
	return 0, principal, true, true
}

func writePairingError(context *gin.Context, err error) {
	switch {
	case err == nil:
		return
	case errors.Is(err, device.ErrInvalidRegistration):
		context.JSON(http.StatusBadRequest, gin.H{"error": "invalid_request"})
	case errors.Is(err, device.ErrInvalidCredential):
		context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
	case errors.Is(err, device.ErrDeviceCodeTaken):
		context.JSON(http.StatusConflict, gin.H{"error": "device_code_taken"})
	case errors.Is(err, device.ErrPairingUnavailable):
		context.JSON(http.StatusNotFound, gin.H{"error": "pairing_request_unavailable"})
	case errors.Is(err, device.ErrAlreadyPaired):
		context.JSON(http.StatusConflict, gin.H{"error": "already_paired"})
	case errors.Is(err, device.ErrPairingCooldown):
		context.JSON(http.StatusTooManyRequests, gin.H{"error": "pairing_rate_limited"})
	default:
		context.JSON(http.StatusInternalServerError, gin.H{"error": "internal_error"})
	}
}
