package clipboard

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestCursorRoundTripAndRejectsMalformedValues(t *testing.T) {
	want := Cursor{CreatedAt: time.Date(2026, 9, 30, 1, 2, 3, 456789000, time.UTC), ID: validClipID}
	encoded, err := EncodeCursor(want)
	if err != nil {
		t.Fatalf("EncodeCursor() error = %v", err)
	}
	got, err := DecodeCursor(encoded)
	if err != nil || got == nil || !got.CreatedAt.Equal(want.CreatedAt) || got.ID != want.ID {
		t.Fatalf("DecodeCursor() = %+v, %v; want %+v", got, err, want)
	}
	for _, invalid := range []string{"not-base64!", "e30", strings.Repeat("a", 2049)} {
		if _, err := DecodeCursor(invalid); err != ErrInvalidCursor {
			t.Errorf("DecodeCursor(%q) error = %v, want ErrInvalidCursor", invalid, err)
		}
	}
	if _, err := EncodeCursor(Cursor{}); err != ErrInvalidCursor {
		t.Fatalf("EncodeCursor(empty) error = %v, want ErrInvalidCursor", err)
	}
}

const validClipID = "550e8400-e29b-41d4-a716-446655440000"

func TestNormalizeTextUpload(t *testing.T) {
	got, err := NormalizeTextUpload(TextUpload{ID: " " + strings.ToUpper(validClipID) + " ", DeviceID: 9, Text: "line one\nline two"})
	if err != nil {
		t.Fatalf("NormalizeTextUpload() error = %v", err)
	}
	if got.ID != validClipID || got.DeviceID != 9 || got.Text != "line one\nline two" {
		t.Fatalf("NormalizeTextUpload() = %+v", got)
	}
}

func TestNormalizeTextUploadAcceptsClientCompactGUID(t *testing.T) {
	compactID := strings.ReplaceAll(validClipID, "-", "")
	got, err := NormalizeTextUpload(TextUpload{ID: compactID, DeviceID: 9, Text: "hello"})
	if err != nil {
		t.Fatalf("NormalizeTextUpload() rejected client GUID: %v", err)
	}
	if got.ID != compactID {
		t.Fatalf("NormalizeTextUpload() changed client GUID: %q", got.ID)
	}
}

func TestNormalizeTextUploadRejectsInvalidFields(t *testing.T) {
	for _, upload := range []TextUpload{
		{ID: "not-a-uuid", DeviceID: 9, Text: "hello"},
		{ID: validClipID, DeviceID: 0, Text: "hello"},
		{ID: validClipID, DeviceID: 9, Text: ""},
		{ID: validClipID, DeviceID: 9, Text: " \r\n\t"},
		{ID: validClipID, DeviceID: 9, Text: string([]byte{0xff})},
		{ID: validClipID, DeviceID: 9, Text: strings.Repeat("x", maxTextBytes+1)},
	} {
		if _, err := NormalizeTextUpload(upload); err != ErrInvalidTextUpload {
			t.Errorf("NormalizeTextUpload(%+v): expected ErrInvalidTextUpload, got %v", upload.ID, err)
		}
	}
}

func TestContentHashIsStableAndTextScoped(t *testing.T) {
	first := ContentHash("hello")
	if first != ContentHash("hello") {
		t.Fatal("same text produced different content hashes")
	}
	if first == ContentHash("TEXThello") {
		t.Fatal("text hash did not include its content type prefix")
	}
	if len(first) != 64 {
		t.Fatalf("content hash has length %d, want 64 hex characters", len(first))
	}
	if ContentHash("é") != ContentHash("e\u0301") {
		t.Fatal("canonically equivalent Unicode text produced different hashes")
	}
}

func TestNormalizeImageUploadAcceptsPngAndRejectsInvalidOrOversizedData(t *testing.T) {
	data := testPNG(t)
	upload, err := NormalizeImageUpload(ImageUpload{
		ID:          " " + strings.ToUpper(validClipID) + " ",
		ContentType: "image/png; charset=binary",
		Data:        data,
	})
	if err != nil {
		t.Fatalf("NormalizeImageUpload() error = %v", err)
	}
	if upload.ID != validClipID || upload.ContentType != "image/png" || !bytes.Equal(upload.Data, data) {
		t.Fatalf("NormalizeImageUpload() = %+v", upload)
	}

	for _, invalid := range []ImageUpload{
		{ID: "not-a-clip-id", ContentType: "image/png", Data: data},
		{ID: validClipID, ContentType: "image/jpeg", Data: data},
		{ID: validClipID, ContentType: "text/plain", Data: data},
		{ID: validClipID, ContentType: "image/png", Data: []byte("not an image")},
	} {
		if _, err := NormalizeImageUpload(invalid); err != ErrInvalidImageUpload {
			t.Errorf("NormalizeImageUpload(%q, %q): got %v, want ErrInvalidImageUpload", invalid.ID, invalid.ContentType, err)
		}
	}
	oversized := append(bytes.Clone(data), bytes.Repeat([]byte{0}, MaxImageUploadBytes-len(data)+1)...)
	if _, err := NormalizeImageUpload(ImageUpload{ID: validClipID, ContentType: "image/png", Data: oversized}); err != ErrImageTooLarge {
		t.Fatalf("oversized upload error = %v, want ErrImageTooLarge", err)
	}
}

func TestImageContentHashIncludesTypeAndBytes(t *testing.T) {
	data := testPNG(t)
	first := ImageContentHash("image/png", data)
	if first != ImageContentHash("image/png", data) {
		t.Fatal("same image produced different content hashes")
	}
	if first == ImageContentHash("image/jpeg", data) || first == ImageContentHash("image/png", append(bytes.Clone(data), 1)) {
		t.Fatal("image content hash did not distinguish type or bytes")
	}
}

func TestPersistImageObjectWritesCompleteFile(t *testing.T) {
	directory := t.TempDir()
	data := testPNG(t)
	if err := persistImageObject(directory, validClipID+".png", data); err != nil {
		t.Fatalf("persistImageObject() error = %v", err)
	}
	stored, err := os.ReadFile(filepath.Join(directory, validClipID+".png"))
	if err != nil || !bytes.Equal(stored, data) {
		t.Fatalf("stored image mismatch: read error=%v", err)
	}
	entries, err := os.ReadDir(directory)
	if err != nil {
		t.Fatalf("read image directory: %v", err)
	}
	if len(entries) != 1 {
		t.Fatalf("image directory contains %d entries, want only final object", len(entries))
	}
}

func testPNG(t *testing.T) []byte {
	t.Helper()
	var buffer bytes.Buffer
	imageData := image.NewRGBA(image.Rect(0, 0, 2, 2))
	imageData.Set(0, 0, color.RGBA{R: 255, A: 255})
	if err := png.Encode(&buffer, imageData); err != nil {
		t.Fatalf("encode test PNG: %v", err)
	}
	return buffer.Bytes()
}
