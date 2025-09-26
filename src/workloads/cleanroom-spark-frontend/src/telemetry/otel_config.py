import logging
import os

from opentelemetry import _logs, metrics, trace
from opentelemetry.exporter.otlp.proto.grpc._log_exporter import OTLPLogExporter
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.instrumentation.requests import RequestsInstrumentor
from opentelemetry.sdk._logs import LoggerProvider, LoggingHandler
from opentelemetry.sdk._logs.export import BatchLogRecordProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.semconv.resource import ResourceAttributes
from src.config.configuration import Configuration

logger = logging.getLogger(__name__)


class TelemetryConfig:
    """Configuration for OpenTelemetry"""

    def __init__(self, config: Configuration):
        self.service_name = config.service.name
        self.telemetry_collection_enabled = (
            config.service.telemetry.telemetry_collection_enabled
        )

        self.otlp_endpoint = os.getenv(
            "OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317"
        )

        self.namespace = config.service.namespace
        self.pod_name = os.getenv("POD_NAME", "unknown")

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
        self._setup_auto_instrumentation()

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

            if self.telemetry_collection_enabled:
                logger_provider = LoggerProvider(resource)
                _logs.set_logger_provider(logger_provider)
                otlp_exporter = OTLPLogExporter(insecure=True)
                log_processor = BatchLogRecordProcessor(otlp_exporter)
                logger_provider.add_log_record_processor(log_processor)

                otel_handler = LoggingHandler(
                    level=logging.NOTSET, logger_provider=logger_provider
                )
                root_logger.addHandler(otel_handler)
            else:
                logger.info(
                    "Telemetry collection is disabled. Using console logging only."
                )

            if self.telemetry_collection_enabled:
                logger.info(
                    f"LoggingInstrumentor configured to export logs to OTEL endpoint: {self.otlp_endpoint}"
                )
            else:
                logger.info("Logging configured for console output only.")
        except Exception as e:
            logger.error(f"Failed to setup OTEL logging export: {e}")

    def _setup_tracing(self, resource: Resource) -> None:
        try:
            if self.telemetry_collection_enabled:
                tracer_provider = TracerProvider(resource=resource)
                otlp_exporter = OTLPSpanExporter(
                    endpoint=self.otlp_endpoint,
                    insecure=True,
                )
                span_processor = BatchSpanProcessor(otlp_exporter)
                tracer_provider.add_span_processor(span_processor)
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
            if self.telemetry_collection_enabled:
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

    def _setup_auto_instrumentation(self) -> None:
        """Setup automatic instrumentation for common libraries"""
        try:
            if self.telemetry_collection_enabled:
                RequestsInstrumentor().instrument()
                LoggingInstrumentor().instrument(set_logging_format=True)
                logger.info("Auto-instrumentation enabled for requests and logging.")
            else:
                logger.info(
                    "Telemetry collection is disabled. Auto-instrumentation is disabled."
                )
        except Exception as e:
            logger.error(f"Failed to setup auto-instrumentation: {e}")

    def instrument_fastapi(self, app) -> None:
        try:
            if self.telemetry_collection_enabled:
                FastAPIInstrumentor.instrument_app(app)
                logger.info("FastAPI auto-instrumentation enabled.")
            else:
                logger.info(
                    "Telemetry collection is disabled. FastAPI instrumentation is disabled."
                )
        except Exception as e:
            logger.error(f"Failed to instrument FastAPI: {e}")
