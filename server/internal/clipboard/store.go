package clipboard

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"regexp"
	"strings"
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

type Store struct {
	pool *pgxpool.Pool
}

func Open(ctx context.Context, databaseURL string) (*Store, error) {
	pool, err := pgxpool.New(ctx, databaseURL)
	if err != nil {
		return nil, fmt.Errorf("configure clipboard database connection: %w", err)
	}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("connect clipboard store to PostgreSQL: %w", err)
	}
	return &Store{pool: pool}, nil
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
`
	if _, err := store.pool.Exec(ctx, schema); err != nil {
		return fmt.Errorf("create clipboard schema: %w", err)
	}
	return nil
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
	return clip, true, nil
}
