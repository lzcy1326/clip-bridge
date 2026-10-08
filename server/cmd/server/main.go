package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"clipbridge/server/internal/accounts"
	"clipbridge/server/internal/auth"
	"clipbridge/server/internal/clipboard"
	"clipbridge/server/internal/device"
	"clipbridge/server/internal/httpapi"
)

func main() {
	databaseURL, err := environmentValue("CLIPBRIDGE_DATABASE_URL")
	if err != nil {
		log.Fatalf("read database URL: %v", err)
	}
	if databaseURL == "" {
		log.Fatal("CLIPBRIDGE_DATABASE_URL or CLIPBRIDGE_DATABASE_URL_FILE is required")
	}
	jwtSecret, err := environmentValue("CLIPBRIDGE_JWT_SECRET")
	if err != nil {
		log.Fatalf("read JWT secret: %v", err)
	}
	if len(jwtSecret) < 32 {
		log.Fatal("CLIPBRIDGE_JWT_SECRET or CLIPBRIDGE_JWT_SECRET_FILE must contain at least 32 bytes")
	}
	startupContext, startupCancel := context.WithTimeout(context.Background(), 15*time.Second)
	store, err := accounts.Open(startupContext, databaseURL)
	if err != nil {
		startupCancel()
		log.Fatalf("open account store: %v", err)
	}
	if err := store.EnsureSchema(startupContext); err != nil {
		store.Close()
		startupCancel()
		log.Fatalf("ensure account schema: %v", err)
	}
	if bootstrapAdminPassword := os.Getenv("CLIPBRIDGE_BOOTSTRAP_ADMIN_PASSWORD"); bootstrapAdminPassword != "" {
		created, err := store.EnsureBootstrapAdmin(startupContext, bootstrapAdminPassword)
		if err != nil {
			store.Close()
			startupCancel()
			log.Fatalf("ensure bootstrap admin: %v", err)
		}
		if created {
			log.Print("created the default ADMIN account 'admin'; change the default password before exposing the server to an untrusted network")
		}
	}
	loginService, err := auth.NewService(store, []byte(jwtSecret))
	if err != nil {
		store.Close()
		startupCancel()
		log.Fatalf("configure authentication: %v", err)
	}
	deviceStore, err := device.Open(startupContext, databaseURL)
	if err != nil {
		store.Close()
		startupCancel()
		log.Fatalf("open device store: %v", err)
	}
	if err := deviceStore.EnsureSchema(startupContext); err != nil {
		deviceStore.Close()
		store.Close()
		startupCancel()
		log.Fatalf("ensure device schema: %v", err)
	}
	clipboardStore, err := clipboard.Open(startupContext, databaseURL, os.Getenv("CLIPBRIDGE_IMAGE_DIR"))
	if err != nil {
		deviceStore.Close()
		store.Close()
		startupCancel()
		log.Fatalf("open clipboard store: %v", err)
	}
	if err := clipboardStore.EnsureSchema(startupContext); err != nil {
		clipboardStore.Close()
		deviceStore.Close()
		store.Close()
		startupCancel()
		log.Fatalf("ensure clipboard schema: %v", err)
	}
	retentionConfig, err := clipboard.RetentionConfigFromEnvironment()
	if err != nil {
		clipboardStore.Close()
		deviceStore.Close()
		store.Close()
		startupCancel()
		log.Fatalf("configure clipboard history cleanup: %v", err)
	}
	startupCancel()
	defer store.Close()
	defer deviceStore.Close()
	defer clipboardStore.Close()
	cleanupContext, stopCleanup := context.WithCancel(context.Background())
	defer stopCleanup()
	go clipboardStore.RunRetentionCleanup(cleanupContext, retentionConfig)

	address := os.Getenv("CLIPBRIDGE_SERVER_ADDR")
	if address == "" {
		address = ":8080"
	}

	server := &http.Server{
		Addr:              address,
		Handler:           httpapi.NewRouterWithImages(loginService, loginService, deviceStore, clipboardStore, deviceStore, clipboardStore, clipboardStore),
		ReadHeaderTimeout: 5 * time.Second,
	}

	serverErrors := make(chan error, 1)
	go func() {
		log.Printf("ClipBridge server listening on %s", address)
		if err := server.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			serverErrors <- err
		}
	}()

	shutdown := make(chan os.Signal, 1)
	signal.Notify(shutdown, os.Interrupt, syscall.SIGTERM)
	select {
	case err := <-serverErrors:
		log.Fatalf("server failed: %v", err)
	case <-shutdown:
		stopCleanup()
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := server.Shutdown(ctx); err != nil {
			log.Printf("server shutdown failed: %v", err)
		}
	}
}

func environmentValue(name string) (string, error) {
	if value := os.Getenv(name); value != "" {
		return value, nil
	}
	path := os.Getenv(name + "_FILE")
	if path == "" {
		return "", nil
	}
	value, err := os.ReadFile(path)
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(value)), nil
}
