package clipboard

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"image"
	_ "image/jpeg"
	_ "image/png"
	"io"
	"mime"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
)

const MaxImageUploadBytes = 2 << 20

var (
	ErrInvalidImageUpload = errors.New("invalid image upload")
	ErrImageTooLarge      = errors.New("image upload exceeds the size limit")
	ErrImageNotFound      = errors.New("clipboard image not found")
)

type ImageUpload struct {
	ID          string
	ContentType string
	Data        []byte
}

type ImageClip struct {
	ID             string    `json:"id"`
	OriginDeviceID int64     `json:"origin_device_id"`
	Type           string    `json:"type"`
	ContentType    string    `json:"content_type"`
	ContentHash    string    `json:"content_hash"`
	ByteSize       int64     `json:"byte_size"`
	CreatedAt      time.Time `json:"created_at"`
}

type ImageHistoryItem struct {
	ID               string    `json:"id"`
	OriginDeviceID   int64     `json:"origin_device_id"`
	OriginDeviceName string    `json:"origin_device_name"`
	Type             string    `json:"type"`
	ContentType      string    `json:"content_type"`
	ContentHash      string    `json:"content_hash"`
	ByteSize         int64     `json:"byte_size"`
	CreatedAt        time.Time `json:"created_at"`
}

type ImageHistoryPage struct {
	Items      []ImageHistoryItem `json:"items"`
	NextCursor string             `json:"next_cursor,omitempty"`
	HasMore    bool               `json:"has_more"`
}

func NormalizeImageUpload(upload ImageUpload) (ImageUpload, error) {
	upload.ID = strings.ToLower(strings.TrimSpace(upload.ID))
	if len(upload.Data) == 0 {
		return ImageUpload{}, ErrInvalidImageUpload
	}
	if len(upload.Data) > MaxImageUploadBytes {
		return ImageUpload{}, ErrImageTooLarge
	}
	if !clipIDPattern.MatchString(upload.ID) {
		return ImageUpload{}, ErrInvalidImageUpload
	}
	mediaType, _, err := mime.ParseMediaType(upload.ContentType)
	if err != nil || (mediaType != "image/png" && mediaType != "image/jpeg") {
		return ImageUpload{}, ErrInvalidImageUpload
	}
	config, format, err := image.DecodeConfig(bytes.NewReader(upload.Data))
	if err != nil || config.Width < 1 || config.Height < 1 {
		return ImageUpload{}, ErrInvalidImageUpload
	}
	expectedType := map[string]string{"png": "image/png", "jpeg": "image/jpeg"}[format]
	if expectedType == "" || mediaType != expectedType {
		return ImageUpload{}, ErrInvalidImageUpload
	}
	upload.ContentType = mediaType
	return upload, nil
}

func ImageContentHash(contentType string, data []byte) string {
	hasher := sha256.New()
	_, _ = io.WriteString(hasher, "IMAGE\x00"+contentType+"\x00")
	_, _ = hasher.Write(data)
	return hex.EncodeToString(hasher.Sum(nil))
}

func (store *Store) UploadImage(ctx context.Context, userID, deviceID int64, upload ImageUpload) (ImageClip, bool, error) {
	if userID <= 0 || deviceID <= 0 {
		return ImageClip{}, false, ErrInvalidImageUpload
	}
	return store.uploadImage(ctx, userID, 0, deviceID, upload)
}

func (store *Store) UploadImageForOwner(ctx context.Context, ownerID, authenticatedDeviceID int64, upload ImageUpload) (ImageClip, bool, error) {
	if ownerID <= 0 || authenticatedDeviceID <= 0 {
		return ImageClip{}, false, ErrInvalidImageUpload
	}
	return store.uploadImage(ctx, 0, ownerID, authenticatedDeviceID, upload)
}

func (store *Store) ListImages(ctx context.Context, userID int64, after *Cursor, limit int) (ImageHistoryPage, error) {
	if userID <= 0 || limit < 1 || limit > 100 {
		return ImageHistoryPage{}, ErrInvalidImageUpload
	}
	return store.listImages(ctx, userID, 0, after, limit)
}

func (store *Store) ListImagesForOwner(ctx context.Context, ownerID int64, after *Cursor, limit int) (ImageHistoryPage, error) {
	if ownerID <= 0 || limit < 1 || limit > 100 {
		return ImageHistoryPage{}, ErrInvalidImageUpload
	}
	return store.listImages(ctx, 0, ownerID, after, limit)
}

func (store *Store) listImages(ctx context.Context, userID, ownerID int64, after *Cursor, limit int) (ImageHistoryPage, error) {
	if after != nil && (after.ID == "" || after.CreatedAt.IsZero()) {
		return ImageHistoryPage{}, ErrInvalidCursor
	}
	query := `
SELECT i.id, i.origin_device_id, d.machine_name, 'IMAGE', i.content_type, i.content_hash, i.byte_size, i.created_at
FROM clipboard_images AS i
JOIN devices AS d ON d.id = i.origin_device_id
WHERE d.pair_group_id = $1 AND d.status = 'ACTIVE'
  AND (i.expires_at IS NULL OR i.expires_at > NOW())`
	args := []any{ownerID}
	if ownerID <= 0 {
		query = `
SELECT i.id, i.origin_device_id, d.machine_name, 'IMAGE', i.content_type, i.content_hash, i.byte_size, i.created_at
FROM clipboard_images AS i
JOIN devices AS d ON d.id = i.origin_device_id
JOIN users AS u ON u.id = d.user_id
WHERE d.user_id = $1 AND d.status = 'ACTIVE' AND u.status = 'ACTIVE'
  AND (i.expires_at IS NULL OR i.expires_at > NOW())`
		args[0] = userID
	}
	if after != nil {
		query += ` AND (i.created_at, i.id) > ($2, $3)`
		args = append(args, after.CreatedAt, after.ID)
	}
	query += fmt.Sprintf(` ORDER BY i.created_at ASC, i.id ASC LIMIT $%d`, len(args)+1)
	args = append(args, limit+1)
	rows, err := store.pool.Query(ctx, query, args...)
	if err != nil {
		return ImageHistoryPage{}, fmt.Errorf("query clipboard image history: %w", err)
	}
	defer rows.Close()

	page := ImageHistoryPage{Items: make([]ImageHistoryItem, 0, limit)}
	for rows.Next() {
		var item ImageHistoryItem
		if err := rows.Scan(&item.ID, &item.OriginDeviceID, &item.OriginDeviceName, &item.Type, &item.ContentType, &item.ContentHash, &item.ByteSize, &item.CreatedAt); err != nil {
			return ImageHistoryPage{}, fmt.Errorf("read clipboard image history: %w", err)
		}
		if len(page.Items) == limit {
			page.HasMore = true
			break
		}
		page.Items = append(page.Items, item)
	}
	if err := rows.Err(); err != nil {
		return ImageHistoryPage{}, fmt.Errorf("iterate clipboard image history: %w", err)
	}
	if len(page.Items) > 0 {
		last := page.Items[len(page.Items)-1]
		page.NextCursor, err = EncodeCursor(Cursor{CreatedAt: last.CreatedAt, ID: last.ID})
		if err != nil {
			return ImageHistoryPage{}, err
		}
	}
	return page, nil
}

func (store *Store) ReadImage(ctx context.Context, userID int64, id string) (ImageClip, []byte, error) {
	if userID <= 0 || !clipIDPattern.MatchString(strings.TrimSpace(id)) {
		return ImageClip{}, nil, ErrImageNotFound
	}
	return store.readImage(ctx, userID, 0, id)
}

func (store *Store) ReadImageForOwner(ctx context.Context, ownerID int64, id string) (ImageClip, []byte, error) {
	if ownerID <= 0 || !clipIDPattern.MatchString(strings.TrimSpace(id)) {
		return ImageClip{}, nil, ErrImageNotFound
	}
	return store.readImage(ctx, 0, ownerID, id)
}

func (store *Store) readImage(ctx context.Context, userID, ownerID int64, id string) (ImageClip, []byte, error) {
	query := `
SELECT i.id, i.origin_device_id, 'IMAGE', i.content_type, i.content_hash, i.byte_size, i.created_at, i.object_key
FROM clipboard_images AS i JOIN devices AS d ON d.id = i.origin_device_id
WHERE i.id = $1 AND d.pair_group_id = $2 AND d.status = 'ACTIVE'
  AND (i.expires_at IS NULL OR i.expires_at > NOW())`
	args := []any{id, ownerID}
	if ownerID <= 0 {
		query = `
SELECT i.id, i.origin_device_id, 'IMAGE', i.content_type, i.content_hash, i.byte_size, i.created_at, i.object_key
FROM clipboard_images AS i
JOIN devices AS d ON d.id = i.origin_device_id
JOIN users AS u ON u.id = d.user_id
WHERE i.id = $1 AND d.user_id = $2 AND d.status = 'ACTIVE' AND u.status = 'ACTIVE'
  AND (i.expires_at IS NULL OR i.expires_at > NOW())`
		args[1] = userID
	}
	var clip ImageClip
	var objectKey string
	if err := store.pool.QueryRow(ctx, query, args...).Scan(&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentType, &clip.ContentHash, &clip.ByteSize, &clip.CreatedAt, &objectKey); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ImageClip{}, nil, ErrImageNotFound
		}
		return ImageClip{}, nil, fmt.Errorf("load clipboard image metadata: %w", err)
	}
	if filepath.Base(objectKey) != objectKey {
		return ImageClip{}, nil, fmt.Errorf("invalid stored clipboard image key")
	}
	data, err := os.ReadFile(filepath.Join(store.imageDirectory, objectKey))
	if err != nil {
		return ImageClip{}, nil, fmt.Errorf("read clipboard image object: %w", err)
	}
	if int64(len(data)) != clip.ByteSize || len(data) > MaxImageUploadBytes {
		return ImageClip{}, nil, fmt.Errorf("stored clipboard image size does not match metadata")
	}
	return clip, data, nil
}

func (store *Store) uploadImage(ctx context.Context, userID, ownerID, deviceID int64, upload ImageUpload) (ImageClip, bool, error) {
	if userID <= 0 && ownerID <= 0 {
		return ImageClip{}, false, ErrInvalidImageUpload
	}
	store.imageObjectMu.Lock()
	defer store.imageObjectMu.Unlock()

	var err error
	upload, err = NormalizeImageUpload(upload)
	if err != nil {
		return ImageClip{}, false, err
	}
	objectKey := upload.ID + ".png"
	if upload.ContentType == "image/jpeg" {
		objectKey = upload.ID + ".jpg"
	}
	contentHash := ImageContentHash(upload.ContentType, upload.Data)

	transaction, err := store.pool.Begin(ctx)
	if err != nil {
		return ImageClip{}, false, fmt.Errorf("begin image upload transaction: %w", err)
	}
	defer transaction.Rollback(ctx)

	var deviceActive bool
	if ownerID > 0 {
		err = transaction.QueryRow(ctx, `SELECT EXISTS (
SELECT 1 FROM devices WHERE id = $1 AND pair_group_id = $2 AND status = 'ACTIVE')`, deviceID, ownerID).Scan(&deviceActive)
	} else {
		err = transaction.QueryRow(ctx, `SELECT EXISTS (
SELECT 1 FROM devices AS d JOIN users AS u ON u.id = d.user_id
WHERE d.id = $1 AND d.user_id = $2 AND d.status = 'ACTIVE' AND u.status = 'ACTIVE')`, deviceID, userID).Scan(&deviceActive)
	}
	if err != nil {
		return ImageClip{}, false, fmt.Errorf("verify image origin device: %w", err)
	}
	if !deviceActive {
		return ImageClip{}, false, ErrDeviceUnavailable
	}

	var clip ImageClip
	err = transaction.QueryRow(ctx, `
INSERT INTO clipboard_images (id, origin_device_id, content_type, content_hash, byte_size, object_key)
VALUES ($1, $2, $3, $4, $5, $6)
ON CONFLICT (id) DO NOTHING
RETURNING id, origin_device_id, 'IMAGE', content_type, content_hash, byte_size, created_at`,
		upload.ID, deviceID, upload.ContentType, contentHash, len(upload.Data), objectKey,
	).Scan(&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentType, &clip.ContentHash, &clip.ByteSize, &clip.CreatedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return store.refreshDuplicateImage(ctx, transaction, userID, ownerID, deviceID, upload, contentHash)
	}
	if err != nil {
		return ImageClip{}, false, fmt.Errorf("insert uploaded image metadata: %w", err)
	}

	if err := persistImageObject(store.imageDirectory, objectKey, upload.Data); err != nil {
		return ImageClip{}, false, fmt.Errorf("persist uploaded image: %w", err)
	}
	if err := transaction.Commit(ctx); err != nil {
		_ = os.Remove(filepath.Join(store.imageDirectory, objectKey))
		return ImageClip{}, false, fmt.Errorf("commit uploaded image metadata: %w", err)
	}
	return clip, false, nil
}

func (store *Store) refreshDuplicateImage(
	ctx context.Context,
	transaction pgx.Tx,
	userID, ownerID, deviceID int64,
	upload ImageUpload,
	contentHash string,
) (ImageClip, bool, error) {
	query := `
SELECT i.id, i.origin_device_id, 'IMAGE', i.content_type, i.content_hash, i.byte_size, i.created_at
FROM clipboard_images AS i JOIN devices AS d ON d.id = i.origin_device_id
WHERE i.id = $1 AND d.pair_group_id = $2 FOR UPDATE OF i`
	args := []any{upload.ID, ownerID}
	if ownerID <= 0 {
		query = `
SELECT i.id, i.origin_device_id, 'IMAGE', i.content_type, i.content_hash, i.byte_size, i.created_at
FROM clipboard_images AS i
JOIN devices AS d ON d.id = i.origin_device_id
JOIN users AS u ON u.id = d.user_id
WHERE i.id = $1 AND d.user_id = $2 AND u.status = 'ACTIVE' FOR UPDATE OF i`
		args[1] = userID
	}
	var clip ImageClip
	err := transaction.QueryRow(ctx, query, args...).Scan(
		&clip.ID, &clip.OriginDeviceID, &clip.Type, &clip.ContentType, &clip.ContentHash, &clip.ByteSize, &clip.CreatedAt,
	)
	if errors.Is(err, pgx.ErrNoRows) {
		return ImageClip{}, false, ErrClipConflict
	}
	if err != nil {
		return ImageClip{}, false, fmt.Errorf("load existing uploaded image: %w", err)
	}
	if clip.OriginDeviceID != deviceID || clip.ContentType != upload.ContentType || clip.ContentHash != contentHash || clip.ByteSize != int64(len(upload.Data)) {
		return ImageClip{}, false, ErrClipConflict
	}
	err = transaction.QueryRow(ctx, `
UPDATE clipboard_images
SET created_at = GREATEST(NOW(), created_at + INTERVAL '1 microsecond')
WHERE id = $1
RETURNING created_at`, upload.ID).Scan(&clip.CreatedAt)
	if err != nil {
		return ImageClip{}, false, fmt.Errorf("refresh repeated image timestamp: %w", err)
	}
	if err := transaction.Commit(ctx); err != nil {
		return ImageClip{}, false, fmt.Errorf("commit repeated image upload: %w", err)
	}
	return clip, true, nil
}

func persistImageObject(directory, objectKey string, data []byte) error {
	if err := os.MkdirAll(directory, 0o700); err != nil {
		return err
	}
	temporary, err := os.CreateTemp(directory, ".clipbridge-image-*.tmp")
	if err != nil {
		return err
	}
	temporaryPath := temporary.Name()
	defer os.Remove(temporaryPath)
	if _, err := temporary.Write(data); err != nil {
		_ = temporary.Close()
		return err
	}
	if err := temporary.Sync(); err != nil {
		_ = temporary.Close()
		return err
	}
	if err := temporary.Close(); err != nil {
		return err
	}
	return os.Rename(temporaryPath, filepath.Join(directory, objectKey))
}
