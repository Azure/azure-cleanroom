import logging

from analytics_contracts.audit import AuditRecord, IAuditRecordLogger

logger = logging.getLogger(__name__)


class MockAuditRecordLogger(IAuditRecordLogger):
    async def log_audit_record(self, event: AuditRecord) -> None:
        logger.info(f"Audit Event: {event.get_message()}")
