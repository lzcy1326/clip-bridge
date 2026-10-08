package clipboard

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

func TestRetentionConfigFromEnvironmentUsesProductDefaults(t *testing.T) {
	t.Setenv("CLIPBRIDGE_HISTORY_RETENTION_DAYS", "")
	t.Setenv("CLIPBRIDGE_HISTORY_MAX_ITEMS", "")
	t.Setenv("CLIPBRIDGE_HISTORY_MAX_BYTES", "")

	config, err := RetentionConfigFromEnvironment()
	if err != nil {
		t.Fatalf("RetentionConfigFromEnvironment() error = %v", err)
	}
	if config.Days != 15 || config.MaxItems != 500 || config.MaxBytes != 0 || config.RunInterval <= 0 {
		t.Fatalf("default retention config = %+v; want 15 days, 500 items, unlimited byte cap, and a positive interval", config)
	}
	if !config.enabled() {
		t.Fatal("product default retention config unexpectedly disables cleanup")
	}
}

func TestRetentionConfigFromEnvironmentParsesLimits(t *testing.T) {
	t.Setenv("CLIPBRIDGE_HISTORY_RETENTION_DAYS", "30")
	t.Setenv("CLIPBRIDGE_HISTORY_MAX_ITEMS", "250")
	t.Setenv("CLIPBRIDGE_HISTORY_MAX_BYTES", "1073741824")

	config, err := RetentionConfigFromEnvironment()
	if err != nil {
		t.Fatalf("RetentionConfigFromEnvironment() error = %v", err)
	}
	if config.Days != 30 || config.MaxItems != 250 || config.MaxBytes != 1073741824 || !config.enabled() {
		t.Fatalf("parsed retention config = %+v", config)
	}
}

func TestRetentionConfigFromEnvironmentRejectsNegativeOrInvalidLimits(t *testing.T) {
	for _, testCase := range []struct {
		name  string
		days  string
		items string
		bytes string
	}{
		{name: "negative days", days: "-1", items: "0", bytes: "0"},
		{name: "invalid days", days: "forever", items: "0", bytes: "0"},
		{name: "negative item count", days: "0", items: "-1", bytes: "0"},
		{name: "invalid item count", days: "0", items: "many", bytes: "0"},
		{name: "negative size", days: "0", items: "0", bytes: "-1"},
		{name: "invalid size", days: "0", items: "0", bytes: "huge"},
	} {
		t.Run(testCase.name, func(t *testing.T) {
			t.Setenv("CLIPBRIDGE_HISTORY_RETENTION_DAYS", testCase.days)
			t.Setenv("CLIPBRIDGE_HISTORY_MAX_ITEMS", testCase.items)
			t.Setenv("CLIPBRIDGE_HISTORY_MAX_BYTES", testCase.bytes)
			if _, err := RetentionConfigFromEnvironment(); err == nil {
				t.Fatal("RetentionConfigFromEnvironment() unexpectedly accepted invalid config")
			}
		})
	}
}

func TestCleanupHistoryIntegration(t *testing.T) {
	databaseURL := os.Getenv("CLIPBRIDGE_TEST_DATABASE_URL")
	if databaseURL == "" {
		t.Skip("set CLIPBRIDGE_TEST_DATABASE_URL to an isolated PostgreSQL database to run this integration test")
	}

	ctx := context.Background()
	admin, err := pgx.Connect(ctx, databaseURL)
	if err != nil {
		t.Fatalf("connect to isolated PostgreSQL database: %v", err)
	}
	defer admin.Close(ctx)
	schema := "clipbridge_retention_" + fmt.Sprintf("%x", time.Now().UnixNano())
	if _, err := admin.Exec(ctx, `CREATE SCHEMA `+schema); err != nil {
		t.Fatalf("create isolated test schema: %v", err)
	}
	defer admin.Exec(ctx, `DROP SCHEMA `+schema+` CASCADE`)

	poolConfig, err := pgxpool.ParseConfig(databaseURL)
	if err != nil {
		t.Fatalf("parse PostgreSQL configuration: %v", err)
	}
	poolConfig.ConnConfig.RuntimeParams["search_path"] = schema
	pool, err := pgxpool.NewWithConfig(ctx, poolConfig)
	if err != nil {
		t.Fatalf("open isolated test pool: %v", err)
	}
	defer pool.Close()
	if _, err := pool.Exec(ctx, `
CREATE TABLE users (id BIGINT PRIMARY KEY);
CREATE TABLE devices (
    id BIGINT PRIMARY KEY,
    user_id BIGINT NULL,
    pair_group_id BIGINT NULL,
    status TEXT NOT NULL DEFAULT 'ACTIVE'
);
INSERT INTO users (id) VALUES (1);
INSERT INTO devices (id, pair_group_id) VALUES (10, 100), (20, 200);`); err != nil {
		t.Fatalf("create isolated device fixtures: %v", err)
	}
	store := &Store{pool: pool, imageDirectory: t.TempDir()}
	if err := store.EnsureSchema(ctx); err != nil {
		t.Fatalf("ensure clipboard test schema: %v", err)
	}

	oldTime := time.Now().UTC().Add(-48 * time.Hour)
	newTime := time.Now().UTC()
	if _, err := pool.Exec(ctx, `
INSERT INTO clipboard_items (id, user_id, origin_device_id, type, text_content, content_hash, created_at)
VALUES ('expired-text', NULL, 10, 'TEXT', 'old', 'old-hash', $1),
       ('recent-text', NULL, 10, 'TEXT', 'newest', 'new-hash', $2),
       ('other-group', NULL, 20, 'TEXT', 'o', 'other-hash', $2)`, oldTime, newTime); err != nil {
		t.Fatalf("insert text fixtures: %v", err)
	}
	imageKey := validClipID + ".png"
	imagePath := filepath.Join(store.imageDirectory, imageKey)
	imageBytes := []byte{1, 2, 3, 4}
	if err := os.WriteFile(imagePath, imageBytes, 0o600); err != nil {
		t.Fatalf("write image object fixture: %v", err)
	}
	if _, err := pool.Exec(ctx, `
INSERT INTO clipboard_images (id, origin_device_id, content_type, content_hash, byte_size, object_key, created_at)
VALUES ($1, 10, 'image/png', 'image-hash', 4, $2, $3)`, validClipID, imageKey, oldTime); err != nil {
		t.Fatalf("insert image fixture: %v", err)
	}

	result, err := store.CleanupHistory(ctx, 1, 0)
	if err != nil {
		t.Fatalf("clean expired history: %v", err)
	}
	if result.DeletedTextItems != 1 || result.DeletedImageItems != 1 || result.ReclaimedBytes != int64(len(imageBytes)) {
		t.Fatalf("expired cleanup result = %+v", result)
	}
	if _, err := os.Stat(imagePath); !os.IsNotExist(err) {
		t.Fatalf("expired image object still exists (stat error %v)", err)
	}
	var recentCount, otherGroupCount int
	if err := pool.QueryRow(ctx, `SELECT COUNT(*) FROM clipboard_items WHERE id = 'recent-text'`).Scan(&recentCount); err != nil {
		t.Fatal(err)
	}
	if err := pool.QueryRow(ctx, `SELECT COUNT(*) FROM clipboard_items WHERE id = 'other-group'`).Scan(&otherGroupCount); err != nil {
		t.Fatal(err)
	}
	if recentCount != 1 || otherGroupCount != 1 {
		t.Fatalf("cleanup crossed retention boundaries: recent=%d other_group=%d", recentCount, otherGroupCount)
	}

	if _, err := pool.Exec(ctx, `
INSERT INTO clipboard_items (id, user_id, origin_device_id, type, text_content, content_hash, created_at)
VALUES ('older-for-size', NULL, 10, 'TEXT', 'older content', 'older-hash', $1),
       ('newest-for-size', NULL, 10, 'TEXT', 'new', 'size-hash', $2)`, newTime.Add(-time.Hour), newTime.Add(time.Hour)); err != nil {
		t.Fatalf("insert capacity fixtures: %v", err)
	}
	result, err = store.CleanupHistory(ctx, 0, 3)
	if err != nil {
		t.Fatalf("enforce per-group capacity: %v", err)
	}
	if result.DeletedTextItems != 2 {
		t.Fatalf("capacity cleanup result = %+v; want older oversized record and excess recent record removed while retaining new record", result)
	}
	var newestCount int
	if err := pool.QueryRow(ctx, `SELECT COUNT(*) FROM clipboard_items WHERE id = 'newest-for-size'`).Scan(&newestCount); err != nil {
		t.Fatal(err)
	}
	if newestCount != 1 {
		t.Fatal("capacity cleanup removed the newest record")
	}
}
