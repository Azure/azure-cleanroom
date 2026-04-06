import logging

from opentelemetry import context
from opentelemetry.baggage.propagation import W3CBaggagePropagator
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator


def extract_context_from_carrier(carrier: dict[str, str]):
    logger = logging.getLogger("otel_utilities")
    try:
        extracted_context = TraceContextTextMapPropagator().extract(carrier)
        baggage_ctx = W3CBaggagePropagator().extract(carrier, context=extracted_context)
        return baggage_ctx
    except Exception as e:
        logger.error(f"Error extracting context from carrier: {e}")


def inject_context_into_carrier(context: context.Context):
    carrier = {}
    TraceContextTextMapPropagator().inject(carrier, context)
    W3CBaggagePropagator().inject(carrier, context)
    return carrier


def inject_current_context_into_carrier():
    return inject_context_into_carrier(context.get_current())


def get_current_baggage() -> dict[str, str]:
    from opentelemetry.baggage import get_all

    return {k: str(v) for k, v in get_all(context=context.get_current()).items()}
