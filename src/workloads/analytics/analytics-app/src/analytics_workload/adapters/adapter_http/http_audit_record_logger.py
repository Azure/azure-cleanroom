import logging
import traceback

from analytics_contracts.audit import AuditRecord, IAuditRecordLogger

from ...utilities.http_utilities import HttpClient


class HttpAuditRecordLogger(IAuditRecordLogger):
    def __init__(
        self,
        audit_endpoint: str,
        max_retries: int = 12,
        retry_delay: int = 5,
    ):
        self.audit_endpoint = audit_endpoint
        self.http_client = HttpClient(max_retries, retry_delay)
        self.logger = logging.getLogger(__name__)

    async def log_audit_record(self, event: AuditRecord) -> None:
        try:
            data = {"source": event.source, "message": event.get_message()}
            self.http_client.put_with_retry(f"{self.audit_endpoint}", json=data)
            self.logger.info(
                f"Audit record logged successfully to {self.audit_endpoint}"
            )
        except Exception as e:
            self.logger.error(
                f"Failed to log audit record. Exception: {repr(e)}. traceback: {traceback.format_exc()}"
            )
