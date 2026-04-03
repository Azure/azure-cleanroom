from abc import ABC, abstractmethod
from typing import List, Optional

from cleanroom_sdk.models.cleanroom import DatasetInfo

from ..config.configuration import DriverSettings, ExecutorSettings
from ..models.cleanroom_spark_application import CleanRoomSparkApplication
from ..models.input_models import EnvData


class ISparkApplicationBuilder(ABC):
    """
    Entry point for the Spark builder chain.
    """

    @abstractmethod
    def CreateBuilder(self, contract_id: str) -> "ISparkApplicationBuilder":
        """
        Create a Spark application builder.
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
    def WithImage(self, image: str) -> "ISparkApplicationBuilderWithImage":
        """
        Set the image for the Spark application.
        """
        pass


class ISparkApplicationBuilderWithImage(ABC):
    @abstractmethod
    def WithPolicy(
        self, policy_file: str, debug_mode: bool, allow_all: bool
    ) -> "ISparkApplicationBuilderWithPolicy":
        """
        Set the policy for the Spark application.
        """
        pass


class ISparkApplicationBuilderWithPolicy(ABC):
    @abstractmethod
    def WithMainApplicationFile(
        self, main_application_file: str
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        """
        Set the main application file for the Spark application.
        """
        pass


class ISparkApplicationBuilderWithMainAppFile(ABC):
    @abstractmethod
    def WithEnvVars(
        self, env_vars: Optional[List[EnvData]]
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        """
        Set the environment variables for the Spark application.
        """
        pass

    @abstractmethod
    def WithArguments(
        self, arguments: Optional[List[str]]
    ) -> "ISparkApplicationBuilderWithMainAppFile":
        """
        Set the arguments for the Spark application.
        """
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
        Add a dataset to the Spark application.
        """
        pass

    @abstractmethod
    def AddDatasink(
        self, datasink: DatasetInfo
    ) -> "ISparkApplicationBuilderWithExecutor":
        """
        Add a datasink to the Spark application.
        """
        pass

    @abstractmethod
    def Build(self) -> CleanRoomSparkApplication:
        """
        Build the Spark application.
        """
        pass
