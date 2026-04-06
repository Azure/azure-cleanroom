import logging

from analytics_contracts.statistics import IStatisticsRecorder, StatisticsEvent

logger = logging.getLogger(__name__)


class MockStatisticsRecorder(IStatisticsRecorder):
    async def record_statistics(self, event: StatisticsEvent) -> None:
        logger.info(f"Statistics Event: {event.model_dump_json()}")
