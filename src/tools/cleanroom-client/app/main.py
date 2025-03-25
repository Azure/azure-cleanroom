import hashlib
import base64
import uuid

import yaml
from fastapi import FastAPI
from fastapi.responses import PlainTextResponse
from fastapi import HTTPException

from pydantic import BaseModel

from azure.cli.core import get_default_cli, CLIError

import os
import logging
from enum import StrEnum

from cleanroom_common.azure_cleanroom_core.models.model import (
    CleanRoomSpecification,
)
from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
    get_deployment_template,
    validate_config,
)

logger = logging.getLogger()

# Set debug to true as we are ok exposing the stack trace details in the error
app = FastAPI(description="Cleanroom Client API", debug=True)


def az_cli(args_str: str):
    args = args_str.split()
    return az_cli_ex(args)


def az_cli_ex(args: list[str]):
    cli = get_default_cli()
    out_file = open(os.devnull, "w")
    try:
        cli.invoke(args, out_file=out_file)
    except SystemExit:
        pass
    except:
        logger.warning(f"Command failed: {args}")
        raise

    if cli.result.result:
        return cli.result.result
    elif cli.result.error:
        if isinstance(cli.result.error, SystemExit):
            if cli.result.error.code == 0:
                return {}
        logger.warning(f"Command failed: {args}, {cli.result.error}")
        raise cli.result.error
    return {}


class LoginRequest(BaseModel):
    loginArgs: list[str] | None = None


class EncryptionMode(StrEnum):
    CPK = "CPK"
    CSE = "CSE"


class AddDatastoreRequest(BaseModel):
    class BackingStoreType(StrEnum):
        Azure_BlobStorage = "Azure_BlobStorage"
        Azure_Onelake = "Azure_Onelake"

    configName: str
    name: str
    secretStore: str
    secretStoreConfig: str
    encryptionMode: EncryptionMode
    backingStoreType: BackingStoreType
    backingStoreId: str
    containerName: str | None = None


class UploadDataStoreRequest(BaseModel):
    name: str
    configName: str
    src: str


class AddSecretStoreRequest(BaseModel):
    class SecretStoreType(StrEnum):
        Azure_KeyVault = "Azure_KeyVault"
        Azure_KeyVault_Managed_HSM = "Azure_KeyVault_Managed_HSM"
        Local_File = "Local_File"

    name: str
    configName: str
    backingStoreType: SecretStoreType
    backingStoreId: str | None = None
    backingStorePath: str | None = None
    attestationEndpoint: str | None = None


class ConfigInitRequest(BaseModel):
    configName: str


class ConfigViewRequest(BaseModel):
    configName: str
    outputFile: str
    configs: list[str] | None = None


class ConfigValidateRequest(BaseModel):
    configName: str


class ConfigAddDatasourceRequest(BaseModel):
    identity: str
    configName: str
    datastoreName: str
    datastoreConfigName: str
    secretStoreConfig: str
    dekSecretStore: str
    kekSecretStore: str


class ConfigAddDatasinkRequest(BaseModel):
    identity: str
    configName: str
    datastoreName: str
    datastoreConfigName: str
    secretStoreConfig: str
    dekSecretStore: str
    kekSecretStore: str


class LogsDownloadRequest(BaseModel):
    targetFolder: str
    configName: str
    datastoreConfigName: str


class TelemetryDownloadRequest(BaseModel):
    targetFolder: str
    configName: str
    datastoreConfigName: str


class DataStoreDownloadRequest(BaseModel):
    name: str
    targetFolder: str
    configName: str


class ConfigSetLoggingRequest(BaseModel):
    storageAccountId: str
    identity: str
    configName: str
    datastoreConfigName: str
    secretStoreConfig: str
    datastoreSecretStore: str
    dekSecretStore: str
    kekSecretStore: str
    encryptionMode: EncryptionMode
    containerSuffix: str | None = None


class ConfigSetTelemetryRequest(BaseModel):
    storageAccountId: str
    identity: str
    configName: str
    datastoreConfigName: str
    secretStoreConfig: str
    datastoreSecretStore: str
    dekSecretStore: str
    kekSecretStore: str
    encryptionMode: EncryptionMode
    containerSuffix: str | None = None


class ConfigAddApplicationRequest(BaseModel):
    name: str
    image: str
    command: str | None = None
    datasources: list[str] | None = None
    datasinks: list[str] | None = None
    environmentVariables: list[str] | None = None
    cpu: str
    memory: str
    autoStart: bool
    configName: str


class ConfigAddApplicationEndpointRequest(BaseModel):
    configName: str
    applicationName: str
    port: int
    policyBundleUrl: str | None = None


class ConfigCreateKekRequest(BaseModel):
    contractId: str
    cleanroomPolicy: str
    configName: str
    secretStoreConfig: str


class ConfigWrapDeksRequest(BaseModel):
    contractId: str
    configName: str
    datastoreConfigName: str
    secretStoreConfig: str


class ConfigWrapSecretRequest(BaseModel):
    contractId: str
    name: str
    value: str
    secretKeyVaultId: str
    keyStore: str
    configName: str


class ConfigDisableSandboxRequest(BaseModel):
    configName: str


class ConfigEnableSandboxRequest(BaseModel):
    configName: str


class ConfigAddIdentityAzFederatedRequest(BaseModel):
    configName: str
    name: str
    clientId: str
    tenantId: str
    backingIdentity: str | None = None


class ConfigAddIdentityAzSecretBased(BaseModel):
    configName: str
    name: str
    clientId: str
    tenantId: str
    secretName: str
    secretStoreUrl: str
    backingIdentity: str | None = None


class ConfigAddIdentityOIDCAttested(BaseModel):
    configName: str
    name: str
    clientId: str
    tenantId: str
    issuerUrl: str


class DeploymentGenerateRequest(BaseModel):
    spec: str
    contract_id: str
    ccf_endpoint: str
    ssl_server_cert_base64: str
    debug_mode: bool | None = False
    operationId: str | None = None


@app.get("/")
def read_root():
    response = az_cli("version")
    return {"version": response}


@app.post("/login")
def login(request: LoginRequest):
    args = [
        "login",
        "--identity",
    ]
    if request.loginArgs:
        args.extend(request.loginArgs)

    return az_cli_ex(args)


@app.get("/account/show")
def account_show():
    args = ["account", "show"]
    try:
        return az_cli_ex(args)
    except CLIError as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/add-secretstore")
def add_secretstore(request: AddSecretStoreRequest):
    args = [
        "cleanroom",
        "secretstore",
        "add",
        "--name",
        f"{request.name}",
        "--config",
        f"{request.configName}",
        "--backingstore-type",
        f"{request.backingStoreType.value}",
    ]

    if request.backingStoreId:
        args.append("--backingstore-id")
        args.append(request.backingStoreId)

    if request.attestationEndpoint:
        args.append("--attestation-endpoint")
        args.append(request.attestationEndpoint)

    if request.backingStorePath:
        args.append("--backingstore-path")
        args.append(request.backingStorePath)

    return az_cli_ex(args)


@app.post("/add-datastore")
def add_datastore(request: AddDatastoreRequest):
    args = [
        "cleanroom",
        "datastore",
        "add",
        "--name",
        f"{request.name}",
        "--config",
        f"{request.configName}",
        "--secretstore",
        f"{request.secretStore}",
        "--secretstore-config",
        f"{request.secretStoreConfig}",
        "--encryption-mode",
        f"{request.encryptionMode.value}",
        "--backingstore-type",
        f"{request.backingStoreType.value}",
        "--backingstore-id",
        f"{request.backingStoreId}",
    ]

    if request.containerName:
        args.append("--container-name")
        args.append(request.containerName)

    return az_cli_ex(args)


@app.post("/upload-datastore")
def upload_datastore(request: UploadDataStoreRequest):
    return az_cli(
        f"cleanroom datastore upload "
        + f"--name {request.name} "
        + f"--config {request.configName} "
        + f"--src {request.src}"
    )


@app.post("/add-identity-az-federated")
def config_add_identity_az_federated(request: ConfigAddIdentityAzFederatedRequest):
    args = [
        f"cleanroom",
        f"config",
        f"add-identity",
        f"az-federated",
        f"--cleanroom-config",
        f"{request.configName}",
        f"-n",
        f"{request.name}",
        f"--client-id",
        f"{request.clientId}",
        f"--tenant-id",
        f"{request.tenantId}",
    ]

    backing_identity = "cleanroom_cgs_oidc"
    if request.backingIdentity:
        backing_identity = request.backingIdentity

    args.extend([f"--backing-identity", f"{backing_identity}"])

    return az_cli_ex(args)


@app.post("/add-identity-az-secret")
def config_add_identity_az_secret(request: ConfigAddIdentityAzSecretBased):
    args = [
        f"cleanroom",
        f"config",
        f"add-identity",
        f"az-federated",
        f"--cleanroom-config",
        f"{request.configName}",
        f"-n",
        f"{request.name}",
        f"--client-id",
        f"{request.clientId}",
        f"--tenant-id",
        f"{request.tenantId}",
        f"--secret-name",
        f"{request.secretName}",
        f"--secret-store-url",
        f"{request.secretStoreUrl}",
    ]

    backing_identity = "cleanroom_cgs_oidc"
    if request.backingIdentity:
        backing_identity = request.backingIdentity

    args.extend([f"--backing-identity", f"{backing_identity}"])

    return az_cli_ex(args)


@app.post("/add-identity-oidc-attested")
def config_add_identity_oidc_attested(request: ConfigAddIdentityOIDCAttested):
    args = [
        f"cleanroom",
        f"config",
        f"add-identity",
        f"az-federated",
        f"--cleanroom-config",
        f"{request.configName}",
        f"-n",
        f"{request.name}",
        f"--client-id",
        f"{request.clientId}",
        f"--tenant-id",
        f"{request.tenantId}",
        f"--issuer-url",
        f"{request.issuerUrl}",
    ]

    return az_cli_ex(args)


@app.post("/config/view", response_class=PlainTextResponse)
def config_get(request: ConfigViewRequest):
    args = [
        "cleanroom",
        "config",
        "view",
        "--cleanroom-config",
        f"{request.configName}",
        "--out-file",
        f"{request.outputFile}",
        "--no-print",
    ]

    if request.configs:
        args.append("--configs")
        args.extend(request.configs)

    return az_cli_ex(args)


@app.post("/config/init")
def config_init(request: ConfigInitRequest):
    return az_cli(f"cleanroom config init --cleanroom-config {request.configName}")


@app.post("/config/validate")
def config_validate(request: ConfigValidateRequest):
    return az_cli(f"cleanroom config validate --cleanroom-config {request.configName}")


@app.post("/config/add-datasource")
def config_add_datasource(request: ConfigAddDatasourceRequest):
    args = [
        "cleanroom",
        "config",
        "add-datasource",
        "--cleanroom-config",
        f"{request.configName}",
        "--datastore-name",
        f"{request.datastoreName}",
        "--datastore-config",
        f"{request.datastoreConfigName}",
        "--identity",
        f"{request.identity}",
        "--secretstore-config",
        f"{request.secretStoreConfig}",
        "--dek-secret-store",
        f"{request.dekSecretStore}",
        "--kek-secret-store",
        f"{request.kekSecretStore}",
    ]

    return az_cli_ex(args)


@app.post("/config/add-datasink")
def config_add_datasink(request: ConfigAddDatasinkRequest):
    args = [
        "cleanroom",
        "config",
        "add-datasink",
        "--cleanroom-config",
        f"{request.configName}",
        "--datastore-name",
        f"{request.datastoreName}",
        "--datastore-config",
        f"{request.datastoreConfigName}",
        "--identity",
        f"{request.identity}",
        "--secretstore-config",
        f"{request.secretStoreConfig}",
        "--dek-secret-store",
        f"{request.dekSecretStore}",
        "--kek-secret-store",
        f"{request.kekSecretStore}",
    ]

    return az_cli_ex(args)


@app.post("/datastore/download")
def datastore_download(request: DataStoreDownloadRequest):
    return az_cli(
        f"cleanroom datastore download "
        + f"--config {request.configName} "
        + f"--name {request.name} "
        + f"--dst {request.targetFolder}"
    )


@app.post("/logs/download")
def logs_download(request: LogsDownloadRequest):
    return az_cli(
        f"cleanroom logs download "
        + f"--target-folder {request.targetFolder} "
        + f"--datastore-config {request.datastoreConfigName} "
        + f"--cleanroom-config {request.configName}"
    )


@app.post("/telemetry/download")
def telemetry_download(request: TelemetryDownloadRequest):
    return az_cli(
        f"cleanroom telemetry download "
        + f"--target-folder {request.targetFolder} "
        + f"--datastore-config {request.datastoreConfigName} "
        + f"--cleanroom-config {request.configName}"
    )


@app.post("/config/set-logging")
def config_set_logging(request: ConfigSetLoggingRequest):
    args = [
        "cleanroom",
        "config",
        "set-logging",
        "--cleanroom-config",
        f"{request.configName}",
        "--storage-account",
        f"{request.storageAccountId}",
        "--identity",
        f"{request.identity}",
        "--datastore-config",
        f"{request.datastoreConfigName}",
        "--secretstore-config",
        f"{request.secretStoreConfig}",
        "--datastore-secret-store",
        f"{request.datastoreSecretStore}",
        "--dek-secret-store",
        f"{request.dekSecretStore}",
        "--kek-secret-store",
        f"{request.kekSecretStore}",
        "--encryption-mode",
        f"{request.encryptionMode.value}",
    ]

    if request.containerSuffix:
        args.append("--container-suffix")
        args.append(request.containerSuffix)

    return az_cli_ex(args)


@app.post("/config/set-telemetry")
def config_set_telemetry(request: ConfigSetTelemetryRequest):
    args = [
        "cleanroom",
        "config",
        "set-telemetry",
        "--cleanroom-config",
        f"{request.configName}",
        "--storage-account",
        f"{request.storageAccountId}",
        "--identity",
        f"{request.identity}",
        "--datastore-config",
        f"{request.datastoreConfigName}",
        "--secretstore-config",
        f"{request.secretStoreConfig}",
        "--datastore-secret-store",
        f"{request.datastoreSecretStore}",
        "--dek-secret-store",
        f"{request.dekSecretStore}",
        "--kek-secret-store",
        f"{request.kekSecretStore}",
        "--encryption-mode",
        f"{request.encryptionMode.value}",
    ]

    if request.containerSuffix:
        args.append("--container-suffix")
        args.append(request.containerSuffix)

    return az_cli_ex(args)


@app.post("/config/add-application")
def config_add_application(request: ConfigAddApplicationRequest):
    args = [
        "cleanroom",
        "config",
        "add-application",
        "--name",
        f"{request.name}",
        "--image",
        f"{request.image}",
        "--cpu",
        f"{request.cpu}",
        "--memory",
        f"{request.memory}",
        "--cleanroom-config",
        f"{request.configName}",
    ]

    if request.command:
        args.append("--command")
        args.append(request.command)

    if request.datasources:
        args.append("--datasources")
        args.extend(request.datasources)

    if request.datasinks:
        args.append("--datasinks")
        args.extend(request.datasinks)

    if request.environmentVariables:
        args.append("--env-vars")
        args.extend(request.environmentVariables)

    if request.autoStart:
        args.append("--auto-start")
    return az_cli_ex(args)


@app.post("/config/add-application-endpoint")
def config_add_application_endpoint(request: ConfigAddApplicationEndpointRequest):
    args = [
        "cleanroom",
        "config",
        "add-application-endpoint",
        "--application-name",
        f"{request.applicationName}",
        "--port",
        f"{request.port}",
        "--cleanroom-config",
        f"{request.configName}",
    ]

    if request.policyBundleUrl:
        args.append("--policy-bundle-url")
        args.append(request.policyBundleUrl)

    return az_cli_ex(args)


@app.post("/config/create-kek")
def config_create_kek(request: ConfigCreateKekRequest):
    return az_cli(
        f"cleanroom config create-kek "
        + f"--contract-id {request.contractId} "
        + f"--cleanroom-policy {request.cleanroomPolicy} "
        + f"--cleanroom-config {request.configName} "
        + f"--secretstore-config {request.secretStoreConfig}"
    )


@app.post("/config/wrap-deks")
def config_wrap_deks(request: ConfigWrapDeksRequest):
    return az_cli(
        f"cleanroom config wrap-deks "
        + f"--contract-id {request.contractId} "
        + f"--cleanroom-config {request.configName} "
        + f"--datastore-config {request.datastoreConfigName} "
        + f"--secretstore-config {request.secretStoreConfig}"
    )


@app.post("/config/wrap-secret")
def config_wrap_secret(request: ConfigWrapSecretRequest):
    return az_cli(
        f"cleanroom config wrap-secret "
        + f"--name {request.name} "
        + f"--value {request.value} "
        + f"--secret-key-vault {request.secretKeyVaultId} "
        + f"--contract-id {request.contractId} "
        + f"--key-store {request.keyStore} "
        + f"--cleanroom-config {request.configName}"
    )


@app.post("/config/disable-sandbox")
def config_disable_sandbox(request: ConfigDisableSandboxRequest):
    return az_cli(
        f"cleanroom config disable-sandbox "
        + f"--cleanroom-config {request.configName}"
    )


@app.post("/config/enable-sandbox")
def config_enable_sandbox(request: ConfigEnableSandboxRequest):
    return az_cli(
        f"cleanroom config enable-sandbox --cleanroom-config {request.configName}"
    )


@app.post("/deployment/generate")
def deployment_generate(request: DeploymentGenerateRequest):

    contract_yaml = yaml.safe_load(request.spec)
    spec = CleanRoomSpecification(**contract_yaml)

    # Currently ignore warnings.
    issues, _ = validate_config(spec, logger)

    errors = [{"code": x.code, "message": x.message} for x in issues]
    if len(issues) > 0:
        raise HTTPException(status_code=400, detail=errors)

    debug_mode = request.debug_mode if request.debug_mode else False
    operation_id = request.operationId if request.operationId else str(uuid.uuid4())

    logger.info(
        f"Generating deployment template for {request.contract_id}, operationId: {operation_id}"
    )

    arm_template, _, policy_rego = get_deployment_template(
        spec,
        request.contract_id,
        request.ccf_endpoint,
        request.ssl_server_cert_base64,
        debug_mode,
        logger,
    )

    cce_policy_base64 = base64.b64encode(bytes(policy_rego, "utf-8")).decode("utf-8")
    cce_policy_hash = hashlib.sha256(bytes(policy_rego, "utf-8")).hexdigest()

    arm_template["resources"][0]["properties"]["confidentialComputeProperties"][
        "ccePolicy"
    ] = cce_policy_base64

    policy_json = {
        "type": "add",
        "claims": {
            "x-ms-sevsnpvm-is-debuggable": False,
            "x-ms-sevsnpvm-hostdata": cce_policy_hash,
        },
    }

    return {
        "arm_template": arm_template,
        "policy_json": policy_json,
    }
