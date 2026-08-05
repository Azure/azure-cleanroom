package dashboard

import (
	"context"
	"fmt"
	"time"

	log "github.com/sirupsen/logrus"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/watch"
)

// watchResources starts K8s watches on all cleanroom CRDs
// and broadcasts change events via SSE.
func (s *Server) watchResources(ctx context.Context) {
	for _, gvr := range cleanroomGVRs {
		kind := gvr.Kind
		go func() {
			for {
				select {
				case <-ctx.Done():
					return
				default:
				}
				s.runWatch(ctx, kind)
			}
		}()
	}
}

func (s *Server) runWatch(
	ctx context.Context,
	kind string,
) {
	// Find the GVR for this kind.
	var gvrIdx int
	for i, g := range cleanroomGVRs {
		if g.Kind == kind {
			gvrIdx = i
			break
		}
	}

	watcher, err := s.dynClient.
		Resource(cleanroomGVRs[gvrIdx].Resource).
		Namespace(s.namespace).
		Watch(ctx, metav1.ListOptions{})
	if err != nil {
		log.WithError(err).WithField(
			"kind", kind,
		).Warn("Failed to start watch, retrying")
		select {
		case <-time.After(5 * time.Second):
		case <-ctx.Done():
		}
		return
	}
	defer watcher.Stop()

	log.WithField("kind", kind).Info(
		"Watch started",
	)

	for {
		select {
		case event, ok := <-watcher.ResultChan():
			if !ok {
				log.WithField("kind", kind).Info(
					"Watch channel closed, restarting",
				)
				return
			}
			s.handleWatchEvent(kind, event)

		case <-ctx.Done():
			return
		}
	}
}

func (s *Server) handleWatchEvent(
	kind string,
	event watch.Event,
) {
	switch event.Type {
	case watch.Added, watch.Modified, watch.Deleted:
		// Broadcast a refresh signal for the resource
		// kind. The HTMX frontend will re-fetch the
		// relevant partials.
		s.hub.Broadcast(
			"resource-change",
			fmt.Sprintf(
				`{"kind":"%s","type":"%s"}`,
				kind, event.Type,
			),
		)
	}
}
