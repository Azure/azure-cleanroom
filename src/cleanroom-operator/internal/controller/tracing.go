package controller

import (
	"context"
	"fmt"
	"strings"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/codes"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/trace"
	ctrlclient "sigs.k8s.io/controller-runtime/pkg/client"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"
)

const (
	// AnnotationTraceParent is the annotation used to
	// propagate W3C traceparent from parent to child CRs.
	AnnotationTraceParent = "cleanroom.azure.com/traceparent"
)

var crTracer = otel.Tracer(
	"cleanroom-operator/controller",
)

// saveTrace extracts the W3C traceparent from the current
// span context. Returns (traceParent, lastOperationTraceId).
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
// from the given traceparent string. If empty or invalid,
// returns the original context unchanged.
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

// resolveTraceContext restores trace context using the
// dual-source resolution order:
//  1. statusTraceParent — continuing an in-flight operation
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
		if tp := annotations[AnnotationTraceParent]; tp != "" {
			return restoreTrace(ctx, tp)
		}
	}
	return ctx
}

// injectTraceAnnotation sets the traceparent annotation on
// a K8s object from the current span context.
func injectTraceAnnotation(
	ctx context.Context,
	obj ctrlclient.Object,
) {
	carrier := propagation.MapCarrier{}
	propagation.TraceContext{}.Inject(ctx, carrier)
	tp := carrier.Get("traceparent")
	if tp == "" {
		return
	}
	annotations := obj.GetAnnotations()
	if annotations == nil {
		annotations = map[string]string{}
	}
	annotations[AnnotationTraceParent] = tp
	obj.SetAnnotations(annotations)
}

// recordError records an error on the span and sets its
// status to Error.
func recordError(
	span trace.Span,
	err error,
) {
	span.RecordError(err)
	span.SetStatus(codes.Error, err.Error())
}

// recordContextError records an error on the active span
// (if any) extracted from ctx.
func recordContextError(
	ctx context.Context,
	err error,
) {
	span := trace.SpanFromContext(ctx)
	if span.IsRecording() {
		recordError(span, err)
	}
}

// startSpan creates a new span with the standard naming
// convention "{Resource}/{Operation}" and the resource name
// as an attribute. It also injects the span context into
// the logger stored in ctx so that structured logs emitted
// via ctrllog.FromContext correlate with the trace in the
// OTel bridge.
func startSpan(
	ctx context.Context,
	resource string,
	operation string,
	name string,
) (context.Context, trace.Span) {
	spanName := fmt.Sprintf("%s/%s", resource, operation)
	ctx, span := crTracer.Start(ctx, spanName,
		trace.WithAttributes(
			attribute.String(
				"resource.name", name,
			),
		),
	)

	// Inject the span-bearing context into the logger.
	// The contextTeeCore in main.go intercepts this
	// context.Context value and passes it to the otelzap
	// bridge for trace/span ID correlation, while
	// stripping it from console output.
	log := ctrllog.FromContext(ctx)
	ctx = ctrllog.IntoContext(
		ctx,
		log.WithValues(otelCtxKey, ctx),
	)

	return ctx, span
}

// otelCtxKey is the logr key used to pass context.Context
// through the logging chain for OTel trace correlation.
const otelCtxKey = "_otelCtx"
