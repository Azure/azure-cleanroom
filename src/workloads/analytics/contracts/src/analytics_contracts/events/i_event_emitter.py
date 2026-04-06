import logging
from abc import ABC, abstractmethod

from .operational_event import OperationalEvent

logger = logging.getLogger(__name__)


class IEventEmitter(ABC):
    """Interface for operational event recording."""

    @abstractmethod
    async def log_operational_event(self, event: OperationalEvent) -> None:
        pass
