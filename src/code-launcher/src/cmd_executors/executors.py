import asyncio
import json
import os

from abc import ABCMeta, abstractmethod

from cleanroomspec.models.python.model import Application
from ..connectors.httpconnectors import *
from ..utilities import podman_utilities
from ..exceptions.custom_exceptions import (
    PrivateACRCmdExecutorFailure,
)

logger = logging.getLogger()


class BaseCmdExecutor(metaclass=ABCMeta):
    @abstractmethod
    async def execute(self, application: Application):
        pass


class ACRCmdExecutor(BaseCmdExecutor):
    async def execute(self, application):
        try:
            await podman_utilities.fetch_application_image(application)
        except Exception as e:
            logger.error(f"failed to pull image for application {application.name}")
            raise PrivateACRCmdExecutorFailure(
                f"failed to pull image for application {application.name}"
            ) from e
