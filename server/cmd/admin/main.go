package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
	"time"

	"clipbridge/server/internal/accounts"
)

func main() {
	if len(os.Args) != 2 || os.Args[1] != "create-user" {
		fmt.Fprintln(os.Stderr, "usage: clipbridge-admin create-user (reads username and password from stdin)")
		os.Exit(2)
	}
	if err := createUser(os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func createUser(input io.Reader, output io.Writer) error {
	scanner := bufio.NewScanner(input)
	if !scanner.Scan() {
		return errors.New("missing username on stdin")
	}
	username := strings.TrimSpace(scanner.Text())
	if !scanner.Scan() {
		return errors.New("missing password on stdin")
	}
	password := scanner.Text()
	if scanner.Scan() && scanner.Text() != "" {
		return errors.New("expected only username and password on stdin")
	}
	if err := scanner.Err(); err != nil {
		return fmt.Errorf("read credentials: %w", err)
	}

	databaseURL := os.Getenv("CLIPBRIDGE_DATABASE_URL")
	if databaseURL == "" {
		return errors.New("CLIPBRIDGE_DATABASE_URL is required")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()

	store, err := accounts.Open(ctx, databaseURL)
	if err != nil {
		return err
	}
	defer store.Close()
	if err := store.EnsureSchema(ctx); err != nil {
		return err
	}
	userID, err := store.CreateUser(ctx, username, password)
	if err != nil {
		return err
	}
	_, err = fmt.Fprintf(output, "Created user %q (id %d, role USER).\n", strings.ToLower(username), userID)
	return err
}
