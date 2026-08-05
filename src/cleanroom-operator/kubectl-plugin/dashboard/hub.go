package dashboard

import (
	"context"
	"fmt"
	"net/http"
	"sync"

	log "github.com/sirupsen/logrus"
)

// Hub manages SSE client connections and broadcasts
// resource change events.
type Hub struct {
	mu      sync.RWMutex
	clients map[chan string]struct{}
}

// NewHub creates a new Hub.
func NewHub() *Hub {
	return &Hub{
		clients: make(map[chan string]struct{}),
	}
}

// Run keeps the hub alive until ctx is cancelled.
func (h *Hub) Run(ctx context.Context) {
	<-ctx.Done()
	h.mu.Lock()
	defer h.mu.Unlock()
	for ch := range h.clients {
		close(ch)
		delete(h.clients, ch)
	}
}

// Subscribe registers a new SSE client.
func (h *Hub) Subscribe() chan string {
	ch := make(chan string, 16)
	h.mu.Lock()
	h.clients[ch] = struct{}{}
	h.mu.Unlock()
	return ch
}

// Unsubscribe removes an SSE client.
func (h *Hub) Unsubscribe(ch chan string) {
	h.mu.Lock()
	delete(h.clients, ch)
	h.mu.Unlock()
}

// Broadcast sends a message to all connected clients.
func (h *Hub) Broadcast(event, data string) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	msg := fmt.Sprintf("event: %s\ndata: %s\n\n",
		event, data)
	for ch := range h.clients {
		select {
		case ch <- msg:
		default:
			// Drop if client is slow.
		}
	}
}

// handleSSE serves the Server-Sent Events stream.
func (s *Server) handleSSE(
	w http.ResponseWriter, r *http.Request,
) {
	flusher, ok := w.(http.Flusher)
	if !ok {
		http.Error(w, "SSE not supported",
			http.StatusInternalServerError)
		return
	}

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set(
		"Access-Control-Allow-Origin", "*",
	)

	ch := s.hub.Subscribe()
	defer s.hub.Unsubscribe(ch)

	// Send initial keepalive.
	fmt.Fprint(w, ": connected\n\n")
	flusher.Flush()

	log.Info("SSE client connected")
	defer log.Info("SSE client disconnected")

	for {
		select {
		case msg, ok := <-ch:
			if !ok {
				return
			}
			fmt.Fprint(w, msg)
			flusher.Flush()
		case <-r.Context().Done():
			return
		}
	}
}
