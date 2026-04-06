import os
from enum import StrEnum
from typing import Optional

from frontend_internal.models.input_models import (
    DEFAULT_MCR_URL,
    DEFAULT_MCR_VERSION,
)
from frontend_internal.models.input_models import (
    CleanroomSettings as CleanroomSettingsBase,
)
from frontend_internal.models.input_models import (
    TelemetrySettings,
)
from pydantic import BaseModel, Field


class CleanroomSettings(CleanroomSettingsBase):
    cvm_measurements_document: str = Field(
        alias="cvmMeasurementsDocument",
        default=os.environ.get("CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL")
        or f"{DEFAULT_MCR_URL}/cvm-measurements:{DEFAULT_MCR_VERSION}",
    )
    runtime_digests_document: str = Field(
        alias="runtimeDigestsDocument",
        default=os.environ.get("CLEANROOM_RUNTIME_DIGESTS_DOCUMENT_URL")
        or f"{DEFAULT_MCR_URL}/inf-runtime-digests:{DEFAULT_MCR_VERSION}",
    )


class InferencingComputeProvider(StrEnum):
    ConfidentialVirtualNode = "confidential-virtual-node"
    ConfidentialVM = "confidential-vm"
    Virtual = "virtual"


class PredictorSettings(BaseModel):
    cores: float = Field(alias="cores")
    memory: str = Field(alias="memory")
    service_account: str = Field(
        alias="serviceAccount", default="kserve-operator-kserve"
    )


class SkuSettings(BaseModel):
    predictor: PredictorSettings = Field(alias="predictor")


class InferencingSettings(BaseModel):
    namespace: str = Field(alias="namespace", default="kserve-inferencing")
    image: str = Field(
        alias="image",
        default=os.environ.get(
            "CLEANROOM_KSERVE_INFERENCING_IMAGE_URL",
            f"{DEFAULT_MCR_URL}/workloads/cleanroom-kserve-inferencing-app:{DEFAULT_MCR_VERSION}",
        ),
    )
    policy_file: str = Field(
        alias="policyFile",
        default=os.environ.get(
            "CLEANROOM_KSERVE_INFERENCING_POLICY_FILE",
            f"{DEFAULT_MCR_URL}/policies/cleanroom-kserve-inferencing-app-policy:{DEFAULT_MCR_VERSION}",
        ),
    )
    debug_mode: bool = Field(alias="debugMode", default=False)
    allow_all: bool = Field(alias="allowAll", default=False)
    kserve: SkuSettings = Field(alias="kserve")


class ApplicationSettings(BaseModel):
    inferencing: InferencingSettings = Field(alias="inferencing")


class InferenceServiceResourceSettings(BaseModel):
    group: str = Field(alias="group", default="serving.kserve.io")
    version: str = Field(alias="version", default="v1beta1")
    kind: str = Field(alias="kind", default="inferenceservice")
    plural: str = Field(alias="plural", default="inferenceservices")


class ServiceSettings(BaseModel):
    name: str = Field(alias="name", default="kserve-inferencing-frontend")
    namespace: str = Field(alias="namespace", default="kserve-inferencing-frontend")
    telemetry: TelemetrySettings = Field(
        alias="telemetry", default_factory=TelemetrySettings
    )


class KServeSettings(BaseModel):
    compute_provider: InferencingComputeProvider = Field(
        alias="computeProvider",
        default=InferencingComputeProvider.ConfidentialVM,
    )
    resource: InferenceServiceResourceSettings = Field(
        alias="resource", default_factory=InferenceServiceResourceSettings
    )


class Configuration(BaseModel):
    cleanroom: CleanroomSettings = Field(
        alias="cleanroom", default_factory=CleanroomSettings
    )
    kserve: KServeSettings = Field(alias="kserve", default_factory=KServeSettings)
    applications: ApplicationSettings = Field(alias="applications")
    service: ServiceSettings = Field(alias="service", default_factory=ServiceSettings)
