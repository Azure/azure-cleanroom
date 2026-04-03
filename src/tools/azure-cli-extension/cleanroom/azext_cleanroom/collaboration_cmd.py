import base64
import json
import uuid
from typing import Union

import requests
from azure.cli.core.util import CLIError, shell_safe_json_parse
from cleanroom_common.azure_cleanroom_core.models.dataset import DatasetSpecification
from cleanroom_common.azure_cleanroom_core.models.spark import SparkSQLApplication

from .utilities._azcli_helpers import logger
from .utilities._datastore_helpers import DataStoreConfiguration
from .utilities._querysegment_helpers import QuerySegmentHelper
from .utilities._secretstore_helpers import SecretStoreConfiguration
from .utilities.collaboration_helper import (
    CollaborationConfiguration,
    CollaborationContext,
)


def collaboration_context_add_cmd(
    cmd,
    collaboration_name: str,
    collaborator_id: str,
    governance_client_name: str = "",
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Connect to a collaboration and set it as the current context.

    :param collaboration_name: Friendly name of the collaboration to connect.
    :param collaborator_id: Identity of the collaborator.
    :param governance_client_name: Name of the governance client to be used for connecting to the
      collaboration. Defaults to the collaboration name if not provided.
    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    if governance_client_name is None or governance_client_name == "":
        governance_client_name = collaboration_name

    collaboration_config = CollaborationConfiguration.load(
        collaboration_config_file,
        create_if_not_existing=True,
        require_current_context=False,
    )
    exists, index, collaboration_context = (
        collaboration_config.check_collaboration_context(collaboration_name)
    )
    if exists:
        logger.warning(
            f"Collaboration '{collaboration_name}' already exists ({index}):\\n{collaboration_context}"
        )
    else:
        collaboration_context = CollaborationContext(
            name=collaboration_name,
            collaborator_id=collaborator_id,
            governance_client_name=governance_client_name,
            identities=[],
        )
        collaboration_config.add_collaboration_context(
            collaboration_context, set_current_context=True
        )
        CollaborationConfiguration.store(
            collaboration_config_file, collaboration_config
        )
        logger.warning(
            f"Collaboration '{collaboration_name}' added to collaboration configuration."
        )
    assert (
        collaboration_context is not None
    ), f"Collaboration context for {collaboration_name} should not be None at this point in code."

    from .custom import governance_user_identity_show_cmd

    governance_user_identity_show_cmd(
        cmd,
        identity_id=collaboration_context.collaborator_id,
        gov_client_name=collaboration_context.governance_client_name,
    )


def collaboration_context_set_cmd(
    cmd,
    collaboration_name: str,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Set the current collaboration context.

    :param collaboration_name: Friendly name of the collaboration to be set as the current context.
    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    CollaborationConfiguration.set_default_collaboration_context(
        collaboration_config_file, collaboration_name
    )


def collaboration_context_show_cmd(
    cmd,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Show the current collaboration context.

    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    collaboration = CollaborationConfiguration.get_default_collaboration_context(
        collaboration_config_file
    )
    print(
        f"Collaboration: {collaboration.name}, "
        + f"User ID: {collaboration.collaborator_id}, "
        + f"Governance Client: {collaboration.governance_client_name}"
    )

    return collaboration


def collaboration_context_list_cmd(
    cmd,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """List the registered collaboration contexts.

    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    collaboration_config = CollaborationConfiguration.load(
        collaboration_config_file,
        create_if_not_existing=True,
        require_current_context=False,
    )
    if not collaboration_config.collaborations:
        raise CLIError("No collaborations found in the configuration.")

    for collaboration in collaboration_config.collaborations:
        print(
            f"Collaboration: {collaboration.name}, "
            + f"User ID: {collaboration.collaborator_id}, "
            + f"Governance Client: {collaboration.governance_client_name}"
        )
    return collaboration_config.collaborations


def collaboration_identity_add_az_federated_cmd(
    cmd,
    identity_name: str,
    client_id: str,
    tenant_id: str,
    backing_identity_name: str,
    token_issuer_url: str = "",
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Add an Azure Identity backed by federated credentials to the current collaboration context.

    :param identity_name: Friendly name of the identity being registered.
    :param client_id: Client ID of the Azure Identity.
    :param tenant_id: Tenant ID of the Azure Identity.
    :param backing_identity_name: Friendly name of an existing identity in the configuration
        that will back this federated identity.
    :param token_issuer_url: The URL of the token issuer for federation.
    :param collaboration_config_file: Path to the collaboration configuration file.
    """

    from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
        CleanroomSpecificationError,
        ErrorCode,
    )
    from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
        IdentityManager,
    )

    collaboration_config = CollaborationConfiguration.load(
        collaboration_config_file, require_current_context=True
    )
    collaboration_context = collaboration_config.get_active_collaboration_context()

    try:
        IdentityManager(
            collaboration_context.identities, logger
        ).add_identity_az_federated(
            name=identity_name,
            client_id=client_id,
            tenant_id=tenant_id,
            token_issuer_url=token_issuer_url,
            backing_identity_name=backing_identity_name,
        )
    except CleanroomSpecificationError as e:
        match e.code:
            case ErrorCode.BackingIdentityNotFound:
                raise CLIError(
                    f"Identity {backing_identity_name} could not be found in the config file. "
                    + f"First add the {backing_identity_name} before adding a federated identity."
                )
            case _:
                raise CLIError(f"Error adding identity: {e}")

    CollaborationConfiguration.store(collaboration_config_file, collaboration_config)


def collaboration_identity_add_az_secret_cmd(
    cmd,
    identity_name: str,
    client_id: str,
    tenant_id: str,
    secret_name: str,
    secret_store_url: str,
    secret_access_identity_name: str,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Add an Azure Identity backed by a secret to the current collaboration context.

    :param identity_name: Friendly name of the identity being registered.
    :param client_id: Client ID of the Azure Identity.
    :param tenant_id: Tenant ID of the Azure Identity.
    :param secret_name: The name of the secret to be used for obtaining the token.
    :param secret_store_url: The URL of the secret store.
    :param secret_access_identity_name: Friendly name of an existing identity in the configuration
        used to access the secret.
    :param collaboration_config_file: Path to the collaboration configuration file.
    """

    from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
        CleanroomSpecificationError,
        ErrorCode,
    )
    from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
        IdentityManager,
    )

    collaboration_config = CollaborationConfiguration.load(
        collaboration_config_file, require_current_context=True
    )
    collaboration_context = collaboration_config.get_active_collaboration_context()
    try:
        IdentityManager(
            collaboration_context.identities, logger
        ).add_identity_az_secret(
            name=identity_name,
            client_id=client_id,
            tenant_id=tenant_id,
            secret_name=secret_name,
            secret_store_url=secret_store_url,
            backing_identity_name=secret_access_identity_name,
        )
    except CleanroomSpecificationError as e:
        match e.code:
            case ErrorCode.BackingIdentityNotFound:
                raise CLIError(
                    f"Identity {secret_access_identity_name} could not be found in the config file. "
                    + f"First add the {secret_access_identity_name} before adding a secret based identity."
                )
            case _:
                raise CLIError(f"Error adding identity: {e}")

    CollaborationConfiguration.store(collaboration_config_file, collaboration_config)


def collaboration_identity_add_oidc_attested_cmd(
    cmd,
    identity_name: str,
    client_id: str,
    tenant_id: str,
    issuer_url: str,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Add an attestation based OIDC Identity to the current collaboration context.

    :param identity_name: Friendly name of the identity being registered.
    :param client_id: Client ID for the OIDC Identity.
    :param tenant_id: Tenant ID for the OIDC Identity.
    :param issuer_url: The issuer URL for the OIDC Identity.
    :param collaboration_config_file: Path to the collaboration configuration file.
    """

    from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
        IdentityManager,
    )

    collaboration_config = CollaborationConfiguration.load(
        collaboration_config_file, require_current_context=True
    )
    collaboration_context = collaboration_config.get_active_collaboration_context()
    IdentityManager(
        collaboration_context.identities, logger
    ).add_identity_oidc_attested(
        name=identity_name,
        client_id=client_id,
        tenant_id=tenant_id,
        issuer_url=issuer_url,
    )

    CollaborationConfiguration.store(collaboration_config_file, collaboration_config)


def collaboration_identity_list_cmd(
    cmd,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """List the identities registered for the current collaboration context.

    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
        IdentityManager,
    )

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )

    identities = IdentityManager(collaboration_context.identities, logger)
    for identity in identities.identities:
        print(
            f"Identity: {identity.name}, "
            + f"Type: {identity.tokenIssuer.issuerType}, "
            + f"Client ID: {identity.clientId}, "
            + f"Tenant ID: {identity.tenantId}"
        )

    return identities.identities


def collaboration_dataset_publish_cmd(
    cmd,
    dataset_name: str,
    datastore_name: str,
    identity_name: str,
    dek_secret_store_name: str = "",
    kek_secret_store_name: str = "",
    workload: str = "",
    contract_id: str = "",
    policy_file: str = "",
    policy_access_mode: str = "",
    policy_allowed_fields: str = "",
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
    datastore_config_file: str = DataStoreConfiguration.default_datastore_config_file(),
    secretstore_config_file: str = SecretStoreConfiguration.default_secretstore_config_file(),
    kek_name: str = "",
):
    """Publish a dataset to the collaboration.

    :param dataset_name: Unique name of the dataset to publish.
    :param datastore_name: Datastore containing the data.
    :param identity_name: Identity to be used for datastore access.
    :param dek_secret_store_name: Secret store for the Data Encryption Key.
    :param kek_secret_store_name: Secret store for the Key Encryption Key.
    :param workload: Workload for which the dataset is being published (mutually exclusive with --contract-id).
    :param contract_id: Contract for which the dataset is being published (mutually exclusive with --workload).
    :param policy_file: Dataset access policy file.
    :param policy_access_mode: Access mode for the dataset policy [read | write] (required if no --policy-file).
    :param policy_allowed_fields: Comma-separated allowed list of dataset fields (required if no --policy-file).
    :param collaboration_config_file: Path of the collaboration configuration file.
    :param datastore_config_file: Path of the datastore configuration file.
    :param secretstore_config_file: Path of the secret store configuration file.
    :param kek_name: Name of the Key Encryption Key to be generated.
    """

    from cleanroom_common.azure_cleanroom_core.models.dataset import (
        AccessMode,
        Workload,
    )
    from cleanroom_common.azure_cleanroom_core.models.datastore import DataStoreEntry
    from cleanroom_common.azure_cleanroom_core.utilities.identity_manager import (
        IdentityManager,
    )

    from .utilities._dataset_helpers import (
        generate_access_policy_from_fields,
        load_access_policy_from_file,
    )

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )

    datastore_entry = DataStoreConfiguration.get_datastore(
        datastore_name, datastore_config_file
    )

    dek_secret_store = None
    dek_secret_store_entry = None
    if dek_secret_store_name != "":
        dek_secret_store = SecretStoreConfiguration.get_secretstore(
            dek_secret_store_name, secretstore_config_file
        )
        dek_secret_store_entry = dek_secret_store.entry
    kek_secret_store = None
    kek_secret_store_entry = None
    if kek_secret_store_name != "":
        kek_secret_store = SecretStoreConfiguration.get_secretstore(
            kek_secret_store_name, secretstore_config_file
        )
        kek_secret_store_entry = kek_secret_store.entry

    if not ((workload != "") ^ (contract_id != "")):
        raise CLIError(
            "One of workload or contract_id must be provided to publish a dataset."
        )

    # Validate and convert access mode
    if workload != "":
        try:
            dataset_workload = Workload(workload.strip().lower())
        except ValueError:
            valid_workloads = [workload.value for workload in Workload]
            raise CLIError(
                f"Invalid workload '{workload}'. Valid workloads are: {', '.join(valid_workloads)}"
            )

        # TODO: Fetch contract for this workload.
        contract_id = f"{dataset_workload}-42"

    if datastore_entry.datasetSchema is None:
        raise CLIError(
            f"Datastore {datastore_name} does not have a schema defined. "
            + "Please define a schema before publishing the dataset."
        )

    dataset_access_identity = IdentityManager(
        collaboration_context.identities, logger
    ).get_identity(identity_name)

    # Attach an access policy to the dataset.
    policy = None
    if policy_file != "":
        policy = load_access_policy_from_file(policy_file)
    else:
        if policy_access_mode == "" or policy_allowed_fields == "":
            raise CLIError(
                "Access mode and allowed fields must be provided when not using a policy file."
            )
        policy = generate_access_policy_from_fields(
            policy_access_mode, policy_allowed_fields.split(",")
        )

    # Create an access point for the datastore.
    dataset_accesspoint = datastore_entry.get_access_point(
        access_name=dataset_name,
        access_mode=(
            DataStoreEntry.AccessMode.Source
            if policy.accessMode == AccessMode.read
            else DataStoreEntry.AccessMode.Sink
        ),
        access_identity=dataset_access_identity,
        kek_name=kek_name,
        dek_secret_store_entry=dek_secret_store_entry,
        kek_secret_store_entry=kek_secret_store_entry,
    )

    if dataset_accesspoint.protection.encryptionSecrets is not None:
        assert (
            dek_secret_store is not None and kek_secret_store is not None
        ), f"Secret stores not specified for publishing dataset {datastore_entry.name}"

        # Generate KEK and wrapped DEK for the access point using the contract.
        from .custom import create_kek, governance_deployment_policy_show_cmd
        from .utilities._datastore_helpers import generate_wrapped_dek

        cl_policy = governance_deployment_policy_show_cmd(
            cmd,
            contract_id,
            gov_client_name=collaboration_context.governance_client_name,
        )
        if (
            not "policy" in cl_policy
            or not "x-ms-sevsnpvm-hostdata" in cl_policy["policy"]
        ):
            raise CLIError(
                f"No clean room policy found under contract '{contract_id}'. Check "
                + "--contract-id parameter is correct and that a policy proposal for the contract "
                + "has been accepted."
            )

        assert (
            dataset_accesspoint.protection.encryptionSecrets.kek is not None
        ), f"KEK not specified for datastore {datastore_entry.name}"

        kek_name = (
            dataset_accesspoint.protection.encryptionSecrets.kek.secret.backingResource.name
        )
        create_kek(
            secretstore_config_file,
            kek_secret_store_name,
            kek_name,
            cl_policy["policy"]["x-ms-sevsnpvm-hostdata"][0],
        )
        logger.info(f"Created KEK {kek_name} for {datastore_entry.name}")

        public_key = kek_secret_store.get_secret(kek_name)
        wrapped_dek_name = (
            dataset_accesspoint.protection.encryptionSecrets.dek.secret.backingResource.name
        )
        logger.warning(
            f"Creating wrapped DEK secret '{wrapped_dek_name}' for '{datastore_name}' in "
            + f"key vault '{dek_secret_store.entry.storeProviderUrl}'."
        )
        dek_secret_store.add_secret(
            wrapped_dek_name,
            lambda: generate_wrapped_dek(
                datastore_name, datastore_config_file, public_key, logger
            ),
        )

    # Publish the dataset specification.
    dataset_spec = DatasetSpecification(
        name=dataset_name,
        datasetSchema=datastore_entry.datasetSchema,
        datasetAccessPolicy=policy,
        datasetAccessPoint=dataset_accesspoint,
    )

    from .custom import (
        governance_user_document_create_cmd,
        governance_user_document_propose_cmd,
        governance_user_document_show_cmd,
        governance_user_document_vote_cmd,
    )

    dataset_approvers = [
        {"id": collaboration_context.collaborator_id, "type": "user"},
    ]
    governance_user_document_create_cmd(
        cmd,
        document_id=dataset_name,
        contract_id=contract_id,
        data=dataset_spec.model_dump_json(),
        labels=json.dumps({"type": "dataset"}),
        approvers=json.dumps(dataset_approvers),
        gov_client_name=collaboration_context.governance_client_name,
    )

    document = governance_user_document_show_cmd(
        cmd,
        document_id=dataset_name,
        gov_client_name=collaboration_context.governance_client_name,
    )
    proposal = governance_user_document_propose_cmd(
        cmd,
        document_id=dataset_name,
        version=document["version"],
        gov_client_name=collaboration_context.governance_client_name,
    )
    governance_user_document_vote_cmd(
        cmd,
        document_id=dataset_name,
        proposal_id=proposal["proposalId"],
        action="accept",
        gov_client_name=collaboration_context.governance_client_name,
    )


def collaboration_dataset_list_cmd(
    cmd,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """List datasets published to the collaboration.

    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    from .custom import governance_user_document_list_cmd

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )
    documents = governance_user_document_list_cmd(
        cmd,
        label_selector="type:dataset",
        gov_client_name=collaboration_context.governance_client_name,
    )

    return documents


def collaboration_spark_sql_application_publish_cmd(
    cmd,
    application_name: str,
    workload: str = "",
    contract_id: str = "",
    application_specification_file: str = "",
    application_query: str = "",
    application_input_dataset: str = "",
    application_output_dataset: str = "",
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
    prepare_only: bool = False,
):
    """Publish a Spark-SQL application to the collaboration.

    :param application_name: Unique name of the application to publish.
    :param workload: Workload for which the application is being published (mutually exclusive with --contract-id).
    :param contract_id: Contract for which the application is being published (mutually exclusive with --workload).
    :param application_specification_file: Path to the application specification file.
    :param application_query: Path to the query segment configuration file.
    :param application_input_dataset: Input dataset for the application.
    :param application_output_dataset: Output dataset for the application.
    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    from cleanroom_common.azure_cleanroom_core.models.dataset import Workload
    from cleanroom_common.azure_cleanroom_core.models.datastore import DataStoreEntry
    from cleanroom_common.azure_cleanroom_core.models.spark import (
        SparkApplicationSpecification,
        SparkMLApplication,
    )

    from .utilities._spark_helpers import (
        generate_spark_sql_application_specification_from_fields,
        load_spark_sql_application_specification_from_file,
    )

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )

    # Validate workload / contract.
    if not ((workload != "") ^ (contract_id != "")):
        raise CLIError(
            "One of workload or contract_id must be provided to publish a dataset."
        )

    if workload != "":
        try:
            application_workload = Workload(workload.strip().lower())
        except ValueError:
            valid_workloads = [workload.value for workload in Workload]
            raise CLIError(
                f"Invalid workload '{workload}'. Valid workloads are: {', '.join(valid_workloads)}"
            )

        # TODO: Fetch contract for this workload.
        contract_id = f"{application_workload}-42"

    # Generate the application specification.
    sqlApplication: SparkSQLApplication
    if application_specification_file != "":
        sqlApplication = load_spark_sql_application_specification_from_file(
            application_specification_file
        )
    else:
        if (
            application_query == ""
            or application_input_dataset == ""
            or application_output_dataset == ""
        ):
            raise CLIError(
                "Query JSON and datasets are required for inline specification."
            )
        sqlApplication = generate_spark_sql_application_specification_from_fields(
            application_query,
            application_input_dataset.split(","),
            application_output_dataset,
        )

    if prepare_only:
        logger.info(
            f"Prepared Spark-SQL application specification for '{application_name}' but did not publish "
            + "to collaboration as --prepare-only was specified."
        )

        return get_frontend_compatible_spark_sql_application(sqlApplication)

    # Encode the SQL query in base64.
    sqlApplication.query = base64.b64encode(sqlApplication.query.encode()).decode()

    spark_application: Union[SparkSQLApplication, SparkMLApplication] = sqlApplication

    application_specification = SparkApplicationSpecification(
        name=application_name,
        application=spark_application,
    )

    from .custom import (
        governance_user_document_create_cmd,
        governance_user_document_propose_cmd,
        governance_user_document_show_cmd,
        governance_user_document_vote_cmd,
    )

    # Fetch the owner for each dataset and add as approver.
    dataset_owners: dict[str, list[str]] = {}
    for dataset_map in sqlApplication.inputDataset + [sqlApplication.outputDataset]:
        dataset_document = governance_user_document_show_cmd(
            cmd,
            document_id=dataset_map.specification,
            gov_client_name=collaboration_context.governance_client_name,
        )
        dataset_owner = dataset_document["proposerId"]
        if dataset_owners.get(dataset_owner) is None:
            dataset_owners[dataset_owner] = [dataset_map.specification]
        else:
            dataset_owners[dataset_owner].append(dataset_map.specification)

    application_approvers = []
    for approver, assets in dataset_owners.items():
        application_approvers.append({"id": approver, "type": "user"})

    # Publish the spark application.
    governance_user_document_create_cmd(
        cmd,
        document_id=application_name,
        contract_id=contract_id,
        data=application_specification.model_dump_json(),
        labels=json.dumps({"type": "spark-application"}),
        approvers=json.dumps(application_approvers),
        gov_client_name=collaboration_context.governance_client_name,
    )

    application_document = governance_user_document_show_cmd(
        cmd,
        document_id=application_name,
        gov_client_name=collaboration_context.governance_client_name,
    )

    proposal = governance_user_document_propose_cmd(
        cmd,
        document_id=application_name,
        version=application_document["version"],
        gov_client_name=collaboration_context.governance_client_name,
    )

    # governance_user_document_vote_cmd(
    #     cmd,
    #     document_id=application_name,
    #     proposal_id=proposal["proposalId"],
    #     action="accept",
    #     gov_client_name=collaboration_context.governance_client_name,
    # )


def get_frontend_compatible_spark_sql_application(
    spark_sql_application: SparkSQLApplication,
) -> dict:
    inputDatasetsArray = []
    for dataset in spark_sql_application.inputDataset:
        inputDatasetsArray.append(f"{dataset.specification}:{dataset.view}")
    inputDatasets = ",".join(inputDatasetsArray)

    outputDataset = f"{spark_sql_application.outputDataset.specification}:{spark_sql_application.outputDataset.view}"

    queryDataArray = []

    queryJsonObject = json.loads(spark_sql_application.query)
    for querySegment in queryJsonObject["segments"]:
        executionSequence = querySegment["executionSequence"]
        data = querySegment["data"]
        preConditions = []
        for preCondition in querySegment.get("preConditions", []):
            preConditions.append(
                f"{preCondition['viewName']}:{preCondition['minRowCount']}"
            )
        preConditionsString = ",".join(preConditions)

        postFilters = []
        for postFilter in querySegment.get("postFilters", []):
            postFilters.append(f"{postFilter['columnName']}:{postFilter['value']}")
        postFiltersString = ",".join(postFilters)

        queryDataArray.append(
            {
                "executionSequence": executionSequence,
                "data": data,
                "preConditions": preConditionsString,
                "postFilters": postFiltersString,
            }
        )

    return {
        "inputDatasets": inputDatasets,
        "outputDataset": outputDataset,
        "queryData": queryDataArray,
    }


def collaboration_spark_sql_application_list_cmd(
    cmd,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """List Spark-SQL applications published to the collaboration.

    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    from .custom import governance_user_document_list_cmd

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )
    documents = governance_user_document_list_cmd(
        cmd,
        label_selector="type:spark-application",
        gov_client_name=collaboration_context.governance_client_name,
    )

    return documents


def collaboration_spark_sql_application_show_cmd(
    cmd,
    application_name: str,
    raw_output: bool = False,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Show details about a Spark-SQL application published to the collaboration.

    :param application_name: Unique name of the published application.
    :param raw_output: If true, returns the raw application specification and deployment information.
    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    from .custom import (
        governance_deployment_information_show_cmd,
        governance_user_document_show_cmd,
    )

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )
    application_document = governance_user_document_show_cmd(
        cmd,
        document_id=application_name,
        gov_client_name=collaboration_context.governance_client_name,
    )

    cleanroom_contract = application_document["contractId"]
    cleanroom_deployment_information = governance_deployment_information_show_cmd(
        cmd,
        contract_id=cleanroom_contract,
        gov_client_name=collaboration_context.governance_client_name,
    )

    from cleanroom_common.azure_cleanroom_core.models.spark import (
        SparkApplicationSpecification,
        SparkSQLApplication,
    )

    application_specification = SparkApplicationSpecification.model_validate_json(
        application_document["data"]
    )

    if raw_output is True:
        return {
            "specification": application_specification,
            "deployment": cleanroom_deployment_information["data"],
        }
    else:
        sql_query = (
            json.loads(
                base64.b64decode(application_specification.application.query).decode()
            )
            if isinstance(application_specification.application, SparkSQLApplication)
            else None
        )
        return {
            "name": application_specification.name,
            "type": application_specification.application.applicationType,
            "input": [
                {f"{ds.view}": ds.specification}
                for ds in application_specification.application.inputDataset
            ],
            "output": {
                f"{application_specification.application.outputDataset.view}": application_specification.application.outputDataset.specification,
            },
            "query": sql_query,
            "endpoint": cleanroom_deployment_information["data"]["url"],
        }


def collaboration_spark_sql_application_execute_cmd(
    cmd,
    application_name: str,
    application_parameters: str = "",
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Start a job to execute a Spark-SQL application published to the collaboration.

    :param application_name: Unique name of the published application.
    :param application_parameters: JSON string of any additional parameters to be passed for the run.
    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )
    application = collaboration_spark_sql_application_show_cmd(
        cmd,
        application_name=application_name,
        raw_output=True,
        collaboration_config_file=collaboration_config_file,
    )
    application_endpoint = application["deployment"]["url"]

    run_id = str(uuid.uuid4())[:8]
    correlation_id = str(uuid.uuid4())
    client_request_id = str(uuid.uuid4())
    logger.info(
        {
            "Application endpoint": application_endpoint,
            "Run ID": run_id,
            "Correlation ID": correlation_id,
            "Client Request ID": client_request_id,
        }
    )

    from .custom import governance_client_get_access_token_cmd, response_error_message

    token = governance_client_get_access_token_cmd(
        cmd, collaboration_context.governance_client_name
    )
    headers = {
        "content-type": "application/json",
        "x-ms-cleanroom-authorization": f"Bearer {token['accessToken']}",
        "x-ms-correlation-id": f"{correlation_id}",
        "x-ms-client-request-id": f"{client_request_id}",
    }
    body = {"runId": run_id}
    if application_parameters != "":
        json_params = shell_safe_json_parse(application_parameters)
        for k, v in json_params.items():
            body[k] = v
    logger.info(body)

    r = requests.post(
        f"{application_endpoint}/queries/{application_name}/run",
        json=body,
        headers=headers,
    )

    if r.status_code != 200:
        raise CLIError(response_error_message(r))

    job_details = r.json()
    job_details["x-ms-correlation-id"] = correlation_id
    job_details["x-ms-client-request-id"] = client_request_id
    logger.info(f"Started Spark-SQL application job. Job ID is '{job_details['id']}'")
    return job_details


def collaboration_spark_sql_application_get_execution_status_cmd(
    cmd,
    application_name: str,
    job_id: str,
    collaboration_config_file: str = CollaborationConfiguration.default_collaboration_config_file(),
):
    """Query status of a Spark-SQL application job.

    :param application_name: Unique name of the published application.
    :param job_id: ID of the job to query status for.
    :param collaboration_config_file: Path of the collaboration configuration file.
    """

    collaboration_context = (
        CollaborationConfiguration.get_default_collaboration_context(
            collaboration_config_file
        )
    )
    application = collaboration_spark_sql_application_show_cmd(
        cmd,
        application_name=application_name,
        raw_output=True,
        collaboration_config_file=collaboration_config_file,
    )
    application_endpoint = application["deployment"]["url"]

    correlation_id = str(uuid.uuid4())
    client_request_id = str(uuid.uuid4())
    logger.info(
        {
            "Application endpoint": application_endpoint,
            "Correlation ID": correlation_id,
            "Client Request ID": client_request_id,
        }
    )

    from .custom import governance_client_get_access_token_cmd, response_error_message

    token = governance_client_get_access_token_cmd(
        cmd, collaboration_context.governance_client_name
    )
    headers = {
        "x-ms-cleanroom-authorization": f"Bearer {token['accessToken']}",
        "x-ms-correlation-id": f"{correlation_id}",
        "x-ms-client-request-id": f"{client_request_id}",
    }
    r = requests.get(url=f"{application_endpoint}/status/{job_id}", headers=headers)
    if r.status_code != 200:
        raise CLIError(response_error_message(r))

    response_details = r.json()
    response_details["x-ms-correlation-id"] = correlation_id
    response_details["x-ms-client-request-id"] = client_request_id
    return response_details


def collaboration_spark_sql_application_querysegment_add_cmd(
    cmd,
    execution_sequence: int,
    query_content: str,
    config_file: str,
    pre_conditions: str = "",
    post_filters: str = "",
):
    """Adds a new query segment to the query segment store configuration file.

    :param name: The name of the query segment.
    :param execution_sequence: The execution sequence number of this segment. Should be >=1.
    :param query_content: The SQL query content.
    :param pre_conditions: Comma-separated pre-segment conditions in 'viewName:minRowCount' format.
    :param post_filters: Comma-separated post-segment filterings in 'columnName:value' format.
    :param config_file: Path to the query segment configuration file.
    """

    from azure.cli.core.util import CLIError

    try:
        executionsequence = int(execution_sequence)
    except ValueError:
        raise CLIError("Execution sequence must be a positive integer.")

    if executionsequence < 1:
        raise CLIError("Execution sequence must be a positive integer.")

    if not query_content:
        raise CLIError("Query content is required.")

    # Load existing query segment configuration or create a new one.
    querysegment_config = QuerySegmentHelper.load(
        config_file, create_if_not_existing=True
    )

    new_segment_entry = QuerySegmentHelper.generate_segment_from_fields(
        executionsequence,
        query_content,
        pre_conditions,
        post_filters,
    )

    # Check if a segment with the same execution sequence already exists
    for existing_segment in querysegment_config.segments:
        if existing_segment.data == new_segment_entry.data:
            raise CLIError(f"Query segment with the same data already exists.")

    # Add the new segment to the configuration.
    querysegment_config.segments.append(new_segment_entry)
    # Save the updated configuration back to the file.
    QuerySegmentHelper.store(config_file, querysegment_config)
    logger.warning(f"Added query segment to configuration file '{config_file}'.")


def collaboration_spark_sql_application_querysegment_list_cmd(
    cmd,
    config_file: str,
):
    # Load existing query segment configuration.
    querysegment_config = QuerySegmentHelper.load(
        config_file, create_if_not_existing=False
    )
    if not querysegment_config.segments:
        logger.warning("No query segments found in the configuration.")
        return

    for segment in querysegment_config.segments:
        print(
            f"\nExecution-Sequence: {segment.executionSequence}"
            + f",\nQueryData: {segment.data},\nPre-Conditions: {segment.preConditions},"
            + f"\nPost-Filterings: {segment.postFilters}"
        )
