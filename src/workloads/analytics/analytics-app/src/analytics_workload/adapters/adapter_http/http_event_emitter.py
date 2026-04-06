import logging
import traceback

from analytics_contracts.events import IEventEmitter, OperationalEvent

from ...utilities.http_utilities import HttpClient


class HttpEventEmitter(IEventEmitter):
    def __init__(
        self,
        operational_endpoint: str,
        max_retries: int = 12,
        retry_delay: int = 5,
    ):
        self.operational_endpoint = operational_endpoint
        self.http_client = HttpClient(max_retries, retry_delay)
        self.logger = logging.getLogger(__name__)

    async def log_operational_event(self, event: OperationalEvent) -> None:
        try:
            self.http_client.put_with_retry(
                self.operational_endpoint,
                data=event.model_dump_json(),
                # TODO (HPrabh): Enable SSL verification when certificates are properly set up.
                verify_ssl=False,
            )
            self.logger.info(
                f"Operational event {event.name} logged successfully to {self.operational_endpoint}"
            )
        except Exception as e:
            self.logger.error(
                f"Failed to log operational event. Exception: {repr(e)}. traceback: {traceback.format_exc()}"
            )
