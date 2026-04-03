# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
from abc import ABC, abstractmethod

from cleanroom_sdk.models.cleanroom import AccessPoint

from .models.cleanroom_application import CleanroomApplication
from .models.input_models import AttestationType, GovernanceSettings, TelemetrySettings


class ICleanroomApplicationBuilder(ABC):
    @abstractmethod
    def CreateBuilder(self) -> "ICleanroomApplicationBuilder":
        """
        Create a Cleanroom application builder.
        """
        pass

    @abstractmethod
    def WithName(self, name: str) -> "ICleanroomApplicationBuilderWithName":
        """
        Set the name of the application.
        """
        pass


class ICleanroomApplicationBuilderWithName(ABC):
    @abstractmethod
    def WithContractId(
        self, contract_id: str
    ) -> "ICleanroomApplicationBuilderWithContractId":
        """
        Set the contract ID for the application.
        """
        pass


class ICleanroomApplicationBuilderWithGovernance(ABC):
    @abstractmethod
    def AddStorage(
        self, dataset: AccessPoint
    ) -> "ICleanroomApplicationBuilderWithGovernance":
        """
        Add storage (dataset or datasink) to the application.
        """
        pass

    @abstractmethod
    def WithCcrProxyHttpsHttp(
        self, listener_port: int, destination_port: int, fqdn: str = ""
    ) -> "ICleanroomApplicationBuilderWithGovernance":
        """
        Add a ccr-proxy sidecar for HTTPS-to-HTTP TLS termination.
        """
        pass

    @abstractmethod
    def Build(self) -> CleanroomApplication:
        """
        Build the application.
        """
        pass


class ICleanroomApplicationBuilderWithTelemetry(
    ICleanroomApplicationBuilderWithGovernance,
):
    @abstractmethod
    def WithGovernance(
        self,
        governance: GovernanceSettings,
        attestation_type: AttestationType = AttestationType.SKR,
    ) -> "ICleanroomApplicationBuilderWithGovernance":
        """
        Set the governance for the application.
        """
        pass


class ICleanroomApplicationBuilderWithContractId(
    ICleanroomApplicationBuilderWithTelemetry,
):
    @abstractmethod
    def WithTelemetry(
        self,
        telemetry: TelemetrySettings,
        trace_context: dict[str, str],
        extra_vars: dict = {},
    ) -> "ICleanroomApplicationBuilderWithTelemetry":
        """
        Set the telemetry for the application.
        """
        pass
