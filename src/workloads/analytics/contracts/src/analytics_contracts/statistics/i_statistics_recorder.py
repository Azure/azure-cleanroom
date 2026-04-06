from abc import ABC, abstractmethod

from .statistics_event import StatisticsEvent


class IStatisticsRecorder(ABC):
    """Interface for statistics event recording."""

    @abstractmethod
    async def record_statistics(self, event: StatisticsEvent) -> None:
        pass
