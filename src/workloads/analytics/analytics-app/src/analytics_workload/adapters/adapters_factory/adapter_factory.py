from typing import Optional

from analytics_contracts.audit import IAuditRecordLogger
from analytics_contracts.events import IEventEmitter
from analytics_contracts.statistics import IStatisticsRecorder

from ...config.configuration import ProviderType
from ..adapter_http.http_audit_record_logger import HttpAuditRecordLogger
from ..adapter_http.http_event_emitter import HttpEventEmitter
from ..adapter_http.http_statistics_recorder import HttpStatisticsRecorder
from ..adapters_mock.mock_audit_record_logger import MockAuditRecordLogger
from ..adapters_mock.mock_event_emitter import LocalEventEmitter
from ..adapters_mock.mock_statistics_recorder import MockStatisticsRecorder


class AdapterFactory:
    """Consolidated factory for creating all adapter instances."""

    def __init__(
        self,
        provider_type: ProviderType,
        statistics_recorder_endpoint: Optional[str] = None,
        event_emitter_endpoint: Optional[str] = None,
        audit_record_logger_endpoint: Optional[str] = None,
    ):
        """
        Initialize the adapter factory.

        Args:
            provider_type: The type of provider (Http or Mock/Local).
            statistics_endpoint: Statistics endpoint URL (overrides endpoints_json)
            operational_endpoint: Operational events endpoint URL (overrides endpoints_json)
            audit_endpoint: Audit records endpoint URL (overrides endpoints_json)
        """
        self.provider_type = provider_type
        self._event_emitter: Optional[IEventEmitter] = None
        self._statistics_recorder: Optional[IStatisticsRecorder] = None
        self._audit_record_logger: Optional[IAuditRecordLogger] = None

        if provider_type == ProviderType.Http:
            if not (
                statistics_recorder_endpoint
                and event_emitter_endpoint
                and audit_record_logger_endpoint
            ):
                raise ValueError(
                    "Statistics, Event emitter and Audit record endpoints must be provided for Http provider type"
                )
            self._statistics_recorder_endpoint = statistics_recorder_endpoint
            self._event_emitter_endpoint = event_emitter_endpoint
            self._audit_record_logger_endpoint = audit_record_logger_endpoint

    @property
    def event_emitter(self) -> IEventEmitter:
        """Get or create an event emitter instance."""
        if self._event_emitter is None:
            if self.provider_type == ProviderType.Http:
                self._event_emitter = HttpEventEmitter(self._event_emitter_endpoint)
            else:  # Mock or Local
                self._event_emitter = LocalEventEmitter()
        return self._event_emitter

    @property
    def statistics_recorder(self) -> IStatisticsRecorder:
        """Get or create a statistics recorder instance."""
        if self._statistics_recorder is None:
            if self.provider_type == ProviderType.Http:
                self._statistics_recorder = HttpStatisticsRecorder(
                    self._statistics_recorder_endpoint
                )
            else:  # Mock or Local
                self._statistics_recorder = MockStatisticsRecorder()
        return self._statistics_recorder

    @property
    def audit_logger(self) -> IAuditRecordLogger:
        """Get or create an audit record logger instance."""
        if self._audit_record_logger is None:
            if self.provider_type == ProviderType.Http:
                self._audit_record_logger = HttpAuditRecordLogger(
                    self._audit_record_logger_endpoint
                )
            else:  # Mock or Local
                self._audit_record_logger = MockAuditRecordLogger()
        return self._audit_record_logger
