package clipboard

import (
	"context"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

const defaultRetentionInterval = 24 * time.Hour

type RetentionConfig struct {
	Days        int
	MaxItems    int
	MaxBytes    int64
	RunInterval time.Duration
}

type RetentionResult struct {
	DeletedTextItems  int
	DeletedImageItems int
	ReclaimedBytes    int64
}

func RetentionConfigFromEnvironment() (RetentionConfig, error) {
	config := RetentionConfig{Days: 15, MaxItems: 500, RunInterval: defaultRetentionInterval}
	if raw := strings.TrimSpace(os.Getenv("CLIPBRIDGE_HISTORY_RETENTION_DAYS")); raw != "" {
		days, err := strconv.Atoi(raw)
		if err != nil || days < 0 {
			return RetentionConfig{}, fmt.Errorf("CLIPBRIDGE_HISTORY_RETENTION_DAYS must be a non-negative integer")
		}
		config.Days = days
	}
	if raw := strings.TrimSpace(os.Getenv("CLIPBRIDGE_HISTORY_MAX_ITEMS")); raw != "" {
		items, err := strconv.Atoi(raw)
		if err != nil || items < 0 {
			return RetentionConfig{}, fmt.Errorf("CLIPBRIDGE_HISTORY_MAX_ITEMS must be a non-negative integer")
		}
		config.MaxItems = items
	}
	if raw := strings.TrimSpace(os.Getenv("CLIPBRIDGE_HISTORY_MAX_BYTES")); raw != "" {
		bytes, err := strconv.ParseInt(raw, 10, 64)
		if err != nil || bytes < 0 {
			return RetentionConfig{}, fmt.Errorf("CLIPBRIDGE_HISTORY_MAX_BYTES must be a non-negative integer")
		}
		config.MaxBytes = bytes
	}
	return config, nil
}

func (config RetentionConfig) enabled() bool {
	return config.Days > 0 || config.MaxItems > 0 || config.MaxBytes > 0
}

func (store *Store) RunRetentionCleanup(ctx context.Context, config RetentionConfig) {
	if !config.enabled() {
		return
	}
	interval := config.RunInterval
	if interval <= 0 {
		interval = defaultRetentionInterval
	}
	store.runRetentionSweep(ctx, config)
	ticker := time.NewTicker(interval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			store.runRetentionSweep(ctx, config)
		}
	}
}

func (store *Store) runRetentionSweep(ctx context.Context, config RetentionConfig) {
	result, err := store.CleanupHistoryWithLimits(ctx, config.Days, config.MaxItems, config.MaxBytes)
	if err != nil {
		log.Printf("clipboard history cleanup failed: %v", err)
		return
	}
	if result.DeletedTextItems+result.DeletedImageItems > 0 {
		log.Printf("clipboard history cleanup removed %d text items and %d images (%d image bytes reclaimed)",
			result.DeletedTextItems, result.DeletedImageItems, result.ReclaimedBytes)
	}
}

func (store *Store) CleanupHistory(ctx context.Context, retentionDays int, maximumContentBytes int64) (RetentionResult, error) {
	return store.CleanupHistoryWithLimits(ctx, retentionDays, 0, maximumContentBytes)
}

func (store *Store) CleanupHistoryWithLimits(ctx context.Context, retentionDays, maximumItems int, maximumContentBytes int64) (RetentionResult, error) {
	if retentionDays < 0 || maximumItems < 0 || maximumContentBytes < 0 {
		return RetentionResult{}, errors.New("clipboard retention limits must not be negative")
	}
	if retentionDays == 0 && maximumItems == 0 && maximumContentBytes == 0 {
		return RetentionResult{}, nil
	}

	var cutoff any
	if retentionDays > 0 {
		cutoff = time.Now().UTC().AddDate(0, 0, -retentionDays)
	}

	store.imageObjectMu.Lock()
	defer store.imageObjectMu.Unlock()

	transaction, err := store.pool.Begin(ctx)
	if err != nil {
		return RetentionResult{}, fmt.Errorf("begin clipboard history cleanup: %w", err)
	}
	defer transaction.Rollback(ctx)

	rows, err := transaction.Query(ctx, `
WITH records AS (
    SELECT c.id, 'TEXT'::text AS item_type, c.created_at,
           octet_length(c.text_content)::bigint AS content_bytes,
           CASE WHEN d.pair_group_id IS NOT NULL THEN 'pair:' || d.pair_group_id::text
                ELSE 'user:' || COALESCE(d.user_id, c.user_id)::text END AS scope_key,
           NULL::text AS object_key
    FROM clipboard_items AS c
    JOIN devices AS d ON d.id = c.origin_device_id
    WHERE d.pair_group_id IS NOT NULL OR COALESCE(d.user_id, c.user_id) IS NOT NULL
    UNION ALL
    SELECT i.id, 'IMAGE'::text, i.created_at, i.byte_size,
           CASE WHEN d.pair_group_id IS NOT NULL THEN 'pair:' || d.pair_group_id::text
                ELSE 'user:' || d.user_id::text END,
           i.object_key
    FROM clipboard_images AS i
    JOIN devices AS d ON d.id = i.origin_device_id
    WHERE d.pair_group_id IS NOT NULL OR d.user_id IS NOT NULL
), ranked AS (
    SELECT records.*,
           ROW_NUMBER() OVER (
               PARTITION BY scope_key ORDER BY created_at DESC, id DESC
           ) AS item_rank,
           SUM(content_bytes) OVER (
               PARTITION BY scope_key ORDER BY created_at DESC, id DESC
               ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW
           ) AS cumulative_bytes
    FROM records
), targets AS (
    SELECT id, item_type
    FROM ranked
    WHERE ($1::timestamptz IS NOT NULL AND created_at < $1)
       OR ($3::bigint > 0 AND item_rank > $3)
       OR ($2::bigint > 0 AND cumulative_bytes > $2)
), deleted_text AS (
    DELETE FROM clipboard_items AS c USING targets AS t
    WHERE t.item_type = 'TEXT' AND c.id = t.id
    RETURNING c.id
), deleted_images AS (
    DELETE FROM clipboard_images AS i USING targets AS t
    WHERE t.item_type = 'IMAGE' AND i.id = t.id
    RETURNING i.object_key
)
SELECT 'TEXT'::text, id, NULL::text FROM deleted_text
UNION ALL
		SELECT 'IMAGE'::text, NULL::text, object_key FROM deleted_images`, cutoff, maximumContentBytes, maximumItems)
	if err != nil {
		return RetentionResult{}, fmt.Errorf("delete expired clipboard history: %w", err)
	}

	result := RetentionResult{}
	objectKeys := make([]string, 0)
	for rows.Next() {
		var itemType string
		var id, objectKey *string
		if err := rows.Scan(&itemType, &id, &objectKey); err != nil {
			rows.Close()
			return RetentionResult{}, fmt.Errorf("read deleted clipboard history: %w", err)
		}
		if itemType == "TEXT" {
			result.DeletedTextItems++
		} else {
			result.DeletedImageItems++
			if objectKey != nil {
				objectKeys = append(objectKeys, *objectKey)
			}
		}
	}
	if err := rows.Err(); err != nil {
		rows.Close()
		return RetentionResult{}, fmt.Errorf("iterate deleted clipboard history: %w", err)
	}
	rows.Close()
	if err := transaction.Commit(ctx); err != nil {
		return RetentionResult{}, fmt.Errorf("commit clipboard history cleanup: %w", err)
	}

	for _, objectKey := range objectKeys {
		if filepath.Base(objectKey) != objectKey || (filepath.Ext(objectKey) != ".png" && filepath.Ext(objectKey) != ".jpg") {
			log.Printf("skip invalid clipboard image object key during cleanup")
			continue
		}
		var stillReferenced bool
		if err := store.pool.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM clipboard_images WHERE object_key = $1)`, objectKey).Scan(&stillReferenced); err != nil {
			log.Printf("could not check clipboard image object references after cleanup: %v", err)
			continue
		}
		if stillReferenced {
			continue
		}
		objectPath := filepath.Join(store.imageDirectory, objectKey)
		info, err := os.Stat(objectPath)
		if errors.Is(err, os.ErrNotExist) {
			continue
		}
		if err != nil {
			log.Printf("could not inspect clipboard image object after cleanup: %v", err)
			continue
		}
		if err := os.Remove(objectPath); err != nil && !errors.Is(err, os.ErrNotExist) {
			log.Printf("could not remove clipboard image object after cleanup: %v", err)
			continue
		}
		result.ReclaimedBytes += info.Size()
	}
	return result, nil
}
