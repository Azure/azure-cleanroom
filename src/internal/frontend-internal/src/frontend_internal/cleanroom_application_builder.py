# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.
import base64
import json
import logging
import os
import tempfile
import threading
from string import Template
from typing import List, Optional, Tuple
from urllib.parse import urlparse

import oras.client
import yaml
from cleanroom_sdk.models.cleanroom import (
    AccessPoint,
    AccessPointType,
    Identity,
    ProtocolType,
    ResourceType,
)
from kubernetes.client import models as k8smodels

from .i_cleanroom_application_builder import (
    ICleanroomApplicationBuilder,
    ICleanroomApplicationBuilderWithContractId,
    ICleanroomApplicationBuilderWithGovernance,
    ICleanroomApplicationBuilderWithName,
    ICleanroomApplicationBuilderWithTelemetry,
)
from .models.cleanroom_application import CleanroomApplication, Sidecar
from .models.input_models import (
    AttestationType,
    CleanroomSettings,
    GovernanceSettings,
    TelemetrySettings,
)

VOLUMESTATUS_MOUNT_PATH = "/mnt/volumestatus"
TELEMETRY_MOUNT_PATH = "/mnt/telemetry"
BLOBFUSE_READINESS_PORT_START = 6300

logger = logging.getLogger("cleanroom_application_builder")


def replace_vars(content: str, vars: dict):
    spec = Template(content).substitute(vars)
    return json.loads(spec)


class CleanroomApplicationBuilder(
    ICleanroomApplicationBuilder,
    ICleanroomApplicationBuilderWithName,
    ICleanroomApplicationBuilderWithContractId,
    ICleanroomApplicationBuilderWithTelemetry,
    ICleanroomApplicationBuilderWithGovernance,
):
    def __init__(self, cleanroom_settings: CleanroomSettings):
        self._app_name: Optional[str] = None
        self._contract_id: Optional[str] = None
        self._cleanroom_settings = cleanroom_settings
        self._governance_settings: Optional[GovernanceSettings] = None
        self._governance_required = False
        self._attestation_type: AttestationType = AttestationType.SKR
        self._telemetry: TelemetrySettings = TelemetrySettings()
        self._trace_context: dict[str, str] = {}
        self._telemetry_extra_vars: dict = {}
        self._storage_items: list[tuple[AccessPoint, str]] = []
        self._ccr_proxy_https_http: Optional[tuple[int, int, str]] = None
        self._debug_mode: bool = False

    def CreateBuilder(self) -> "ICleanroomApplicationBuilder":
        return self

    def WithName(self, name: str) -> "ICleanroomApplicationBuilderWithName":
        if not name:
            raise ValueError("Application name cannot be empty")
        self._app_name = name.lower().replace(" ", "-")
        return self

    def WithContractId(
        self, contract_id: str
    ) -> "ICleanroomApplicationBuilderWithContractId":
        self._contract_id = contract_id
        return self

    def WithTelemetry(
        self,
        telemetry: TelemetrySettings,
        trace_context: dict[str, str],
        extra_vars: dict = {},
    ) -> "ICleanroomApplicationBuilderWithTelemetry":
        self._telemetry = telemetry
        self._trace_context = trace_context
        self._telemetry_extra_vars = extra_vars
        return self

    def WithGovernance(
        self,
        governance: GovernanceSettings,
        attestation_type: AttestationType = AttestationType.SKR,
    ) -> "ICleanroomApplicationBuilderWithGovernance":
        if governance.cert_base64 is None and governance.service_cert_discovery is None:
            raise ValueError(
                "Either cert_base64 or service_cert_discovery must be provided in governance settings."
            )
        self._governance_required = True
        self._governance_settings = governance
        self._attestation_type = attestation_type
        return self

    def AddStorage(
        self, access_point: AccessPoint, subject: str
    ) -> "ICleanroomApplicationBuilderWithGovernance":
        self._storage_items.append((access_point, subject))
        return self

    def WithCcrProxyHttpsHttp(
        self, listener_port: int, destination_port: int, fqdn: str = ""
    ) -> "ICleanroomApplicationBuilderWithGovernance":
        self._ccr_proxy_https_http = (listener_port, destination_port, fqdn)
        return self

    def Build(self) -> CleanroomApplication:
        sidecars: List[Sidecar] = []
        blobfuse_sidecars: List[Sidecar] = []
        s3fs_sidecars: List[Sidecar] = []
        identities: List[Tuple[Identity, str]] = []

        # Order of the containers in the sidecars list is important as that determines the order of
        # the startup sequence of the init containers in the Spark application.
        if self._telemetry and self._telemetry.telemetry_collection_enabled:
            sidecars.append(
                self._get_otel_sidecar(TELEMETRY_MOUNT_PATH, self._telemetry_extra_vars)
            )

        if self._governance_required:
            assert (
                self._governance_settings is not None
            ), "Governance settings are required."
            assert self._contract_id is not None, "Contract ID is required."
            if self._attestation_type == AttestationType.CVM:
                sidecars.append(
                    self._get_cvm_attestation_agent_sidecar(TELEMETRY_MOUNT_PATH)
                )
            else:
                sidecars.append(self._get_skr_sidecar(TELEMETRY_MOUNT_PATH))
            sidecars.append(
                self._get_ccr_governance_sidecar(
                    self._contract_id,
                    self._governance_settings,
                    TELEMETRY_MOUNT_PATH,
                )
            )

        if self._ccr_proxy_https_http:
            assert (
                self._governance_required is not None
            ), "Governance is required when using ccr-proxy with CGS CA."
            listener_port, destination_port, fqdn = self._ccr_proxy_https_http
            sidecars.append(
                self._get_ccr_proxy_sidecar(listener_port, destination_port, fqdn)
            )

        next_readiness_port = BLOBFUSE_READINESS_PORT_START
        if self._storage_items:
            assert self._contract_id is not None, "Contract ID is required."
            for item, expected_subject in self._storage_items:
                if item.store.type in [
                    ResourceType.Azure_BlobStorage,
                    ResourceType.Azure_BlobStorage_DataLakeGen2,
                ]:
                    identities.append(
                        (item.identity, self._contract_id + "-" + expected_subject)
                    )
                    blobfuse_sidecars.append(
                        self._get_blobfuse_sidecar(
                            item,
                            "/mnt/remote",
                            item.name,
                            TELEMETRY_MOUNT_PATH,
                            VOLUMESTATUS_MOUNT_PATH,
                            next_readiness_port,
                        )
                    )
                    next_readiness_port += 1
                elif item.store.type == ResourceType.Aws_S3:
                    s3fs_sidecars.append(
                        self._get_s3fs_sidecar(
                            item,
                            "/mnt/remote",
                            item.name,
                            TELEMETRY_MOUNT_PATH,
                            VOLUMESTATUS_MOUNT_PATH,
                        )
                    )
                else:
                    raise ValueError(
                        f"Unsupported store type {item.store.type} for {item.name}."
                    )

        if identities:
            sidecars.append(
                self._get_identity_sidecar(
                    identities,
                    "api://AzureADTokenExchange",
                    TELEMETRY_MOUNT_PATH,
                )
            )

        # Blobfuse sidecars are added after the identity sidecar to ensure that
        # the identity sidecar is ready before the blobfuse sidecars start.
        if blobfuse_sidecars:
            sidecars.extend(blobfuse_sidecars)

        if s3fs_sidecars:
            sidecars.extend(s3fs_sidecars)

        return CleanroomApplication(sidecars=sidecars)

    def _get_container_registry_url(self) -> str:
        return self._cleanroom_settings.registry_url

    def _get_sidecars_version(self):
        # Download the sidecar versions document.
        temp_dir = tempfile.gettempdir()

        lock = threading.Lock()
        if not os.path.exists(os.path.join(temp_dir, "sidecar-digests.yaml")):
            with lock:
                if not os.path.exists(os.path.join(temp_dir, "sidecar-digests.yaml")):
                    versions_registry_url = self._cleanroom_settings.versions_document
                    logger.warning(
                        f"Using cleanroom containers versions registry: {versions_registry_url}"
                    )

                    insecure = self._cleanroom_settings.use_http
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=versions_registry_url,
                        outdir=temp_dir,
                    )

        with open(os.path.join(temp_dir, "sidecar-digests.yaml")) as f:
            sidecars_version = yaml.safe_load(f)
        return sidecars_version

    def _get_sidecar_policy_document(self, imageName: str):
        # Download the sidecar policy document.
        temp_dir = tempfile.gettempdir()

        base_url = self._cleanroom_settings.sidecars_policy_document_registry_url

        sidecar = [x for x in self._get_sidecars_version() if x["image"] == imageName][
            0
        ]
        insecure = self._cleanroom_settings.use_http

        lock = threading.Lock()
        if not os.path.exists(
            os.path.join(temp_dir, sidecar["policyDocument"] + ".yaml")
        ):
            with lock:
                if not os.path.exists(
                    os.path.join(temp_dir, sidecar["policyDocument"] + ".yaml")
                ):
                    os.makedirs(temp_dir, exist_ok=True)
                    policy_document_url = (
                        f"{base_url}/policies/"
                        + f"{sidecar['policyDocument']}@{sidecar['policyDocumentDigest']}"
                    )
                    logger.info("Pulling policy document: %s", policy_document_url)
                    client = oras.client.OrasClient(insecure=insecure)
                    client.pull(
                        target=policy_document_url,
                        outdir=temp_dir,
                    )

        with open(os.path.join(temp_dir, sidecar["policyDocument"] + ".yaml")) as f:
            return yaml.safe_load(f)

    @staticmethod
    def _build_startup_probe(probe_obj: dict) -> k8smodels.V1Probe:
        http_get = None
        tcp_socket = None
        if "httpGet" in probe_obj:
            http_get = k8smodels.V1HTTPGetAction(
                path=probe_obj["httpGet"]["path"],
                port=int(probe_obj["httpGet"]["port"]),
            )
        elif "tcpSocket" in probe_obj:
            tcp_socket = k8smodels.V1TCPSocketAction(
                port=int(probe_obj["tcpSocket"]["port"]),
            )
        return k8smodels.V1Probe(
            http_get=http_get,
            tcp_socket=tcp_socket,
            initial_delay_seconds=probe_obj.get("initialDelaySeconds"),
            period_seconds=probe_obj.get("periodSeconds"),
            failure_threshold=probe_obj.get("failureThreshold"),
        )

    def _get_sidecar(
        self, sidecar_name: str, sidecar_replacement_vars: dict
    ) -> Sidecar:
        sidecar = [
            x for x in self._get_sidecars_version() if x["image"] == sidecar_name
        ][0]
        sidecar_replacement_vars["containerRegistryUrl"] = (
            self._cleanroom_settings.registry_url
        )
        sidecar_replacement_vars["digest"] = sidecar["digest"]

        sidecar_policy_document = self._get_sidecar_policy_document(sidecar_name)
        sidecar_yaml = replace_vars(
            json.dumps(sidecar_policy_document["podSpecYaml"]),
            sidecar_replacement_vars,
        )
        node = "virtual_node_rego"
        if self._debug_mode:
            logger.warning(
                f"Using debug policy for sidecar {sidecar_name}. This should only be used for development purposes."
            )
            node = "virtual_node_rego_debug"
        sidecar_policy_rego = replace_vars(
            json.dumps(sidecar_policy_document["policy"][node]),
            sidecar_replacement_vars,
        )
        sidecar_obj = sidecar_yaml["spec"]["containers"][0]
        logger.info("Using sidecar image: %s", sidecar_obj["image"])
        sidecar_container = k8smodels.V1Container(
            name=sidecar_obj["name"],
            image=sidecar_obj["image"],
            command=sidecar_obj["command"],
            env=[
                k8smodels.V1EnvVar(name=env["name"], value=env["value"])
                for env in sidecar_obj.get("env", [])
            ],
            resources=k8smodels.V1ResourceRequirements(
                requests=sidecar_obj.get("resources", {}).get("requests", {}),
            ),
            volume_mounts=[
                k8smodels.V1VolumeMount(
                    name=vm["name"],
                    mount_path=vm["mountPath"],
                    read_only=vm.get("readOnly", False),
                    mount_propagation=vm.get("mountPropagation", "None"),
                )
                for vm in sidecar_obj.get("volumeMounts", [])
            ],
            security_context=k8smodels.V1SecurityContext(
                privileged=sidecar_obj.get("securityContext", {}).get(
                    "privileged", False
                ),
            ),
            ports=(
                [
                    k8smodels.V1ContainerPort(
                        name=p.get("name"),
                        container_port=int(p["containerPort"]),
                        protocol=p.get("protocol"),
                    )
                    for p in sidecar_obj["ports"]
                ]
                if "ports" in sidecar_obj
                else None
            ),
            startup_probe=(
                self._build_startup_probe(sidecar_obj["startupProbe"])
                if "startupProbe" in sidecar_obj
                else None
            ),
            # https://kubernetes.io/docs/concepts/workloads/pods/sidecar-containers/
            restart_policy="Always",
        )
        return Sidecar(sidecar_container, sidecar_policy_rego)

    def _get_skr_sidecar(self, telemetry_mount_path: str) -> Sidecar:
        return self._get_sidecar("skr", {"telemetryMountPath": telemetry_mount_path})

    def _get_cvm_attestation_agent_sidecar(self, telemetry_mount_path: str) -> Sidecar:
        return self._get_sidecar(
            "cvm-attestation-agent", {"telemetryMountPath": telemetry_mount_path}
        )

    def _get_ccr_governance_sidecar(
        self,
        contract_id: str,
        governance_settings: GovernanceSettings,
        telemetry_mount_path: str,
    ) -> Sidecar:
        cgs_endpoint = governance_settings.service_url

        replace_vars_dict = {
            "cgsEndpoint": cgs_endpoint,
            "contractId": contract_id,
            "telemetryMountPath": telemetry_mount_path,
        }
        if governance_settings.cert_base64:
            replace_vars_dict["serviceCertBase64"] = governance_settings.cert_base64
        else:
            replace_vars_dict["serviceCertBase64"] = ""

        if governance_settings.service_cert_discovery:
            replace_vars_dict["serviceCertDiscoveryEndpoint"] = (
                governance_settings.service_cert_discovery.certificate_discovery_endpoint
            )
            replace_vars_dict["serviceCertDiscoverySnpHostData"] = (
                governance_settings.service_cert_discovery.host_data[0]
            )
            replace_vars_dict["serviceCertDiscoverySkipDigestCheck"] = (
                f"{governance_settings.service_cert_discovery.skip_digest_check}".lower()
            )
            replace_vars_dict["serviceCertDiscoveryConstitutionDigest"] = (
                f"{governance_settings.service_cert_discovery.constitution_digest}"
            )
            replace_vars_dict["serviceCertDiscoveryJsappBundleDigest"] = (
                f"{governance_settings.service_cert_discovery.js_app_bundle_digest}"
            )
        else:
            replace_vars_dict["serviceCertDiscoveryEndpoint"] = ""
            replace_vars_dict["serviceCertDiscoverySnpHostData"] = ""
            replace_vars_dict["serviceCertDiscoverySkipDigestCheck"] = ""
            replace_vars_dict["serviceCertDiscoveryConstitutionDigest"] = ""
            replace_vars_dict["serviceCertDiscoveryJsappBundleDigest"] = ""

        return self._get_sidecar("ccr-governance", replace_vars_dict)

    def _get_ccr_proxy_sidecar(
        self, listener_port: int, destination_port: int, fqdn: str = ""
    ) -> Sidecar:
        replace_vars_dict = {
            "caType": "cgs",
            "listenerPort": str(listener_port),
            "destinationPort": str(destination_port),
            "ccrFqdn": fqdn,
        }
        return self._get_sidecar("ccr-proxy-https-http", replace_vars_dict)

    def _get_identity_sidecar(
        self,
        identities: List[Tuple[Identity, str]],
        audience: str,
        telemetry_mount_path: str,
    ) -> Sidecar:
        identity_args = {
            "Identities": {"ManagedIdentities": [], "ApplicationIdentities": []}
        }

        for identity, subject in identities:
            if identity.tokenIssuer.issuerType == "AttestationBasedTokenIssuer":
                if (
                    identity.tokenIssuer.issuer.protocol
                    == ProtocolType.AzureAD_ManagedIdentity
                ):
                    if identity.clientId not in [
                        i["ClientId"]
                        for i in identity_args["Identities"]["ManagedIdentities"]
                    ]:
                        identity_args["Identities"]["ManagedIdentities"].append(
                            {"ClientId": identity.clientId}
                        )
            elif identity.tokenIssuer.issuerType == "FederatedIdentityBasedTokenIssuer":
                if identity.clientId not in [
                    i["ClientId"]
                    for i in identity_args["Identities"]["ApplicationIdentities"]
                ]:
                    identity_args["Identities"]["ApplicationIdentities"].append(
                        {
                            "ClientId": identity.clientId,
                            "Credential": {
                                "CredentialType": "FederatedCredential",
                                "FederationConfiguration": {
                                    "IdTokenEndpoint": "http://localhost:8300",
                                    "Subject": subject,
                                    "Audience": audience,
                                    "Issuer": identity.tokenIssuer.issuer.url,
                                },
                            },
                        }
                    )

        identity_args_base64 = base64.b64encode(
            bytes(json.dumps(identity_args), "utf-8")
        ).decode("utf-8")

        replace_vars_dict = {
            "IdentitySidecarArgsBase64": identity_args_base64,
            "OtelMetricExportInterval": "5000",
            "telemetryMountPath": telemetry_mount_path,
        }
        return self._get_sidecar("identity", replace_vars_dict)

    def _get_blobfuse_sidecar(
        self,
        access_point: AccessPoint,
        mount_path,
        access_name,
        telemetry_mount_path,
        volume_status_mount_path,
        readiness_port: int,
    ) -> Sidecar:
        # Sanitize access name to remove spaces and convert to lowercase and replace underscores with dashes.
        access_name = access_name.lower().replace(" ", "").replace("_", "-")
        encryption_config = json.loads(
            base64.b64decode(access_point.protection.configuration).decode()
        )
        encryption_mode = encryption_config["EncryptionMode"]

        # TODO (HPrabh): Add support for CSE.
        assert (
            encryption_mode != "CSE"
        ), f"Encryption mode {encryption_mode} is not supported for {access_name}."
        if encryption_mode == "CPK":
            assert (
                access_point.protection.encryptionSecrets
            ), f"Encryption secrets is null for {access_name}."
            dek_entry = access_point.protection.encryptionSecrets.dek
            assert (
                dek_entry.secret.backingResource.type == ResourceType.Cgs
            ), f"Expecting DEK secret backing resource type as '{str(ResourceType.Cgs)}' but value is '{dek_entry.secret.backingResource.type}'."
        storage_account_name = urlparse(access_point.store.provider.url).hostname.split(
            "."
        )[0]
        subdirectory = ""
        storageBlobEndpoint = access_point.store.provider.url
        storageContainerName = access_point.store.name
        use_adls = (
            True
            if access_point.store.provider.protocol
            in [
                ProtocolType.Azure_OneLake,
                ProtocolType.Azure_BlobStorage_DataLakeGen2,
            ]
            else False
        )
        if access_point.store.provider.protocol == ProtocolType.Azure_OneLake:
            storage_account_name = "onelake"
            parsed_onelake_url = urlparse(access_point.store.provider.url)
            storageBlobEndpoint = parsed_onelake_url.hostname
            storageContainerName = parsed_onelake_url.path.split("/")[1]
            subdirectory = "/".join(parsed_onelake_url.path.split("/")[2:])

        if self._trace_context:
            trace_context_json_b64 = base64.b64encode(
                (json.dumps(self._trace_context).encode())
            ).decode()
        else:
            trace_context_json_b64 = ""

        blobfuse_sidecar_replacement_vars = {
            "datasetName": access_name,
            "storageContainerName": storageContainerName,
            "mountPath": mount_path,
            "maaUrl": "",
            "storageAccountName": storage_account_name,
            "storageBlobEndpoint": storageBlobEndpoint,
            "readOnly": (
                "--read-only"
                if access_point.type == AccessPointType.Volume_ReadOnly
                else "--no-read-only"
            ),
            "kekVaultUrl": "",
            "kekKid": "",
            "dekVaultUrl": "",
            "dekSecretName": "",
            "cgsDekSecretId": (
                f"{dek_entry.secret.backingResource.name}"
                if encryption_mode == "CPK"
                else ""
            ),
            "clientId": access_point.identity.clientId,
            "tenantId": access_point.identity.tenantId,
            "encryptionMode": encryption_mode,
            "useAdls": ("--use-adls" if use_adls else "--no-use-adls"),
            "telemetryMountPath": telemetry_mount_path,
            "volumeStatusMountPath": volume_status_mount_path,
            "traceContextJsonBase64": trace_context_json_b64,
            "readinessPort": readiness_port,
        }

        sidecar = self._get_sidecar(
            "blobfuse-launcher",
            sidecar_replacement_vars=blobfuse_sidecar_replacement_vars,
        )

        # _get_sidecar's replacement logic results in say "6300" but we want 6300 as an integer for
        #  the port value in the startup probe. So need to assign manually to preserve the type.
        if sidecar.container.startup_probe:
            sidecar.container.startup_probe.http_get.port = readiness_port

        if subdirectory != "":
            sidecar.container.command.append("--sub-directory")
            sidecar.container.command.append(subdirectory)
            sidecar.virtual_node_policy["command"].extend(
                ["--sub-directory", subdirectory]
            )
        return sidecar

    def _get_s3fs_sidecar(
        self,
        access_point: AccessPoint,
        mount_path,
        access_name,
        telemetry_mount_path,
        volume_status_mount_path,
    ) -> Sidecar:
        # Sanitize access name to remove spaces and convert to lowercase and replace underscores with dashes.
        access_name = access_name.lower().replace(" ", "").replace("_", "-")
        assert (
            access_point.store.provider.configuration
        ), f"Store provider configuration is null for {access_name}."
        aws_config = json.loads(
            base64.b64decode(access_point.store.provider.configuration).decode()
        )
        aws_config_secret_id = aws_config["secretId"]
        awsUrl = access_point.store.provider.url
        awsBucketName = access_point.store.name

        if self._trace_context:
            trace_context_json_b64 = base64.b64encode(
                (json.dumps(self._trace_context).encode())
            ).decode()
        else:
            trace_context_json_b64 = ""

        assert (
            aws_config_secret_id is not None
        ), f"AWS config secret Id value is not set for {access_name}."

        s3fs_sidecar_replacement_vars = {
            "datasetName": access_name,
            "awsBucketName": awsBucketName,
            "readOnly": (
                "--read-only"
                if access_point.type == AccessPointType.Volume_ReadOnly
                else "--no-read-only"
            ),
            "mountPath": mount_path,
            "cgsAwsS3ConfigSecret": aws_config_secret_id,
            "awsUrl": awsUrl,
            "telemetryMountPath": telemetry_mount_path,
            "volumeStatusMountPath": volume_status_mount_path,
            "traceContextJsonBase64": trace_context_json_b64,
        }

        sidecar = self._get_sidecar(
            "s3fs-launcher",
            sidecar_replacement_vars=s3fs_sidecar_replacement_vars,
        )

        return sidecar

    def _get_otel_sidecar(
        self, telemetry_mount_path: str, extra_vars: dict = {}
    ) -> Sidecar:

        return self._get_sidecar(
            "otel-collector",
            {
                "telemetryCollectionEnabled": self._telemetry.telemetry_collection_enabled,
                "telemetryPath": "",
                "telemetryMountPath": telemetry_mount_path,
                "prometheusEndpoint": self._telemetry.prometheus_endpoint,
                "lokiEndpoint": self._telemetry.loki_endpoint,
                "tempoEndpoint": self._telemetry.tempo_endpoint,
                "sparkMetricsEndpoint": extra_vars.get("sparkMetricsEndpoint", ""),
                "resourceAttributes": extra_vars.get("resourceAttributes", ""),
            },
        )
