package clipboard

import (
	"strings"
	"testing"
)

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
