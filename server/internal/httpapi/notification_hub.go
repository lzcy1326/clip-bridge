package httpapi

import (
	"sync"
)

type clipNotification struct {
	id uint64
}

// clipNotificationHub is a best-effort wakeup channel. Clipboard contents and
// cursors remain in the persistent HTTP history APIs.
type clipNotificationHub struct {
	mu          sync.Mutex
	nextID      uint64
	subscribers map[int64]map[chan clipNotification]struct{}
}

func newClipNotificationHub() *clipNotificationHub {
	return &clipNotificationHub{subscribers: make(map[int64]map[chan clipNotification]struct{})}
}

func (hub *clipNotificationHub) subscribe(ownerID int64) (<-chan clipNotification, func()) {
	channel := make(chan clipNotification, 1)
	hub.mu.Lock()
	if hub.subscribers[ownerID] == nil {
		hub.subscribers[ownerID] = make(map[chan clipNotification]struct{})
	}
	hub.subscribers[ownerID][channel] = struct{}{}
	hub.mu.Unlock()

	var once sync.Once
	return channel, func() {
		once.Do(func() {
			hub.mu.Lock()
			defer hub.mu.Unlock()
			delete(hub.subscribers[ownerID], channel)
			if len(hub.subscribers[ownerID]) == 0 {
				delete(hub.subscribers, ownerID)
			}
			close(channel)
		})
	}
}

func (hub *clipNotificationHub) publish(ownerID int64) {
	if ownerID <= 0 {
		return
	}
	hub.mu.Lock()
	defer hub.mu.Unlock()
	hub.nextID++
	notification := clipNotification{id: hub.nextID}
	for channel := range hub.subscribers[ownerID] {
		select {
		case channel <- notification:
		default:
			// One pending wakeup is enough: clients pull all new data by cursor.
		}
	}
}
