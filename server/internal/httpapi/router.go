package httpapi

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
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

type ClipUploader interface {
	UploadText(ctx context.Context, userID int64, upload clipboard.TextUpload) (clipboard.TextClip, bool, error)
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

func NewRouter(loginService LoginService, tokenVerifier AccessTokenVerifier, deviceRegistrar DeviceRegistrar, clipUploader ClipUploader) *gin.Engine {
	router := gin.New()
	router.Use(gin.Logger(), gin.Recovery())
	router.GET("/health", func(context *gin.Context) {
		context.JSON(http.StatusOK, gin.H{"status": "ok"})
	})
	limiter := newLoginLimiter()
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
		token, ok := parseBearerToken(context.GetHeader("Authorization"))
		if !ok || tokenVerifier == nil {
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
	router.POST("/api/v1/clips", func(context *gin.Context) {
		context.Header("Cache-Control", "no-store")
		token, ok := parseBearerToken(context.GetHeader("Authorization"))
		if !ok || tokenVerifier == nil {
			context.JSON(http.StatusUnauthorized, gin.H{"error": "unauthorized"})
			return
		}
		userID, err := tokenVerifier.UserIDFromToken(token)
		if err != nil || userID <= 0 {
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
		uploaded, duplicate, err := clipUploader.UploadText(context.Request.Context(), userID, upload)
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
		}
		context.JSON(status, uploaded)
	})
	return router
}

func parseBearerToken(header string) (string, bool) {
	parts := strings.Fields(header)
	if len(parts) != 2 || !strings.EqualFold(parts[0], "Bearer") || len(parts[1]) > 4096 {
		return "", false
	}
	return parts[1], true
}
