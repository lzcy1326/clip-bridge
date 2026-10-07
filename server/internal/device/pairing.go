package device

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
)

var (
	ErrDeviceCodeTaken    = errors.New("device code already registered")
	ErrInvalidCredential  = errors.New("invalid device credential")
	ErrPairingUnavailable = errors.New("pairing request unavailable")
	ErrAlreadyPaired      = errors.New("devices are already paired")
	ErrPairingCooldown    = errors.New("pairing request is temporarily blocked")
	deviceCodePattern     = regexp.MustCompile(`^[A-Z0-9]{6}$`)
)

const (
	pairingRequestLifetime = 5 * time.Minute
	pairingRequestLimit    = 10
	pairingRequestWindow   = time.Minute
	pairingRejectCooldown  = time.Minute
)

type PendingPairing struct {
	ID            string    `json:"id"`
	RequesterName string    `json:"requester_name"`
	RequesterCode string    `json:"requester_code"`
	SourceAddress string    `json:"source_address"`
	ExpiresAt     time.Time `json:"expires_at"`
	CreatedAt     time.Time `json:"created_at"`
}

func validDeviceCode(value string) bool { return deviceCodePattern.MatchString(value) }

func hashedDeviceCode(value string) []byte {
	if value == "" {
		return nil
	}
	digest := sha256.Sum256([]byte(strings.ToUpper(value)))
	return digest[:]
}

func hashedCredential(value string) []byte {
	digest := sha256.Sum256([]byte(value))
	return digest[:]
}

func (store *Store) RegisterUnpaired(ctx context.Context, registration Registration) (CredentialResult, error) {
	registration.DeviceCode = strings.ToUpper(strings.TrimSpace(registration.DeviceCode))
	registration, err := NormalizeRegistration(registration)
	if err != nil || !validDeviceCode(registration.DeviceCode) {
		return CredentialResult{}, ErrInvalidRegistration
	}

	credentialBytes := make([]byte, 32)
	if _, err := rand.Read(credentialBytes); err != nil {
		return CredentialResult{}, fmt.Errorf("generate device credential: %w", err)
	}
	credential := base64.RawURLEncoding.EncodeToString(credentialBytes)
	credentialHash := hashedCredential(credential)

	tx, err := store.pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return CredentialResult{}, fmt.Errorf("begin device registration: %w", err)
	}
	defer tx.Rollback(ctx)

	var ownerID int64
	if err := tx.QueryRow(ctx, `INSERT INTO pair_groups DEFAULT VALUES RETURNING id`).Scan(&ownerID); err != nil {
		return CredentialResult{}, fmt.Errorf("create device owner group: %w", err)
	}
	var registered Device
	err = tx.QueryRow(ctx, `
INSERT INTO devices (user_id, pair_group_id, name, machine_name, platform, client_version, device_code_hash, credential_hash)
VALUES (NULL, $1, $2, $3, $4, $5, $6, $7)
RETURNING id, name, machine_name, platform, client_version, status, created_at`,
		ownerID, registration.Name, registration.MachineName, registration.Platform, registration.ClientVersion,
		hashedDeviceCode(registration.DeviceCode), credentialHash,
	).Scan(&registered.ID, &registered.Name, &registered.MachineName, &registered.Platform, &registered.ClientVersion, &registered.Status, &registered.CreatedAt)
	if err != nil {
		var pgError *pgconn.PgError
		if errors.As(err, &pgError) && pgError.Code == "23505" {
			return CredentialResult{}, ErrDeviceCodeTaken
		}
		return CredentialResult{}, fmt.Errorf("register unpaired device: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return CredentialResult{}, fmt.Errorf("commit device registration: %w", err)
	}
	return CredentialResult{Device: registered, Credential: credential}, nil
}

func (store *Store) AuthenticateCredential(ctx context.Context, credential string) (Principal, error) {
	if len(credential) < 32 || len(credential) > 128 {
		return Principal{}, ErrInvalidCredential
	}
	var principal Principal
	err := store.pool.QueryRow(ctx, `
UPDATE devices
SET last_seen_at = NOW()
WHERE credential_hash = $1 AND status = 'ACTIVE' AND pair_group_id IS NOT NULL
  AND EXISTS (
      SELECT 1 FROM pair_groups AS g
      LEFT JOIN users AS u ON u.id = g.owner_user_id
      WHERE g.id = devices.pair_group_id AND (g.owner_user_id IS NULL OR u.status = 'ACTIVE')
  )
RETURNING id, pair_group_id, machine_name`, hashedCredential(credential),
	).Scan(&principal.DeviceID, &principal.OwnerID, &principal.Name)
	if errors.Is(err, pgx.ErrNoRows) {
		return Principal{}, ErrInvalidCredential
	}
	if err != nil {
		return Principal{}, fmt.Errorf("authenticate device credential: %w", err)
	}
	return principal, nil
}

func (store *Store) OwnerGroupForUser(ctx context.Context, userID int64) (int64, error) {
	if userID <= 0 {
		return 0, ErrUserUnavailable
	}
	var ownerID int64
	err := store.pool.QueryRow(ctx, `
SELECT g.id FROM pair_groups AS g
JOIN users AS u ON u.id = g.owner_user_id
WHERE u.id = $1 AND u.status = 'ACTIVE'`, userID).Scan(&ownerID)
	if errors.Is(err, pgx.ErrNoRows) {
		return 0, ErrUserUnavailable
	}
	if err != nil {
		return 0, fmt.Errorf("resolve owner group for user: %w", err)
	}
	return ownerID, nil
}

func (store *Store) ChangeDeviceCode(ctx context.Context, deviceID int64, code string) error {
	code = strings.ToUpper(strings.TrimSpace(code))
	if deviceID <= 0 || !validDeviceCode(code) {
		return ErrInvalidRegistration
	}
	tx, err := store.pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return fmt.Errorf("begin device code change: %w", err)
	}
	defer tx.Rollback(ctx)

	var existingOwnerID int64
	if err := tx.QueryRow(ctx, `SELECT pair_group_id FROM devices WHERE id = $1 AND status = 'ACTIVE' FOR UPDATE`, deviceID).Scan(&existingOwnerID); err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			return ErrInvalidCredential
		}
		return fmt.Errorf("load device owner for code change: %w", err)
	}
	if existingOwnerID <= 0 {
		return ErrInvalidCredential
	}
	var newOwnerID int64
	if err := tx.QueryRow(ctx, `INSERT INTO pair_groups DEFAULT VALUES RETURNING id`).Scan(&newOwnerID); err != nil {
		return fmt.Errorf("create isolated owner group: %w", err)
	}
	if _, err := tx.Exec(ctx, `UPDATE device_pairings SET revoked_at = NOW() WHERE revoked_at IS NULL AND (device_a_id = $1 OR device_b_id = $1)`, deviceID); err != nil {
		return fmt.Errorf("revoke device pairings after code change: %w", err)
	}
	_, err = tx.Exec(ctx, `UPDATE devices SET pair_group_id = $2, device_code_hash = $3 WHERE id = $1`, deviceID, newOwnerID, hashedDeviceCode(code))
	if err != nil {
		var pgError *pgconn.PgError
		if errors.As(err, &pgError) && pgError.Code == "23505" {
			return ErrDeviceCodeTaken
		}
		return fmt.Errorf("update device code: %w", err)
	}
	if _, err := tx.Exec(ctx, `UPDATE pairing_requests SET status = 'EXPIRED', decided_at = NOW() WHERE status = 'PENDING' AND (requester_device_id = $1 OR target_device_id = $1)`, deviceID); err != nil {
		return fmt.Errorf("expire pending requests after code change: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit device code change: %w", err)
	}
	return nil
}

func (store *Store) CreatePairingRequest(ctx context.Context, requesterID int64, requesterCode, targetCode, sourceAddress string) (PendingPairing, error) {
	requesterCode = strings.ToUpper(strings.TrimSpace(requesterCode))
	targetCode = strings.ToUpper(strings.TrimSpace(targetCode))
	if requesterID <= 0 || !validDeviceCode(requesterCode) || !validDeviceCode(targetCode) || requesterCode == targetCode {
		return PendingPairing{}, ErrInvalidRegistration
	}
	tx, err := store.pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return PendingPairing{}, fmt.Errorf("begin pairing request: %w", err)
	}
	defer tx.Rollback(ctx)

	var requesterName string
	var storedRequesterCode []byte
	var requesterOwnerID int64
	err = tx.QueryRow(ctx, `SELECT machine_name, device_code_hash, pair_group_id FROM devices WHERE id = $1 AND status = 'ACTIVE' AND credential_hash IS NOT NULL FOR UPDATE`, requesterID).Scan(&requesterName, &storedRequesterCode, &requesterOwnerID)
	if errors.Is(err, pgx.ErrNoRows) {
		return PendingPairing{}, ErrInvalidCredential
	}
	if err != nil {
		return PendingPairing{}, fmt.Errorf("load requesting device: %w", err)
	}
	if len(storedRequesterCode) != 0 && !equalBytes(storedRequesterCode, hashedDeviceCode(requesterCode)) {
		return PendingPairing{}, ErrInvalidCredential
	}
	var targetID, targetOwnerID int64
	err = tx.QueryRow(ctx, `SELECT id, pair_group_id FROM devices WHERE device_code_hash = $1 AND status = 'ACTIVE'`, hashedDeviceCode(targetCode)).Scan(&targetID, &targetOwnerID)
	if errors.Is(err, pgx.ErrNoRows) {
		return PendingPairing{}, ErrPairingUnavailable
	}
	if err != nil {
		return PendingPairing{}, fmt.Errorf("find pairing target: %w", err)
	}
	if targetID == requesterID {
		return PendingPairing{}, ErrAlreadyPaired
	}
	if requesterOwnerID == targetOwnerID {
		return PendingPairing{}, ErrAlreadyPaired
	}
	var paired bool
	if err := tx.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM device_pairings WHERE revoked_at IS NULL AND ((device_a_id = $1 AND device_b_id = $2) OR (device_a_id = $2 AND device_b_id = $1)))`, requesterID, targetID).Scan(&paired); err != nil {
		return PendingPairing{}, fmt.Errorf("check existing pairing: %w", err)
	}
	if paired {
		return PendingPairing{}, ErrAlreadyPaired
	}
	var existing PendingPairing
	err = tx.QueryRow(ctx, `SELECT id::text, $2, $3, source_address, expires_at, created_at FROM pairing_requests WHERE requester_device_id = $1 AND target_device_id = $4 AND status = 'PENDING' AND expires_at > NOW() ORDER BY created_at DESC LIMIT 1`, requesterID, requesterName, requesterCode, targetID).Scan(&existing.ID, &existing.RequesterName, &existing.RequesterCode, &existing.SourceAddress, &existing.ExpiresAt, &existing.CreatedAt)
	if err == nil {
		return existing, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		return PendingPairing{}, fmt.Errorf("load idempotent pairing request: %w", err)
	}
	var rejectedRecently bool
	if err := tx.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM pairing_requests WHERE requester_device_id = $1 AND target_device_id = $2 AND status = 'REJECTED' AND decided_at > NOW() - ($3 * INTERVAL '1 second'))`, requesterID, targetID, int(pairingRejectCooldown.Seconds())).Scan(&rejectedRecently); err != nil {
		return PendingPairing{}, fmt.Errorf("check pairing rejection cooldown: %w", err)
	}
	if rejectedRecently {
		return PendingPairing{}, ErrPairingCooldown
	}
	var requestCount int
	if err := tx.QueryRow(ctx, `SELECT COUNT(*) FROM pairing_requests WHERE requester_device_id = $1 AND created_at > NOW() - ($2 * INTERVAL '1 second')`, requesterID, int(pairingRequestWindow.Seconds())).Scan(&requestCount); err != nil {
		return PendingPairing{}, fmt.Errorf("check pairing request rate: %w", err)
	}
	if requestCount >= pairingRequestLimit {
		return PendingPairing{}, ErrPairingCooldown
	}
	requestID, err := randomUUID()
	if err != nil {
		return PendingPairing{}, fmt.Errorf("generate pairing request id: %w", err)
	}
	var request PendingPairing
	err = tx.QueryRow(ctx, `
INSERT INTO pairing_requests (id, requester_device_id, target_device_id, requester_code, source_address, status, expires_at)
VALUES ($1::uuid, $2, $3, $4, $5, 'PENDING', NOW() + ($6 * INTERVAL '1 second'))
RETURNING id::text, $7, requester_code, source_address, expires_at, created_at`,
		requestID, requesterID, targetID, requesterCode, boundedSourceAddress(sourceAddress), int(pairingRequestLifetime.Seconds()), requesterName,
	).Scan(&request.ID, &request.RequesterName, &request.RequesterCode, &request.SourceAddress, &request.ExpiresAt, &request.CreatedAt)
	if err != nil {
		return PendingPairing{}, fmt.Errorf("save pairing request: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return PendingPairing{}, fmt.Errorf("commit pairing request: %w", err)
	}
	return request, nil
}

func (store *Store) ListPendingPairings(ctx context.Context, targetID int64) ([]PendingPairing, error) {
	if targetID <= 0 {
		return nil, ErrInvalidCredential
	}
	rows, err := store.pool.Query(ctx, `
SELECT r.id::text, d.machine_name, r.requester_code, r.source_address, r.expires_at, r.created_at
FROM pairing_requests AS r
JOIN devices AS d ON d.id = r.requester_device_id
WHERE r.target_device_id = $1 AND r.status = 'PENDING' AND r.expires_at > NOW()
ORDER BY r.created_at`, targetID)
	if err != nil {
		return nil, fmt.Errorf("list pending pairing requests: %w", err)
	}
	defer rows.Close()
	requests := make([]PendingPairing, 0)
	for rows.Next() {
		var request PendingPairing
		if err := rows.Scan(&request.ID, &request.RequesterName, &request.RequesterCode, &request.SourceAddress, &request.ExpiresAt, &request.CreatedAt); err != nil {
			return nil, fmt.Errorf("read pending pairing request: %w", err)
		}
		requests = append(requests, request)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("iterate pending pairing requests: %w", err)
	}
	return requests, nil
}

func (store *Store) DecidePairingRequest(ctx context.Context, targetID int64, requestID string, accept bool) error {
	if targetID <= 0 || strings.TrimSpace(requestID) == "" {
		return ErrPairingUnavailable
	}
	tx, err := store.pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return fmt.Errorf("begin pairing decision: %w", err)
	}
	defer tx.Rollback(ctx)
	var requesterID, targetOwnerID, requesterOwnerID int64
	err = tx.QueryRow(ctx, `
SELECT r.requester_device_id, target.pair_group_id, requester.pair_group_id
FROM pairing_requests AS r
JOIN devices AS target ON target.id = r.target_device_id
JOIN devices AS requester ON requester.id = r.requester_device_id
WHERE r.id = $1::uuid AND r.target_device_id = $2 AND r.status = 'PENDING' AND r.expires_at > NOW()
FOR UPDATE OF r, target, requester`, requestID, targetID).Scan(&requesterID, &targetOwnerID, &requesterOwnerID)
	if errors.Is(err, pgx.ErrNoRows) {
		return ErrPairingUnavailable
	}
	if err != nil {
		return fmt.Errorf("load pairing request for decision: %w", err)
	}
	status := "REJECTED"
	if accept {
		status = "ACCEPTED"
		if _, err := tx.Exec(ctx, `UPDATE devices SET pair_group_id = $1 WHERE pair_group_id = $2`, targetOwnerID, requesterOwnerID); err != nil {
			return fmt.Errorf("merge owner groups after acceptance: %w", err)
		}
		first, second := targetID, requesterID
		if first > second {
			first, second = second, first
		}
		if _, err := tx.Exec(ctx, `INSERT INTO device_pairings (owner_group_id, device_a_id, device_b_id) VALUES ($1, $2, $3)`, targetOwnerID, first, second); err != nil {
			return fmt.Errorf("persist accepted device pairing: %w", err)
		}
	}
	if _, err := tx.Exec(ctx, `UPDATE pairing_requests SET status = $2, decided_at = NOW() WHERE id = $1::uuid`, requestID, status); err != nil {
		return fmt.Errorf("save pairing decision: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit pairing decision: %w", err)
	}
	return nil
}

func (store *Store) UnpairDevice(ctx context.Context, deviceID, peerID int64) error {
	if deviceID <= 0 || peerID <= 0 || deviceID == peerID {
		return ErrPairingUnavailable
	}
	tx, err := store.pool.BeginTx(ctx, pgx.TxOptions{})
	if err != nil {
		return fmt.Errorf("begin device unpair: %w", err)
	}
	defer tx.Rollback(ctx)
	var exists bool
	if err := tx.QueryRow(ctx, `SELECT EXISTS (SELECT 1 FROM device_pairings WHERE revoked_at IS NULL AND ((device_a_id = $1 AND device_b_id = $2) OR (device_a_id = $2 AND device_b_id = $1)))`, deviceID, peerID).Scan(&exists); err != nil {
		return fmt.Errorf("verify device pairing: %w", err)
	}
	if !exists {
		return ErrPairingUnavailable
	}
	var ownerID int64
	if err := tx.QueryRow(ctx, `INSERT INTO pair_groups DEFAULT VALUES RETURNING id`).Scan(&ownerID); err != nil {
		return fmt.Errorf("create unpaired owner group: %w", err)
	}
	if _, err := tx.Exec(ctx, `UPDATE devices SET pair_group_id = $2 WHERE id = $1`, deviceID, ownerID); err != nil {
		return fmt.Errorf("isolate unpaired device: %w", err)
	}
	if _, err := tx.Exec(ctx, `UPDATE device_pairings SET revoked_at = NOW() WHERE revoked_at IS NULL AND (device_a_id = $1 OR device_b_id = $1)`, deviceID); err != nil {
		return fmt.Errorf("revoke device relationships: %w", err)
	}
	if _, err := tx.Exec(ctx, `UPDATE pairing_requests SET status = 'EXPIRED', decided_at = NOW() WHERE status = 'PENDING' AND (requester_device_id = $1 OR target_device_id = $1)`, deviceID); err != nil {
		return fmt.Errorf("expire pending requests after unpair: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return fmt.Errorf("commit device unpair: %w", err)
	}
	return nil
}

func randomUUID() (string, error) {
	var bytes [16]byte
	if _, err := rand.Read(bytes[:]); err != nil {
		return "", err
	}
	bytes[6] = (bytes[6] & 0x0f) | 0x40
	bytes[8] = (bytes[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", bytes[0:4], bytes[4:6], bytes[6:8], bytes[8:10], bytes[10:16]), nil
}

func equalBytes(left, right []byte) bool {
	if len(left) != len(right) {
		return false
	}
	var mismatch byte
	for index := range left {
		mismatch |= left[index] ^ right[index]
	}
	return mismatch == 0
}

func boundedSourceAddress(value string) string {
	value = strings.TrimSpace(value)
	if len(value) > 128 {
		return value[:128]
	}
	return value
}
