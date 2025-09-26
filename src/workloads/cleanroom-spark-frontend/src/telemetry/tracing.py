import functools
import time
from typing import Any, Callable, Dict, Optional

from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode


class SpanContextManager:
    """Context manager for creating spans with automatic error handling"""

    def __init__(
        self,
        tracer: trace.Tracer,
        span_name: str,
        attributes: Optional[Dict[str, Any]] = None,
    ):
        self.tracer = tracer
        self.span_name = span_name
        self.attributes = attributes or {}
        self.span = None
        self.start_time = None

    def __enter__(self):
        self.span = self.tracer.start_span(self.span_name)
        self.span.set_attributes(self.attributes)
        self.start_time = time.time()
        return self.span

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.span:
            # Record duration
            if self.start_time:
                duration = time.time() - self.start_time
                self.span.set_attribute("duration", duration)

            if exc_type:
                self.span.set_status(Status(StatusCode.ERROR, str(exc_val)))
                self.span.record_exception(exc_val)
            else:
                self.span.set_status(Status(StatusCode.OK))

            self.span.end()

    async def __aenter__(self):
        return self.__enter__()

    async def __aexit__(self, exc_type, exc_val, exc_tb):
        self.__exit__(exc_type, exc_val, exc_tb)


def create_span_context(
    span_name: str, attributes: Optional[Dict[str, Any]] = None
) -> SpanContextManager:
    """Create a span context manager"""
    tracer = trace.get_tracer(__name__)
    return SpanContextManager(tracer, span_name, attributes)


def add_span_attributes(span: trace.Span, attributes: Dict[str, Any]) -> None:
    """Add multiple attributes to a span"""
    for key, value in attributes.items():
        span.set_attribute(key, value)


def trace_async_function(
    span_name: Optional[str] = None, attributes: Optional[Dict[str, Any]] = None
):
    """Decorator to trace async functions"""

    def decorator(func: Callable) -> Callable:
        @functools.wraps(func)
        async def wrapper(*args, **kwargs):
            tracer = trace.get_tracer(__name__)
            name = span_name or f"{func.__module__}.{func.__name__}"
            attrs = attributes or {}
            attrs.update(
                {
                    "function.name": func.__name__,
                    "function.module": func.__module__,
                }
            )
            try:
                async with SpanContextManager(tracer, name, attrs):
                    result = await func(*args, **kwargs)
                # Optionally, you can add duration to the attributes if needed
                return result
            except Exception as e:
                raise

        return wrapper

    return decorator


def trace_function(
    span_name: Optional[str] = None, attributes: Optional[Dict[str, Any]] = None
):
    """Decorator to trace synchronous functions"""

    def decorator(func: Callable) -> Callable:
        @functools.wraps(func)
        def wrapper(*args, **kwargs):
            tracer = trace.get_tracer(__name__)
            name = span_name or f"{func.__module__}.{func.__name__}"
            attrs = attributes or {}
            attrs.update(
                {
                    "function.name": func.__name__,
                    "function.module": func.__module__,
                }
            )
            try:
                with SpanContextManager(tracer, name, attrs):
                    result = func(*args, **kwargs)
                return result
            except Exception as e:
                raise

        return wrapper

    return decorator
