package clipboard

import (
	"context"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"golang.org/x/text/unicode/norm"
)

const (
	MaxUploadBodyBytes = 1 << 20
	maxTextBytes       = MaxUploadBodyBytes - 4096
)

var (
	ErrInvalidTextUpload = errors.New("invalid text upload")
	ErrDeviceUnavailable = errors.New("device unavailable")
	ErrClipConflict      = errors.New("clip id conflicts with existing content")
	ErrInvalidCursor     = errors.New("invalid clipboard cursor")
	clipIDPattern        = regexp.MustCompile(`(?i)^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$`)
)

type TextUpload struct {
	ID       string `json:"id"`
	DeviceID int64  `json:"device_id"`
	Text     string `json:"text"`
}

type TextClip struct {
	ID             string    `json:"id"`
	OriginDeviceID int64     `json:"origin_device_id"`
	Type           string    `json:"type"`
	ContentHash    string    `json:"content_hash"`
	CreatedAt      time.Time `json:"created_at"`
}

type TextHistoryItem struct {
	ID               string    `json:"id"`
	OriginDeviceID   int64     `json:"origin_device_id"`
	OriginDeviceName string    `json:"origin_device_name"`
	Type             string    `json:"type"`
	Text             string    `json:"text"`
	ContentHash      string    `json:"content_hash"`
	CreatedAt        time.Time `json:"created_at"`
}

type Cursor struct {
	CreatedAt time.Time `json:"created_at"`
	ID        string    `json:"id"`
}

type TextHistoryPage struct {
	Items      []TextHistoryItem `json:"items"`
	NextCursor string            `json:"next_cursor,omitempty"`
	HasMore    bool              `json:"has_more"`
}

func EncodeCursor(cursor Cursor) (string, error) {
	if cursor.ID == "" || cursor.CreatedAt.IsZero() {
		return "", ErrInvalidCursor
	}
	encoded, err := json.Marshal(cursor)
	if err != nil {
		return "", fmt.Errorf("encode clipboard cursor: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(encoded), nil
}

func DecodeCursor(value string) (*Cursor, error) {
	if value == "" {
		return nil, nil
	}
	if len(value) > 2048 {
		return nil, ErrInvalidCursor
	}
	decoded, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil {
		return nil, ErrInvalidCursor
	}
	var cursor Cursor
	if err := json.Unmarshal(decoded, &cursor); err != nil || cursor.ID == "" || cursor.CreatedAt.IsZero() {
		return nil, ErrInvalidCursor
	}
	return &cursor, nil
}

type Store struct {
	pool           *pgxpool.Pool
	imageDirectory string
	imageObjectMu  sync.Mutex
}

func Open(ctx context.Context, databaseURL string, imageDirectory ...string) (*Store, error) {
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		return nil, fmt.Errorf("configure clipboard database connection: %w", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("connect clipboard store to PostgreSQL: %w", err)
	}
	imagesPath := filepath.Join("data", "images")
	if len(imageDirectory) > 0 && strings.TrimSpace(imageDirectory[0]) != "" {
		imagesPath = imageDirectory[0]
	}
	return &Store{pool: pool, imageDirectory: imagesPath}, nil
}

func (store *Store) Close() {
	store.pool.Close()
}

func (store *Store) EnsureSchema(ctx context.Context) error {
	const schema = `
CREATE TABLE IF NOT EXISTS clipboard_items (
    id TEXT PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    origin_device_id BIGINT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    type TEXT NOT NULL CHECK (type IN ('TEXT')),
    text_content TEXT NOT NULL,
    image_object_key TEXT NULL,
    content_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS clipboard_items_user_created_idx ON clipboard_items (user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS clipboard_items_user_created_id_idx ON clipboard_items (user_id, created_at ASC, id ASC);
CREATE INDEX IF NOT EXISTS clipboard_items_origin_created_id_idx ON clipboard_items (origin_device_id, created_at ASC, id ASC);
ALTER TABLE clipboard_items ALTER COLUMN user_id DROP NOT NULL;
CREATE TABLE IF NOT EXISTS clipboard_images (
    id TEXT PRIMARY KEY,
    origin_device_id BIGINT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
    content_type TEXT NOT NULL CHECK (content_type IN ('image/png', 'image/jpeg')),
    content_hash TEXT NOT NULL,
    byte_size BIGINT NOT NULL CHECK (byte_size > 0 AND byte_size <= 2097152),
    object_key TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    expires_at TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS clipboard_images_origin_created_id_idx ON clipboard_images (origin_device_id, created_at ASC, id ASC);
`
	if _, err := store.pool.Exec(ctx, schema); err != nil {
		return fmt.Errorf("create clipboard schema: %w", err)
	}
	return nil
}

func (store *Store) ListTextForOwner(ctx context.Context, ownerID int64, after *Cursor, limit int) (TextHistoryPage, error) {
	if ownerID <= 0 || limit < 1 || limit > 100 {
		return TextHistoryPage{}, ErrInvalidTextUpload
	}
	if after != nil && (after.ID == "" || after.CreatedAt.IsZero()) {
		return TextHistoryPage{}, ErrInvalidCursor
	}
	query := `
SELECT c.id, c.origin_device_id, d.machine_name, c.type, c.text_content, c.content_hash, c.created_at
FROM clipboard_items AS c
JOIN devices AS d ON d.id = c.origin_device_id
LEFT JOIN users AS u ON u.id = c.user_id
WHERE d.pair_group_id = $1 AND (c.user_id IS NULL OR u.status = 'ACTIVE')
  AND (c.expires_at IS NULL OR c.expires_at > NOW())`
	args := []any{ownerID}
	if after != nil {
		query += ` AND (c.created_at, c.id) > ($2, $3)`
		args = append(args, after.CreatedAt, after.ID)
	}
	query += fmt.Sprintf(` ORDER BY c.created_at ASC, c.id ASC LIMIT $%d`, len(args)+1)
	args = append(args, limit+1)
	rows, err := store.pool.Query(ctx, query, args...)
	if err != nil {
		return TextHistoryPage{}, fmt.Errorf("query paired clipboard history: %w", err)
	}
	defer rows.Close()
	page := TextHistoryPage{Items: make([]TextHistoryItem, 0, limit)}
	for rows.Next() {
		var item TextHistoryItem
		if err := rows.Scan(&item.ID, &item.OriginDeviceID, &item.OriginDeviceName, &item.Type, &item.Text, &item.ContentHash, &item.CreatedAt); err != nil {
			return TextHistoryPage{}, fmt.Errorf("read paired clipboard history: %w", err)
		}
		if len(page.Items) == limit {
			page.HasMore = true
			break
		}
		page.Items = append(page.Items, item)
	}
	if err := rows.Err(); err != nil {
		return TextHistoryPage{}, fmt.Errorf("iterate paired clipboard history: %w", err)
	}
	if len(page.Items) > 0 {
		last := page.Items[len(page.Items)-1]
		page.NextCursor, err = EncodeCursor(Cursor{CreatedAt: last.CreatedAt, ID: last.ID})
		if err != nil {
			return TextHistoryPage{}, err
		}
	}
	return page, nil
}

func (store *Store) ListText(ctx context.Context, userID int64, after *Cursor, limit int) (TextHistoryPage, error) {
	if userID <= 0 || limit < 1 || limit > 100 {
		return TextHistoryPage{}, ErrInvalidTextUpload
	}
	if after != nil && (after.ID == "" || after.CreatedAt.IsZero()) {
		return TextHistoryPage{}, ErrInvalidCursor
	}

	var rows pgx.Rows
	var err error
	if after == nil {
		rows, err = store.pool.Query(ctx, `
SELECT c.id, c.origin_device_id, d.machine_name, c.type, c.text_content, c.content_hash, c.created_at
FROM clipboard_items AS c
JOIN users AS u ON u.id = c.user_id
JOIN devices AS d ON d.id = c.origin_device_id AND d.user_id = c.user_id
WHERE c.user_id = $1 AND u.status = 'ACTIVE'
  AND (c.expires_at IS NULL OR c.expires_at > NOW())
ORDER BY c.created_at ASC, c.id ASC
LIMIT $2`, userID, limit+1)
	} else {
		rows, err = store.pool.Query(ctx, `
SELECT c.id, c.origin_device_id, d.machine_name, c.type, c.text_content, c.content_hash, c.created_at
FROM clipboard_items AS c
JOIN users AS u ON u.id = c.user_id
JOIN devices AS d ON d.id = c.origin_device_id AND d.user_id = c.user_id
WHERE c.user_id = $1 AND u.status = 'ACTIVE'
  AND (c.expires_at IS NULL OR c.expires_at > NOW())
  AND (c.created_at, c.id) > ($2, $3)
ORDER BY c.created_at ASC, c.id ASC
LIMIT $4`, userID, after.CreatedAt, after.ID, limit+1)
	}
	if err != nil {
		return TextHistoryPage{}, fmt.Errorf("query clipboard history: %w", err)
	}
	defer rows.Close()

	page := TextHistoryPage{Items: make([]TextHistoryItem, 0, limit)}
	for rows.Next() {
		var item TextHistoryItem
		if err := rows.Scan(&item.ID, &item.OriginDeviceID, &item.OriginDeviceName, &item.Type, &item.Text, &item.ContentHash, &item.CreatedAt); err != nil {
			return TextHistoryPage{}, fmt.Errorf("read clipboard history: %w", err)
		}
		if len(page.Items) == limit {
			page.HasMore = true
			break
		}
		page.Items = append(page.Items, item)
	}
	if err := rows.Err(); err != nil {
		return TextHistoryPage{}, fmt.Errorf("iterate clipboard history: %w", err)
	}
	if len(page.Items) > 0 {
		last := page.Items[len(page.Items)-1]
		page.NextCursor, err = EncodeCursor(Cursor{CreatedAt: last.CreatedAt, ID: last.ID})
		if err != nil {
			return TextHistoryPage{}, err
		}
	}
	return page, nil
}

func NormalizeTextUpload(upload TextUpload) (TextUpload, error) {
	upload.ID = strings.ToLower(strings.TrimSpace(upload.ID))
	if !clipIDPattern.MatchString(upload.ID) || upload.DeviceID <= 0 ||
		len(upload.Text) == 0 || len(upload.Text) > maxTextBytes || !utf8.ValidString(upload.Text) || strings.TrimSpace(upload.Text) == "" {
		return TextUpload{}, ErrInvalidTextUpload
	}
	return upload, nil
}

func ContentHash(text string) string {
	digest := sha256.Sum256([]byte("TEXT" + norm.NFC.String(text)))
	return hex.EncodeToString(digest[:])
}

func (store *Store) UploadText(ctx context.Context, userID int64, upload TextUpload) (TextClip, bool, error) {
	if userID <= 0 {
		return TextClip{}, false, ErrInvalidTextUpload
	}
	upload, err := NormalizeTextUpload(upload)
	if err != nil {
		return TextClip{}, false, err
	}

	var deviceID int64
	err = store.pool.QueryRow(ctx, `
SELECT d.id
FROM devices AS d
JOIN users AS u ON u.id = d.user_id
WHERE d.id = $1 AND d.user_id = $2 AND d.status = 'ACTIVE' AND u.status = 'ACTIVE'`,
		upload.DeviceID, userID,
	).Scan(&deviceID)
	if errors.Is(err, pgx.ErrNoRows) {
		return TextClip{}, false, ErrDeviceUnavailable
	}
	if err != nil {
		return TextClip{}, false, fmt.Errorf("verify clip origin device: %w", err)
	}

	contentHash := ContentHash(upload.Text)
	var clip TextClip
	err = store.pool.QueryRow(ctx, `
INSERT INTO clipboard_items (id, user_id, origin_device_id, type, text_content, content_hash)
SELECT $1, u.id, d.id, 'TEXT', $4, $5
FROM users AS u
JOIN devices AS d ON d.user_id = u.id
WHERE u.id = $2 AND u.status = 'ACTIVE' AND d.id = $3 AND d.status = 'ACTIVE'
ON CONFLICT (id) DO NOTHING
RETURNING id, origin_device_id, type, content_hash, created_at`,
		upload.ID, userID, deviceID, upload.Text, contentHash,
	).Scan(&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentHash, &clip.CreatedAt)
	if err == nil {
		return clip, false, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return TextClip{}, false, fmt.Errorf("store uploaded clip: %w", err)
	}

	var existingText string
	err = store.pool.QueryRow(ctx, `
SELECT id, origin_device_id, type, content_hash, text_content, created_at
FROM clipboard_items
WHERE id = $1 AND user_id = $2`, upload.ID, userID).Scan(
		&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentHash, &existingText, &clip.CreatedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return TextClip{}, false, ErrClipConflict
	}
	if err != nil {
		return TextClip{}, false, fmt.Errorf("load existing uploaded clip: %w", err)
	}
	if clip.OriginDeviceID != deviceID || clip.Type != "TEXT" || clip.ContentHash != contentHash || existingText != upload.Text {
		return TextClip{}, false, ErrClipConflict
	}
	err = store.pool.QueryRow(ctx, `
UPDATE clipboard_items
SET created_at = GREATEST(NOW(), created_at + INTERVAL '1 microsecond')
WHERE id = $1 AND user_id = $2
RETURNING created_at`, upload.ID, userID).Scan(&clip.CreatedAt)
	if err != nil {
		return TextClip{}, false, fmt.Errorf("update repeated clipboard copy time: %w", err)
	}
	return clip, true, nil
}

func (store *Store) UploadTextForOwner(ctx context.Context, ownerID, authenticatedDeviceID int64, upload TextUpload) (TextClip, bool, error) {
	if ownerID <= 0 || authenticatedDeviceID <= 0 {
		return TextClip{}, false, ErrInvalidTextUpload
	}
	upload, err := NormalizeTextUpload(upload)
	if err != nil {
		return TextClip{}, false, err
	}
	if upload.DeviceID != authenticatedDeviceID {
		return TextClip{}, false, ErrDeviceUnavailable
	}
	var active bool
	err = store.pool.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM devices WHERE id = $1 AND pair_group_id = $2 AND status = 'ACTIVE')`, authenticatedDeviceID, ownerID).Scan(&active)
	if err != nil {
		return TextClip{}, false, fmt.Errorf("verify paired clip origin device: %w", err)
	}
	if !active {
		return TextClip{}, false, ErrDeviceUnavailable
	}
	contentHash := ContentHash(upload.Text)
	var clip TextClip
	err = store.pool.QueryRow(ctx, `
INSERT INTO clipboard_items (id, user_id, origin_device_id, type, text_content, content_hash)
VALUES ($1, NULL, $2, 'TEXT', $3, $4)
ON CONFLICT (id) DO NOTHING
RETURNING id, origin_device_id, type, content_hash, created_at`,
		upload.ID, authenticatedDeviceID, upload.Text, contentHash,
	).Scan(&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentHash, &clip.CreatedAt)
	if err == nil {
		return clip, false, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return TextClip{}, false, fmt.Errorf("store paired uploaded clip: %w", err)
	}
	var existingText string
	err = store.pool.QueryRow(ctx, `
SELECT c.id, c.origin_device_id, c.type, c.content_hash, c.text_content, c.created_at
FROM clipboard_items AS c
JOIN devices AS d ON d.id = c.origin_device_id
WHERE c.id = $1 AND d.pair_group_id = $2`, upload.ID, ownerID).Scan(
		&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentHash, &existingText, &clip.CreatedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return TextClip{}, false, ErrClipConflict
	}
	if err != nil {
		return TextClip{}, false, fmt.Errorf("load existing paired uploaded clip: %w", err)
	}
	if clip.OriginDeviceID != authenticatedDeviceID || clip.Type != "TEXT" || clip.ContentHash != contentHash || existingText != upload.Text {
		return TextClip{}, false, ErrClipConflict
	}
	err = store.pool.QueryRow(ctx, `
UPDATE clipboard_items AS c
SET created_at = GREATEST(NOW(), c.created_at + INTERVAL '1 microsecond')
FROM devices AS d
WHERE c.id = $1 AND c.origin_device_id = d.id AND d.pair_group_id = $2
RETURNING c.created_at`, upload.ID, ownerID).Scan(&clip.CreatedAt)
	if err != nil {
		return TextClip{}, false, fmt.Errorf("update repeated paired clipboard copy time: %w", err)
	}
	return clip, true, nil
}
