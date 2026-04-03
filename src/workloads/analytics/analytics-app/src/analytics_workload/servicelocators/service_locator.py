from analytics_workload.adapters.adapters_factory.adapter_factory import AdapterFactory
from dependency_injector import containers, providers


class ServiceLocator(containers.DeclarativeContainer):
    """Application dependency injection container."""

    config = providers.Configuration()

    adapter_factory = providers.Singleton(
        AdapterFactory,
        provider_type=config.event_recorder_configuration.provider_type,
        statistics_recorder_endpoint=config.event_recorder_configuration.statistics_recorder_endpoint,
        event_emitter_endpoint=config.event_recorder_configuration.event_emitter_endpoint,
        audit_record_logger_endpoint=config.event_recorder_configuration.audit_record_logger_endpoint,
    )
