package device

import (
	"strings"
	"testing"
)

func TestNormalizeRegistration(t *testing.T) {
	got, err := NormalizeRegistration(Registration{
		Name: "  ClipBridge Local  ", MachineName: " DEV-PC ", Platform: " Windows ", ClientVersion: " 1.0.0 ",
	})
	if err != nil {
		t.Fatalf("NormalizeRegistration() error = %v", err)
	}
	if got.Name != "ClipBridge Local" || got.MachineName != "DEV-PC" || got.Platform != "windows" || got.ClientVersion != "1.0.0" {
		t.Fatalf("unexpected normalized registration: %+v", got)
	}
}

func TestNormalizeRegistrationRejectsInvalidFields(t *testing.T) {
	valid := Registration{Name: "test", MachineName: "pc", Platform: "windows", ClientVersion: "1.0"}
	tests := []struct {
		name   string
		change func(*Registration)
	}{
		{name: "empty name", change: func(value *Registration) { value.Name = "  " }},
		{name: "long name", change: func(value *Registration) { value.Name = strings.Repeat("x", 101) }},
		{name: "empty machine name", change: func(value *Registration) { value.MachineName = "" }},
		{name: "unsupported platform", change: func(value *Registration) { value.Platform = "linux" }},
		{name: "empty client version", change: func(value *Registration) { value.ClientVersion = " " }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			registration := valid
			test.change(&registration)
			if _, err := NormalizeRegistration(registration); err == nil {
				t.Fatal("NormalizeRegistration() accepted invalid registration")
			}
		})
	}
}
