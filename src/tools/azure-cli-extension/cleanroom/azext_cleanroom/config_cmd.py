from cleanroom_common.azure_cleanroom_core.models.model import *


def config_add_identity_az_federated_cmd(
    cmd,
    cleanroom_config_file,
    name,
    client_id,
    tenant_id,
    backing_identity,
):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )
    from .utilities._azcli_helpers import logger
    from azure.cli.core.util import CLIError

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    identities = [x for x in spec.identities if x.name == backing_identity]
    if len(identities) == 0:
        raise CLIError(
            f"Identity {backing_identity} could not be found in the config file. "
            + f"First add the {backing_identity} before adding a federated identity."
        )

    federated_identity = Identity(
        name=name,
        clientId=client_id,
        tenantId=tenant_id,
        tokenIssuer=FederatedIdentityBasedTokenIssuer(
            issuer=ServiceEndpoint(
                protocol=ProtocolType.AzureAD_Federated,
                url="https://AzureAD",
            ),
            federatedIdentity=identities[0],
            issuerType="FederatedIdentityBasedTokenIssuer",
        ),
    )

    index = next((i for i, x in enumerate(spec.identities) if x.name == name), None)
    if index == None:
        logger.info(f"Adding entry for identity {name} in configuration.")
        spec.identities.append(federated_identity)
    else:
        logger.info(f"Patching identity {name} in configuration.")
        spec.identities[index] = federated_identity
    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_add_identity_az_secret_cmd(
    cmd,
    cleanroom_config_file,
    name,
    client_id,
    tenant_id,
    secret_name,
    secret_store_url,
    backing_identity,
):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )
    from .utilities._azcli_helpers import logger
    from azure.cli.core.util import CLIError

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    backing_identities = [x for x in spec.identities if x.name == backing_identity]
    if len(backing_identities) == 0:
        raise CLIError(
            f"Identity {backing_identity} could not be found in the config file."
            + f"First add the {backing_identity} before adding a secret based identity."
        )

    # Add Secret Stores
    secret_store = ServiceEndpoint(
        protocol=ProtocolType.AzureKeyVault_Secret,
        url=secret_store_url,
    )

    secret_identity = Identity(
        name=name,
        clientId=client_id,
        tenantId=tenant_id,
        tokenIssuer=SecretBasedTokenIssuer(
            issuer=ServiceEndpoint(
                protocol=ProtocolType.AzureAD_Secret,
                url="https://AzureAD",
            ),
            secret=CleanroomSecret(
                secretType=SecretType.Secret,
                backingResource=Resource(
                    name=secret_name,
                    id=secret_name,
                    provider=secret_store,
                    type=ResourceType.AzureKeyVault,
                ),
            ),
            secretAccessIdentity=backing_identities[0],
            issuerType="SecretBasedTokenIssuer",
        ),
    )

    index = next((i for i, x in enumerate(spec.identities) if x.name == name), None)
    if index == None:
        logger.info(f"Adding entry for identity {name} in configuration.")
        spec.identities.append(secret_identity)
    else:
        logger.info(f"Patching identity {name} in configuration.")
        spec.identities[index] = secret_identity
    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_add_identity_oidc_attested_cmd(
    cmd, cleanroom_config_file, name, client_id, tenant_id, issuer_url
):
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )
    from .utilities._azcli_helpers import logger
    from azure.cli.core.util import CLIError

    spec = read_cleanroom_spec_internal(cleanroom_config_file)

    secret_identity = Identity(
        name=name,
        clientId=client_id,
        tenantId=tenant_id,
        tokenIssuer=AttestationBasedTokenIssuer(
            issuer=ServiceEndpoint(protocol=ProtocolType.Attested_OIDC, url=issuer_url),
            issuerType="AttestationBasedTokenIssuer",
        ),
    )

    index = next((i for i, x in enumerate(spec.identities) if x.name == name), None)
    if index == None:
        logger.info(f"Adding entry for identity {name} in configuration.")
        spec.identities.append(secret_identity)
    else:
        logger.info(f"Patching identity {name} in configuration.")
        spec.identities[index] = secret_identity
    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_add_datasource_cmd(
    cmd,
    cleanroom_config_file,
    datastore_name,
    datastore_config_file,
    identity,
    secretstore_config_file,
    dek_secret_store,
    kek_secret_store,
    kek_name="",
    access_name="",
):
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
    from .utilities._datastore_helpers import config_add_datastore_internal
    from .utilities._azcli_helpers import logger

    config_add_datastore_internal(
        cleanroom_config_file,
        datastore_name,
        datastore_config_file,
        identity,
        secretstore_config_file,
        dek_secret_store,
        kek_secret_store,
        kek_name,
        DatastoreEntry.AccessMode.Source,
        logger,
        access_name,
    )


def config_add_datasink_cmd(
    cmd,
    cleanroom_config_file,
    datastore_name,
    datastore_config_file,
    identity,
    secretstore_config_file,
    dek_secret_store,
    kek_secret_store,
    kek_name="",
    access_name="",
):
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
    from .utilities._datastore_helpers import config_add_datastore_internal
    from .utilities._azcli_helpers import logger

    config_add_datastore_internal(
        cleanroom_config_file,
        datastore_name,
        datastore_config_file,
        identity,
        secretstore_config_file,
        dek_secret_store,
        kek_secret_store,
        kek_name,
        DatastoreEntry.AccessMode.Sink,
        logger,
        access_name,
    )


def config_set_telemetry_cmd(
    cmd,
    cleanroom_config_file,
    datastore_config_file,
    encryption_mode,
    storage_account,
    identity,
    secretstore_config_file,
    dek_secret_store,
    kek_secret_store,
    datastore_secret_store,
    kek_name="",
    container_suffix="",
):
    import os, re
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
    from .datastore_cmd import datastore_add_cmd
    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        generate_safe_datastore_name,
    )
    from .utilities._datastore_helpers import config_add_datastore_internal
    from .utilities._azcli_helpers import logger

    access_name = "infrastructure-telemetry"

    friendly_name = re.sub("[^A-Za-z0-9]+", "", os.path.basename(cleanroom_config_file))
    datastore_name = generate_safe_datastore_name(
        access_name, cleanroom_config_file, friendly_name
    )

    container_name = ""
    if container_suffix is not "":
        container_name = (f"{access_name}-{container_suffix}")[:63]

    datastore_add_cmd(
        cmd,
        datastore_name,
        datastore_config_file,
        secretstore_config_file,
        datastore_secret_store,
        encryption_mode,
        DatastoreEntry.StoreType.Azure_BlobStorage,
        storage_account,
        container_name,
    )

    config_add_datastore_internal(
        cleanroom_config_file,
        datastore_name,
        datastore_config_file,
        identity,
        secretstore_config_file,
        dek_secret_store,
        kek_secret_store,
        kek_name,
        DatastoreEntry.AccessMode.Sink,
        logger,
        access_name,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)
    telemetryDataSink = [x for x in spec.datasinks if x.name == access_name][0]
    if spec.governance is None:
        spec.governance = GovernanceSettings()
    if spec.governance.telemetry is None:
        spec.governance.telemetry = Telemetry()
    spec.governance.telemetry.infrastructure = InfrastructureTelemetry(
        metrics=telemetryDataSink,
        traces=telemetryDataSink,
        logs=telemetryDataSink,
    )

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def config_set_logging_cmd(
    cmd,
    cleanroom_config_file,
    datastore_config_file,
    encryption_mode,
    storage_account,
    identity,
    secretstore_config_file,
    dek_secret_store_name,
    kek_secret_store_name,
    datastore_secret_store,
    kek_name="",
    container_suffix="",
):
    import os, re
    from .datastore_cmd import datastore_add_cmd
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry

    from .utilities._configuration_helpers import (
        read_cleanroom_spec_internal,
        write_cleanroom_spec_internal,
    )
    from cleanroom_common.azure_cleanroom_core.utilities.datastore_helpers import (
        generate_safe_datastore_name,
    )
    from .utilities._datastore_helpers import config_add_datastore_internal
    from .utilities._azcli_helpers import logger

    access_name = "application-telemetry"
    friendly_name = re.sub("[^A-Za-z0-9]+", "", os.path.basename(cleanroom_config_file))
    datastore_name = generate_safe_datastore_name(
        access_name, cleanroom_config_file, friendly_name
    )

    container_name = ""
    if container_suffix is not "":
        container_name = (f"{access_name}-{container_suffix}")[:63]

    datastore_add_cmd(
        cmd,
        datastore_name,
        datastore_config_file,
        secretstore_config_file,
        datastore_secret_store,
        encryption_mode,
        DatastoreEntry.StoreType.Azure_BlobStorage,
        storage_account,
        container_name,
    )

    config_add_datastore_internal(
        cleanroom_config_file,
        datastore_name,
        datastore_config_file,
        identity,
        secretstore_config_file,
        dek_secret_store_name,
        kek_secret_store_name,
        kek_name,
        DatastoreEntry.AccessMode.Sink,
        logger,
        access_name,
    )

    spec = read_cleanroom_spec_internal(cleanroom_config_file)
    logsDataSink = [x for x in spec.datasinks if x.name == access_name][0]
    if spec.governance is None:
        spec.governance = GovernanceSettings()
    if spec.governance.telemetry is None:
        spec.governance.telemetry = Telemetry()
    spec.governance.telemetry.application = ApplicationTelemetry(logs=logsDataSink)

    write_cleanroom_spec_internal(cleanroom_config_file, spec)


def datasink_download_cmd(
    cmd, cleanroom_config_file, datasink_name, datastore_config, target_folder
):
    from .datastore_cmd import datastore_download_cmd
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
    from .utilities._datastore_helpers import config_get_datastore_name_internal

    datastore_name = config_get_datastore_name_internal(
        cleanroom_config_file, datasink_name, DatastoreEntry.AccessMode.Sink
    )

    datastore_download_cmd(cmd, datastore_name, datastore_config, target_folder)


def telemetry_decrypt_cmd(
    cmd, cleanroom_config_file, datastore_config_file, source_path, destination_path
):
    from .datastore_cmd import datastore_decrypt_cmd
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
    from .utilities._datastore_helpers import config_get_datastore_name_internal

    datastore_name = config_get_datastore_name_internal(
        cleanroom_config_file,
        "infrastructure-telemetry",
        DatastoreEntry.AccessMode.Sink,
    )

    datastore_decrypt_cmd(
        cmd,
        datastore_name,
        datastore_config_file,
        source_path,
        destination_path,
        blockSize=4,
    )


def logs_decrypt_cmd(
    cmd, cleanroom_config_file, datastore_config_file, source_path, destination_path
):
    from .datastore_cmd import datastore_decrypt_cmd
    from cleanroom_common.azure_cleanroom_core.models.datastore import DatastoreEntry
    from .utilities._datastore_helpers import config_get_datastore_name_internal

    datastore_name = config_get_datastore_name_internal(
        cleanroom_config_file,
        "application-telemetry",
        DatastoreEntry.AccessMode.Sink,
    )

    datastore_decrypt_cmd(
        cmd,
        datastore_name,
        datastore_config_file,
        source_path,
        destination_path,
        blockSize=4,
    )


def telemetry_download_cmd(cmd, cleanroom_config_file, datastore_config, target_folder):
    datasink_download_cmd(
        cmd,
        cleanroom_config_file,
        "infrastructure-telemetry",
        datastore_config,
        target_folder,
    )


def logs_download_cmd(cmd, cleanroom_config_file, datastore_config, target_folder):
    datasink_download_cmd(
        cmd,
        cleanroom_config_file,
        "application-telemetry",
        datastore_config,
        target_folder,
    )
