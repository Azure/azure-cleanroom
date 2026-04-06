from abc import ABC, abstractmethod
from typing import Optional

from cleanroom_sdk.models.cleanroom import DatasetInfo

from ..config.configuration import PredictorSettings
from ..models.cleanroom_inferencing_application import CleanRoomInferencingApplication
from ..models.input_models import PlacementInput, PredictorInput


class IInferenceServiceBuilder(ABC):
    """
    Entry point for the Inferencing Service builder chain.
    """

    @abstractmethod
    def CreateBuilder(self, contract_id: str = "") -> "IInferenceServiceBuilder":
        """
        Create an Inferencing Service builder.
        """
        pass

    @abstractmethod
    def WithName(self, name: str) -> "IInferenceServiceBuilderWithName":
        """
        Set the name of the Inferencing Service.
        """
        pass


class IInferenceServiceBuilderWithName(ABC):
    @abstractmethod
    def WithPolicy(
        self, policy_file: str, debug_mode: bool, allow_all: bool
    ) -> "IInferenceServiceBuilderWithPolicy":
        """
        Set the policy file for the Inferencing Service.
        """
        pass


class IInferenceServiceBuilderWithPolicy(ABC):
    """
    Inferencing application stage — adds inference-specific configuration.
    """

    @abstractmethod
    def WithModelDir(self, model_dir: str) -> "IInferenceServiceBuilderWithPolicy":
        pass

    @abstractmethod
    def WithModelName(self, model_name: str) -> "IInferenceServiceBuilderWithPolicy":
        """
        Set the model name for the serving container.
        """
        pass

    @abstractmethod
    def WithNamespace(self, namespace: str) -> "IInferenceServiceBuilderWithPolicy":
        pass

    @abstractmethod
    def WithPlacement(
        self, placement: PlacementInput
    ) -> "IInferenceServiceBuilderWithPolicy":
        pass

    @abstractmethod
    def AddPredictor(
        self,
        input: PredictorInput,
        settings: PredictorSettings,
    ) -> "IInferenceServiceBuilderWithSpec":
        """
        Add predictor specifications to the Inferencing Service.
        """
        pass


class IInferenceServiceBuilderWithSpec(ABC):
    """
    Inferencing spec stage — Build the application.
    """

    @abstractmethod
    def AddDataset(self, dataset: DatasetInfo) -> "IInferenceServiceBuilderWithSpec":
        """
        Add a dataset to the Inferencing Service.
        """
        pass

    @abstractmethod
    def Build(self) -> CleanRoomInferencingApplication:
        """
        Build the Inferencing Service.
        """
        pass
