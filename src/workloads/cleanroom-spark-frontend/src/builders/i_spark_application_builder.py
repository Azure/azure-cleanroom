from abc import ABC, abstractmethod
from typing import List

from src.config.configuration import DriverSettings, ExecutorSettings, TelemetrySettings
from src.models.cleanroom_spark_application import CleanRoomSparkApplication
from src.models.input_models import EnvData, GovernanceSettings
from src.models.model import AccessPoint, DatasetInfo


class ISparkApplicationBuilder(ABC):
    @abstractmethod
    def CreateBuilder(self) -> "ISparkApplicationBuilder":
        """
        Create a Spark application builder with the given name.
        """
        pass

    @abstractmethod
    def WithName(self, name: str) -> "ISparkApplicationBuilderWithName":
        """
        Set the name of the Spark application.
        """
        pass


class ISparkApplicationBuilderWithName(ABC):
    @abstractmethod
    def WithType(self, app_type: str) -> "ISparkApplicationBuilderWithMeta":
        """
        Set the type of the Spark application.
        """
        pass


class ISparkApplicationBuilderWithMeta(ABC):
    @abstractmethod
    def WithImage(self, image: str) -> "ISparkApplicationBuilderWithImage":
        """
        Set the Docker image for the Spark application.
        """
        pass


class ISparkApplicationBuilderWithImage(ABC):
    @abstractmethod
    def WithMainApplicationFile(
        self, main_application_file: str
    ) -> "ISparkApplicationBuilderWithApplication":
        """
        Set the main application file for the Spark application.
        """
        pass


class ISparkApplicationBuilderWithApplication(ABC):
    @abstractmethod
    def WithPolicy(
        self, policy_file: str, debug_mode: bool, allow_all: bool
    ) -> "ISparkApplicationBuilderWithSpec":
        """
        Set the policy file for the Spark application.
        """
        pass

    @abstractmethod
    def WithEnvVars(
        self, env_vars: list[EnvData]
    ) -> "ISparkApplicationBuilderWithApplication":
        """
        Set environment variables for the Spark application.
        """
        pass

    @abstractmethod
    def WithArguments(
        self, arguments: List[str]
    ) -> "ISparkApplicationBuilderWithApplication":
        """
        Set the command for the Spark application.
        """
        pass


class ISparkApplicationBuilderWithSpec(ABC):
    @abstractmethod
    def WithTelemetry(
        self, telemetry: TelemetrySettings
    ) -> "ISparkApplicationBuilderWithSpec":
        """
        Set the telemetry for the Spark application.
        """
        pass

    @abstractmethod
    def WithGovernance(
        self, contract_id: str, governance: GovernanceSettings
    ) -> "ISparkApplicationBuilderWithSpec":
        pass

    @abstractmethod
    def AddDriver(
        self, settings: DriverSettings
    ) -> "ISparkApplicationBuilderWithDriver":
        """
        Add driver specifications to the Spark application.
        """
        pass


class ISparkApplicationBuilderWithDriver(ABC):
    @abstractmethod
    def AddExecutor(
        self, settings: ExecutorSettings
    ) -> "ISparkApplicationBuilderWithExecutor":
        """
        Add executor specifications to the Spark application.
        """
        pass


class ISparkApplicationBuilderWithExecutor(ABC):
    @abstractmethod
    def AddDataset(
        self, dataset: DatasetInfo
    ) -> "ISparkApplicationBuilderWithExecutor":
        """
        Build the Spark application.
        """
        pass

    @abstractmethod
    def AddDatasink(
        self, datasink: DatasetInfo
    ) -> "ISparkApplicationBuilderWithExecutor":
        """
        Build the Spark application.
        """
        pass

    @abstractmethod
    def Build(self) -> CleanRoomSparkApplication:
        """
        Build the Spark application.
        """
        pass
