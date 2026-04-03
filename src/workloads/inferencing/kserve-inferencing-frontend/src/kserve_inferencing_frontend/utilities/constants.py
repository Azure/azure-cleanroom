class Constants:
    """Constants used in the Inference Service."""

    ALLOW_ALL_POLICY_BASE64 = "WyJhbGxvd2FsbCJd"

    # The service name to use for any OpenTelemetry instrumentation.
    OTEL_SERVICE_NAME = "kserve-inferencing-frontend"

    # The environment variable key for OpenTelemetry trace context.
    OTEL_TRACE_CONTEXT_ENV_KEY = "OTEL_TRACE_CONTEXT_BASE64"
