package main

import (
	"context"
	"errors"
	"log"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"clipbridge/server/internal/accounts"
	"clipbridge/server/internal/auth"
	"clipbridge/server/internal/httpapi"
)

func main() {
	databaseURL := os.Getenv("CLIPBRIDGE_DATABASE_URL")
	if databaseURL == "" {
		log.Fatal("CLIPBRIDGE_DATABASE_URL is required")
	}
	jwtSecret := os.Getenv("CLIPBRIDGE_JWT_SECRET")
	if len(jwtSecret) < 32 {
		log.Fatal("CLIPBRIDGE_JWT_SECRET must contain at least 32 bytes")
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
	loginService, err := auth.NewService(store, []byte(jwtSecret))
	startupCancel()
	if err != nil {
		store.Close()
		log.Fatalf("configure authentication: %v", err)
	}
	defer store.Close()

	address := os.Getenv("CLIPBRIDGE_SERVER_ADDR")
	if address == "" {
		address = ":8080"
	}

	server := &http.Server{
		Addr:              address,
		Handler:           httpapi.NewRouter(loginService),
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
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		if err := server.Shutdown(ctx); err != nil {
			log.Printf("server shutdown failed: %v", err)
		}
	}
}
