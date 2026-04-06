import logging

from analytics_contracts.events import IEventEmitter, OperationalEvent

logger = logging.getLogger(__name__)


class LocalEventEmitter(IEventEmitter):
    async def log_operational_event(self, event: OperationalEvent) -> None:
        logger.info(f"Operational Event: {event.get_message()}")
