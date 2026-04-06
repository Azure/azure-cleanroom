from abc import ABC, abstractmethod

from .audit_record import AuditRecord


class IAuditRecordLogger(ABC):
    """Interface for audit event logging."""

    @abstractmethod
    async def log_audit_record(self, event: AuditRecord) -> None:
        pass
