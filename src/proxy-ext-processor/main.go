package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"os/signal"

	log "github.com/sirupsen/logrus"

	"google.golang.org/grpc"

	configuration "github.com/azure/azure-cleanroom/src/internal/configuration"
	"github.com/azure/azure-cleanroom/src/internal/filter"
	"github.com/azure/azure-cleanroom/src/internal/filter/opa"
	pb "github.com/envoyproxy/go-control-plane/envoy/service/ext_proc/v3"
	"go.opentelemetry.io/contrib/instrumentation/google.golang.org/grpc/otelgrpc"
)

var (
	configFile = flag.String("c", "", "config file")
)

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
	defer cancel()

	var err error
	flag.Parse()
	log.SetFormatter(&log.TextFormatter{
		DisableQuote:  true,
		FullTimestamp: true,
	})
	log.SetReportCaller(true)
	log.SetLevel(log.InfoLevel)

	jsonFile, err := os.Open(*configFile)
	if err != nil {
		log.Panicf("failed to open config file: %v", err)
	}

	defer func() {
		err = jsonFile.Close()
		if err != nil {
			log.Errorf("failed to close config file: %v", err)
		}
	}()

	byteValue, err := io.ReadAll(jsonFile)
	if err != nil {
		log.Panicf("failed to read config file: %v", err)
	}

	config := configuration.Settings{}
	err = json.Unmarshal(byteValue, &config)
	if err != nil {
		log.Panicf("failed to unmarshal config file: %v", err)
	}

	tp, err := initTraceProvider(ctx, config.Otlp)
	if err != nil {
		log.Panicf("failed to setup tracing: %v", err)
	}
	defer func() {
		if err = tp.Shutdown(ctx); err != nil {
			log.Errorf("failed to shutdown TracerProvider: %v", err)
		}
	}()

	tracer := tp.Tracer("ccr-proxy-ext-processor")
	ctx, mainSpan := tracer.Start(ctx, "main")
	defer mainSpan.End()

	defer filter.RecordSpanError(ctx, &err)

	if config.Host == "" {
		config.Host = "127.0.0.1"
	}
	if config.Port == 0 {
		config.Port = 8281
	}

	token := config.Local.PolicyEngine.BundleServiceCredentialsToken
	config.Local.PolicyEngine.BundleServiceCredentialsToken = "****"
	log.Infof("Configuration options: %+v", config)
	config.Local.PolicyEngine.BundleServiceCredentialsToken = token

	address := fmt.Sprintf("%s:%d", config.Host, config.Port)
	lis, err := net.Listen("tcp", address)
	if err != nil {
		log.Panicf("failed to listen: %v", err)
	}

	var ff filter.HttpFilterFactory
	switch config.Filter {
	case "opa":
		fallthrough
	default:
		ff, err = opa.NewHttpFilterFactory(ctx, tracer, config.Local.PolicyEngine)
		if err != nil {
			log.Panicf("failed to create filter factory: %v", err)
		}
	}

	// This service receives gRPC requests for which we add otel instrumentation here.
	grpcServer := grpc.NewServer(
		grpc.UnaryInterceptor(otelgrpc.UnaryServerInterceptor()),
		grpc.StreamInterceptor(otelgrpc.StreamServerInterceptor()),
	)
	pb.RegisterExternalProcessorServer(grpcServer, filter.NewExternalProcessorServer(tracer, ff))

	log.Infof("Listening on %s", address)
	err = grpcServer.Serve(lis)
	if err != nil {
		log.Panicf("failed start gRPC server: %v", err)
	}
}
