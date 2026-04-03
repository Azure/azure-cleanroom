import logging
import os

from opentelemetry import _logs, metrics, trace
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.processor.baggage import ALLOW_ALL_BAGGAGE_KEYS, BaggageSpanProcessor
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.semconv.resource import ResourceAttributes

logger = logging.getLogger(__name__)


class TelemetryConfig:
    """Configuration for OpenTelemetry"""

    def __init__(
        self, service_name: str, is_otel_enabled: bool, pod_name: str, namespace: str
    ):
        self.service_name = service_name
        self.is_otel_enabled = is_otel_enabled
        self.pod_name = pod_name
        self.namespace = namespace

        self.otlp_endpoint = os.getenv(
            "OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"
        )

    def setup_telemetry(self) -> None:
        # Create resource with service information
        resource = Resource.create(
            {
                ResourceAttributes.SERVICE_NAME: self.service_name,
                "k8s.namespace.name": self.namespace,
                "k8s.pod.name": self.pod_name,
            }
        )
        self._setup_logging(resource)
        self._setup_tracing(resource)
        self._setup_metrics(resource)
        logger.info(f"OpenTelemetry initialized for service: {self.service_name}")

    def _setup_logging(self, resource: Resource) -> None:
        """Setup logging to export logs to the OTEL endpoint and console"""
        try:
            console_handler = logging.StreamHandler()
            console_handler.setLevel(logging.INFO)
            formatter = logging.Formatter(
                "%(asctime)s - %(name)s - %(levelname)s - %(message)s"
            )
            console_handler.setFormatter(formatter)

            root_logger = logging.getLogger()
            root_logger.setLevel(logging.NOTSET)
            root_logger.addHandler(console_handler)

            if self.is_otel_enabled:
                logger_provider = LoggerProvider(resource)
                _logs.set_logger_provider(logger_provider)
                otlp_exporter = OTLPLogExporter(insecure=True)
                log_processor = BatchLogRecordProcessor(otlp_exporter)
                logger_provider.add_log_record_processor(log_processor)

                otel_handler = LoggingHandler(
                    level=logging.NOTSET, logger_provider=logger_provider
                )
                root_logger.addHandler(otel_handler)
                LoggingInstrumentor().instrument(set_logging_format=True)
                logger.info(
                    f"LoggingInstrumentor configured to export logs to OTEL endpoint: {self.otlp_endpoint}"
                )
            else:
                logger.info(
                    "Telemetry collection is disabled. Using console logging only."
                )

        except Exception as e:
            logger.error(f"Failed to setup OTEL logging export: {e}")

    def _setup_tracing(self, resource: Resource) -> None:
        try:
            if self.is_otel_enabled:
                tracer_provider = TracerProvider(resource=resource)
                otlp_exporter = OTLPSpanExporter(
                    endpoint=self.otlp_endpoint,
                    insecure=True,
                )
                span_processor = BatchSpanProcessor(otlp_exporter)
                tracer_provider.add_span_processor(span_processor)
                tracer_provider.add_span_processor(
                    BaggageSpanProcessor(ALLOW_ALL_BAGGAGE_KEYS)
                )
                trace.set_tracer_provider(tracer_provider)
                logger.info(
                    f"Tracing configured to export to OTEL endpoint: {self.otlp_endpoint}"
                )
            else:
                logger.info("Telemetry collection is disabled. Tracing is disabled.")
        except Exception as e:
            logger.error(f"Failed to setup tracing: {e}")

    def _setup_metrics(self, resource: Resource) -> None:
        try:
            if self.is_otel_enabled:
                metric_exporter = OTLPMetricExporter(
                    endpoint=self.otlp_endpoint,
                    insecure=True,
                )

                metric_reader = PeriodicExportingMetricReader(
                    exporter=metric_exporter,
                    export_interval_millis=5000,  # Export every 5 seconds
                )

                meter_provider = MeterProvider(
                    resource=resource, metric_readers=[metric_reader]
                )

                metrics.set_meter_provider(meter_provider)
                logger.info(
                    f"Metrics configured to export to OTEL endpoint: {self.otlp_endpoint}"
                )
            else:
                logger.info(
                    "Telemetry collection is disabled. Metrics collection is disabled."
                )
        except Exception as e:
            logger.error(f"Failed to setup metrics: {e}")

    def instrument_requests(self) -> None:
        from opentelemetry.instrumentation.requests import RequestsInstrumentor

        try:
            if self.is_otel_enabled:
                RequestsInstrumentor.instrument()
            else:
                logger.info(
                    "Telemetry collection is disabled. Not enabling Requests instrumentation."
                )
        except Exception as e:
            logger.error(f"Failed to instrument Requests: {e}")

    def instrument_fastapi(self, app) -> None:
        from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor

        try:
            if self.is_otel_enabled:
                FastAPIInstrumentor.instrument_app(app)
                logger.info("FastAPI auto-instrumentation enabled.")
            else:
                logger.info(
                    "Telemetry collection is disabled. Not enabling FastAPI instrumentation."
                )
        except Exception as e:
            logger.error(f"Failed to instrument FastAPI: {e}")
