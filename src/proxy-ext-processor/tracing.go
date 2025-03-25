package main

import (
	"context"

	configuration "github.com/azure/azure-cleanroom/src/internal/configuration"
	log "github.com/sirupsen/logrus"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/sdk/resource"
	"go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.17.0"
)

// https://www.komu.engineer/blogs/11/opentelemetry-and-go
// https://github.com/open-telemetry/opentelemetry-go/blob/9b0c4d2caf7a5fb88714e4c64fe61d5fbf1c0b8a/example/otel-collector/main.go
// https://github.com/open-telemetry/opentelemetry-demo/blob/main/src/checkoutservice/main.go
func initTraceProvider(ctx context.Context, otlp configuration.OtlpConfig) (*trace.TracerProvider, error) {
	endpoint := otlp.Endpoint
	if endpoint == "" {
		endpoint = "localhost:4317"
	}

	exporter, err := otlptracegrpc.New(
		ctx,
		otlptracegrpc.WithEndpoint(endpoint),
		otlptracegrpc.WithInsecure(),
	)
	if err != nil {
		log.Errorf("otlptracegrpc.New() failed: %v", err)
		return nil, err
	}

	resource, err := initResource(ctx)
	if err != nil {
		log.Errorf("failed to create traceprovider's resource: %v", err)
		return nil, err
	}

	provider := trace.NewTracerProvider(
		trace.WithBatcher(exporter),
		trace.WithResource(resource),
	)

	otel.SetTracerProvider(provider)
	otel.SetTextMapPropagator(
		propagation.NewCompositeTextMapPropagator(propagation.TraceContext{}, propagation.Baggage{}))
	return provider, nil
}

func initResource(ctx context.Context) (*resource.Resource, error) {
	extraResources, _ := resource.New(
		ctx,
		resource.WithOS(),
		resource.WithProcess(),
		resource.WithContainer(),
		resource.WithHost(),
		resource.WithSchemaURL(semconv.SchemaURL),
		resource.WithAttributes(
			semconv.ServiceNameKey.String("cleanroom")),
	)
	r, err := resource.Merge(
		resource.Default(),
		extraResources,
	)

	if err != nil {
		return nil, err
	}

	return r, nil
}
