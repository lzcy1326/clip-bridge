package accounts

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"fmt"
	"regexp"
	"strconv"
	"strings"

	"golang.org/x/crypto/argon2"
)

const (
	argonMemory      = 64 * 1024
	argonIterations  = 3
	argonParallelism = 4
	argonKeyLength   = 32
	argonSaltLength  = 16
)

var usernamePattern = regexp.MustCompile(`^[a-zA-Z0-9._-]{3,64}$`)

func NormalizeUsername(username string) (string, error) {
	username = strings.TrimSpace(username)
	if !usernamePattern.MatchString(username) {
		return "", fmt.Errorf("username must be 3-64 characters using letters, digits, dot, underscore, or hyphen")
	}
	return strings.ToLower(username), nil
}

func HashPassword(password string) (string, error) {
	if len(password) < 12 || len(password) > 1024 {
		return "", fmt.Errorf("password must be between 12 and 1024 bytes")
	}
	return hashPassword(password)
}

func hashBootstrapPassword(password string) (string, error) {
	if len(password) == 0 || len(password) > 1024 {
		return "", fmt.Errorf("bootstrap password must be between 1 and 1024 bytes")
	}
	return hashPassword(password)
}

func hashPassword(password string) (string, error) {
	salt := make([]byte, argonSaltLength)
	if _, err := rand.Read(salt); err != nil {
		return "", fmt.Errorf("generate password salt: %w", err)
	}
	hash := argon2.IDKey([]byte(password), salt, argonIterations, argonMemory, argonParallelism, argonKeyLength)

	encodedSalt := base64.RawStdEncoding.EncodeToString(salt)
	encodedHash := base64.RawStdEncoding.EncodeToString(hash)
	return fmt.Sprintf("$argon2id$v=19$m=%d,t=%d,p=%d$%s$%s", argonMemory, argonIterations, argonParallelism, encodedSalt, encodedHash), nil
}

// VerifyPassword checks a password against hashes produced by HashPassword.
// The parameters are deliberately fixed so malformed database values cannot
// request unbounded memory or CPU during login.
func VerifyPassword(password, encodedHash string) bool {
	parts := strings.Split(encodedHash, "$")
	if len(parts) != 6 || parts[0] != "" || parts[1] != "argon2id" || parts[2] != "v=19" {
		return false
	}

	parameters := strings.Split(parts[3], ",")
	if len(parameters) != 3 {
		return false
	}
	values := make(map[string]int, 3)
	for _, parameter := range parameters {
		key, value, ok := strings.Cut(parameter, "=")
		if !ok {
			return false
		}
		if _, exists := values[key]; exists {
			return false
		}
		parsed, err := strconv.Atoi(value)
		if err != nil {
			return false
		}
		values[key] = parsed
	}
	if values["m"] != argonMemory || values["t"] != argonIterations || values["p"] != argonParallelism || len(values) != 3 {
		return false
	}

	salt, err := base64.RawStdEncoding.DecodeString(parts[4])
	if err != nil || len(salt) != argonSaltLength {
		return false
	}
	expected, err := base64.RawStdEncoding.DecodeString(parts[5])
	if err != nil || len(expected) != argonKeyLength {
		return false
	}
	actual := argon2.IDKey([]byte(password), salt, argonIterations, argonMemory, argonParallelism, uint32(len(expected)))
	return subtle.ConstantTimeCompare(actual, expected) == 1
}

func burnPasswordHash(password string) {
	salt := []byte("ClipBridge-login")
	argon2.IDKey([]byte(password), salt, argonIterations, argonMemory, argonParallelism, argonKeyLength)
}
