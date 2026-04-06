import logging
import traceback

from analytics_contracts.statistics import IStatisticsRecorder, StatisticsEvent

from ...utilities.http_utilities import HttpClient


class HttpStatisticsRecorder(IStatisticsRecorder):
    def __init__(
        self,
        statistics_endpoint: str,
        max_retries: int = 12,
        retry_delay: int = 5,
    ):
        self.statistics_endpoint = statistics_endpoint
        self.http_client = HttpClient(max_retries, retry_delay)
        self.logger = logging.getLogger(__name__)

    async def record_statistics(self, event: StatisticsEvent) -> None:
        try:
            self.http_client.put_with_retry(
                self.statistics_endpoint,
                data=event.model_dump_json(),
                # TODO (HPrabh): Enable SSL verification when certificates are properly set up.
                verify_ssl=False,
            )
            self.logger.info(
                f"Statistics event {event.type} recorded successfully to {self.statistics_endpoint}"
            )
        except Exception as e:
            self.logger.error(
                f"Failed to record statistics. Exception: {repr(e)}. traceback: {traceback.format_exc()}"
            )
