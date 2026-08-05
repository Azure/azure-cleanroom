package flexnodeclaimctrl

import (
	"context"
	"fmt"
	"strings"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/trace"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"
)

var fncTracer = otel.Tracer(
	"cleanroom-operator/flexnodeclaim-controller",
)

// saveTrace extracts the W3C traceparent from the current
// span context. Returns (traceParent, traceID).
func saveTrace(
	ctx context.Context,
) (string, string) {
	carrier := propagation.MapCarrier{}
	propagation.TraceContext{}.Inject(ctx, carrier)
	tp := carrier.Get("traceparent")
	var traceID string
	if parts := strings.SplitN(
		tp, "-", 4,
	); len(parts) >= 2 {
		traceID = parts[1]
	}
	return tp, traceID
}

// restoreTrace returns a context with the span restored
// from the given traceparent string.
func restoreTrace(
	ctx context.Context,
	traceParent string,
) context.Context {
	if traceParent == "" {
		return ctx
	}
	carrier := propagation.MapCarrier{}
	carrier.Set("traceparent", traceParent)
	return propagation.TraceContext{}.Extract(
		ctx, carrier,
	)
}

// resolveTraceContext restores trace context using:
//  1. statusTraceParent — continuing an in-flight op
//  2. annotation — first reconcile, link to parent
//  3. new root trace — standalone resource
func resolveTraceContext(
	ctx context.Context,
	obj ctrlclient.Object,
	statusTraceParent string,
) context.Context {
	if statusTraceParent != "" {
		return restoreTrace(ctx, statusTraceParent)
	}
	annotations := obj.GetAnnotations()
	if annotations != nil {
		if tp := annotations[annotationTraceParent]; tp != "" {
			return restoreTrace(ctx, tp)
		}
	}
	return ctx
}

// startSpan creates a new span with the naming convention
// "{Resource}/{Operation}" and the resource name as an
// attribute.
func startSpan(
	ctx context.Context,
	resource string,
	operation string,
	name string,
) (context.Context, trace.Span) {
	spanName := fmt.Sprintf("%s/%s", resource, operation)
	ctx, span := fncTracer.Start(ctx, spanName,
		trace.WithAttributes(
			attribute.String("resource.name", name),
		),
	)

	log := ctrllog.FromContext(ctx)
	ctx = ctrllog.IntoContext(ctx, log)

	return ctx, span
}

const annotationTraceParent = "cleanroom.azure.com/traceparent"
