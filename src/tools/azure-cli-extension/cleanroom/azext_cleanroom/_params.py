# pylint: disable=line-too-long
# pylint: disable=too-many-statements
# pylint: disable=anomalous-backslash-in-string
# pylint: disable=missing-module-docstring
# pylint: disable=missing-function-docstring

from typing import Required
from knack.arguments import CLIArgumentType
from azure.cli.core.commands.parameters import (
    resource_group_name_type,
    name_type,
    get_enum_type,
)
from .models.datastore import DatastoreEntry


def validate_mount(string):
    """Extracts a single tag in key[=value] format"""
    result = {}
    if string:
        comps = string.split(",", 1)
        result = {comps[0]: comps[1]} if len(comps) > 1 else {string: ""}
    return result


def validate_mounts(ns):
    """Extracts multiple space-separated tags in key[=value] format"""
    if isinstance(ns.mounts, list):
        mounts_dict = {}
        for item in ns.mounts:
            mounts_dict.update(validate_mount(item))
        ns.mounts = mounts_dict


def validate_env(string):
    """Extracts a single tag in key[=value] format"""
    result = {}
    if string:
        comps = string.split("=", 1)
        result = {comps[0]: comps[1]} if len(comps) > 1 else {string: ""}
    return result


def validate_envs(ns):
    """Extracts multiple space-separated tags in key[=value] format"""
    if isinstance(ns.env_vars, list):
        mounts_dict = {}
        for item in ns.env_vars:
            mounts_dict.update(validate_env(item))
        ns.env_vars = mounts_dict


default_security_policy_creation_option = "cached"


def load_arguments(self, _):
    cleanroom_name_type = CLIArgumentType(
        options_list=["--name", "-n"], help="Name of the clean room", id_part=None
    )

    with self.argument_context("cleanroom") as c:
        c.argument("location")
        c.argument("resource_group", resource_group_name_type)
        c.argument("name", cleanroom_name_type)

    # samples
    with self.argument_context("cleanroom kv show-1") as c:
        c.argument("resource_group_name", resource_group_name_type)
        c.argument("resource_name", name_type)

    with self.argument_context("cleanroom kv show-2") as c:
        c.argument("resource_group_name", resource_group_name_type)
        c.argument("resource_name", name_type)

    # governance client
    with self.argument_context("cleanroom governance client") as c:
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use",
            options_list=["--name"],
        )

    with self.argument_context("cleanroom governance client deploy") as c:
        c.argument(
            "ccf_endpoint", help="CCF endpoint", options_list=["--ccf-endpoint", "-e"]
        )
        c.argument(
            "signing_cert",
            help="Path to the PEM-encoded signing cert",
            options_list=["--signing-cert"],
        )
        c.argument(
            "signing_key",
            help="Path to the PEM-encoded signing key",
            options_list=["--signing-key"],
        )
        c.argument(
            "service_cert",
            help="Path to the PEM-encoded service cert",
            options_list=["--service-cert", "-s"],
        )

    # governance contract
    with self.argument_context("cleanroom governance service") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance service deploy") as c:
        c.argument("ccf_endpoint", help="CCF endpoint", options_list=["--ccf-endpoint"])
        c.argument(
            "signing_cert",
            help="Path to the PEM-encoded signing cert",
            options_list=["--signing-cert"],
        )
        c.argument(
            "signing_key",
            help="Path to the PEM-encoded signing key",
            options_list=["--signing-key"],
        )
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use that will be deployed for this service",
            options_list=["--governance-client"],
        )
        c.argument(
            "service_cert",
            help="Path to the PEM-encoded service cert",
            options_list=["--service-cert"],
        )

    with self.argument_context(
        "cleanroom governance service upgrade-constitution"
    ) as c:
        c.argument(
            "constitution_version",
            help="The version of the CGS constitution to deploy",
            options_list=["--constitution-version"],
        )
        c.argument(
            "constitution_url",
            help="The explict url (repo:tag) to download the version of the CGS constitution to deploy",
            options_list=["--constitution-url"],
        )
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use that will be deployed for this service",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance service upgrade-js-app") as c:
        c.argument(
            "js_app_version",
            help="The version of the CGS js app to deploy",
            options_list=["--js-app-version"],
        )
        c.argument(
            "js_app_url",
            help="The explict url (repo:tag) to download the version of the CGS js app to deploy",
            options_list=["--js-app-url"],
        )
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use that will be deployed for this service",
            options_list=["--governance-client"],
        )

    # governance contract
    with self.argument_context("cleanroom governance contract") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )
        c.argument(
            "contract_id",
            help="Contract Id",
            options_list=["--id"],
        )

    with self.argument_context("cleanroom governance contract create") as c:
        c.argument(
            "data",
            help="Contract data",
            options_list=["--data"],
        )
        c.argument(
            "version",
            help="Contract version if updating an exsiting contract",
            options_list=["--version"],
        )

    with self.argument_context("cleanroom governance contract propose") as c:
        c.argument(
            "version",
            help="Contract version being proposed",
            options_list=["--version"],
        )

    with self.argument_context("cleanroom governance contract vote") as c:
        c.argument(
            "proposal_id",
            help="Proposal Id to vote on",
            options_list=["--proposal-id"],
        )
        c.argument(
            "action",
            arg_type=get_enum_type(["accept", "reject"]),
            help="Whether to accept or reject the proposal",
            options_list=["--action"],
        )

    with self.argument_context("cleanroom governance proposal") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance proposal show") as c:
        c.argument(
            "proposal_id",
            help="The proposal Id",
            options_list=["--proposal-id"],
        )

    with self.argument_context("cleanroom governance proposal show-actions") as c:
        c.argument(
            "proposal_id",
            help="The proposal Id",
            options_list=["--proposal-id"],
        )

    with self.argument_context("cleanroom governance proposal vote") as c:
        c.argument(
            "proposal_id",
            help="Proposal Id to vote on",
            options_list=["--proposal-id"],
        )
        c.argument(
            "action",
            arg_type=get_enum_type(["accept", "reject"]),
            help="Whether to accept or reject the proposal",
            options_list=["--action"],
        )

    with self.argument_context("cleanroom governance proposal withdraw") as c:
        c.argument(
            "proposal_id",
            help="Proposal Id to withdraw",
            options_list=["--proposal-id"],
        )

    with self.argument_context("cleanroom governance ca") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )
        c.argument(
            "contract_id",
            help="The contract Id of the contract",
            options_list=["--contract-id"],
        )

    with self.argument_context("cleanroom governance deployment") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )
        c.argument(
            "contract_id",
            help="The contract Id of the deployment",
            options_list=["--contract-id"],
        )

    with self.argument_context("cleanroom governance deployment generate") as c:
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "generate", "generate-debug", "allow-all"]
            ),
            help="Whether to use the cached policy files or generate the security policy or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default="cached",
        )

    with self.argument_context("cleanroom governance deployment template propose") as c:
        c.argument(
            "template_file",
            help="The path to the template file",
            options_list=["--template-file"],
        )

    with self.argument_context("cleanroom governance deployment policy propose") as c:
        c.argument(
            "allow_all",
            action="store_true",
            help="Whether to use the allow all policy (insecure)",
        )
        c.argument(
            "policy_file",
            help="The path to the policy file",
            options_list=["--policy-file"],
        )

    with self.argument_context("cleanroom governance oidc-issuer") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )

    with self.argument_context(
        "cleanroom governance oidc-issuer propose-set-issuer-url"
    ) as c:
        c.argument(
            "url",
            help="The issuer url",
            options_list=["--url"],
        )

    with self.argument_context("cleanroom governance oidc-issuer set-issuer-url") as c:
        c.argument(
            "url",
            help="The issuer url",
            options_list=["--url"],
        )

    with self.argument_context("cleanroom governance contract runtime-option") as c:
        c.argument(
            "contract_id",
            help="The contract Id",
            options_list=["--contract-id"],
        )

    with self.argument_context("cleanroom governance contract runtime-option get") as c:
        c.argument(
            "option_name",
            arg_type=get_enum_type(["execution", "logging", "telemetry"]),
            help="The option name",
            options_list=["--option"],
        )

    with self.argument_context("cleanroom governance contract runtime-option set") as c:
        c.argument(
            "option_name",
            arg_type=get_enum_type(["execution"]),
            help="The option name",
            options_list=["--option"],
        )
        c.argument(
            "action",
            arg_type=get_enum_type(["enable", "disable"]),
            help="The action",
            options_list=["--action"],
        )

    with self.argument_context(
        "cleanroom governance contract runtime-option propose"
    ) as c:
        c.argument(
            "option_name",
            arg_type=get_enum_type(["logging", "telemetry"]),
            help="The option name",
            options_list=["--option"],
        )
        c.argument(
            "action",
            arg_type=get_enum_type(["enable", "disable"]),
            help="The action",
            options_list=["--action"],
        )

    with self.argument_context("cleanroom governance contract secret set") as c:
        c.argument(
            "contract_id",
            help="The contract Id",
            options_list=["--contract-id"],
        )
        c.argument(
            "secret_name",
            help="The secret name",
            options_list=["--secret-name"],
        )
        c.argument(
            "value",
            help="The secret value",
            options_list=["--value"],
        )

    with self.argument_context("cleanroom governance contract event list") as c:
        c.argument(
            "contract_id",
            help="The contract Id",
            options_list=["--contract-id"],
        )
        c.argument(
            "all",
            action="store_true",
            help="list all events from the start",
        )
        c.argument(
            "event_id",
            help="A particular event id to fetch events for under the contract",
            options_list=["--event-id"],
        )
        c.argument(
            "scope",
            help="The scope for the events to fetch",
            options_list=["--scope"],
        )

    with self.argument_context("cleanroom governance document") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )
        c.argument(
            "document_id",
            help="Document Id",
            options_list=["--id"],
        )

    with self.argument_context("cleanroom governance document create") as c:
        c.argument(
            "contract_id",
            help="Contract Id",
            options_list=["--contract-id"],
        )
        c.argument(
            "data",
            help="Document data",
            options_list=["--data"],
        )
        c.argument(
            "version",
            help="Document version if updating an exsiting document",
            options_list=["--version"],
        )

    with self.argument_context("cleanroom governance document propose") as c:
        c.argument(
            "version",
            help="Document version being proposed",
            options_list=["--version"],
        )

    with self.argument_context("cleanroom governance document vote") as c:
        c.argument(
            "proposal_id",
            help="Proposal Id to vote on",
            options_list=["--proposal-id"],
        )
        c.argument(
            "action",
            arg_type=get_enum_type(["accept", "reject"]),
            help="Whether to accept or reject the proposal",
            options_list=["--action"],
        )

    with self.argument_context("cleanroom governance member") as c:
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance member add") as c:
        c.argument(
            "identifier",
            help="A unique identifier for the member",
            options_list=["--identifier"],
            required=False,
        )
        c.argument(
            "certificate",
            help="Path to the PEM certificate file",
            options_list=["--certificate"],
        )
        c.argument(
            "member_data",
            help="Member data as JSON, or a path to a file containing a JSON description",
            options_list=["--member-data"],
            required=False,
        )
        c.argument(
            "tenant_id",
            help="Tenant ID to set in the member data",
            options_list=["--tenant-id"],
            required=False,
        )
        c.argument(
            "encryption_public_key",
            help="Encryption public key for the member",
            options_list=["--encryption-public-key"],
            required=False,
        )

    with self.argument_context("cleanroom config") as c:
        c.argument(
            "cleanroom_config_file",
            help="The configuration file",
            options_list=["--cleanroom-config"],
        )
    with self.argument_context("cleanroom config init") as c:
        pass

    with self.argument_context("cleanroom config view") as c:
        c.argument(
            "configs",
            help="The configuration file(s) to merge",
            options_list=["--configs"],
            nargs="*",
            default=[],
            required=False,
        )
        c.argument(
            "no_print",
            action="store_true",
            help="Whether to not print the configuration but return it as a json string",
            required=False,
        )
        c.argument(
            "output_file",
            help="Output file to dump the combined config",
            options_list=["--out-file", "--output-file"],
        )
    with self.argument_context("cleanroom config add-application") as c:
        c.argument(
            "name",
            help="The application name.",
            options_list=["--name"],
        )
        c.argument(
            "image",
            help="The image to use for the application.",
            options_list=["--image"],
        )
        c.argument(
            "command_line",
            help="The command to run.",
            options_list=["--command-line"],
        )
        c.argument(
            "mounts",
            help="The mount points to expose.",
            options_list=["--mounts"],
            validator=validate_mounts,
            nargs="*",
        )
        c.argument(
            "env_vars",
            help="The environment variables to expose.",
            options_list=["--env-vars"],
            validator=validate_envs,
            nargs="*",
        )
        c.argument(
            "cpu",
            help="The required number of CPU cores of the container, accurate to one decimal place.",
            options_list=["--cpu"],
        )
        c.argument(
            "memory",
            help="The required memory of the containers in GB, accurate to one decimal place.",
            options_list=["--memory"],
        )
        c.argument(
            "acr_access_identity",
            help="The identity used to access the Azure Container Registry to pull the container image. This identity must have the 'AcrPull' role on the container registry.",
            options_list=["--acr-access-identity"],
        )

    with self.argument_context("cleanroom config add-application-endpoint") as c:
        c.argument(
            "application_name",
            help="Application Name",
            options_list=["--application-name"],
        )
        c.argument("port", help="The port", options_list=["--port"], type=int)
        c.argument(
            "policy_bundle_url",
            help="The policy bundle URL",
            options_list=["--policy-bundle-url", "--policy"],
            default="",
        )
    with self.argument_context("cleanroom config create-kek") as c:
        c.argument(
            "contract_id",
            help="Contract Id",
            options_list=["--contract-id"],
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )
        c.argument(
            "b64_cl_policy",
            help="The base64 encoded clean room policy for the contract",
            options_list=["--cleanroom-policy"],
        )

    with self.argument_context("cleanroom config wrap-deks") as c:
        c.argument(
            "contract_id",
            help="Contract Id",
            options_list=["--contract-id"],
        )
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )

    with self.argument_context("cleanroom config wrap-secret") as c:
        c.argument(
            "contract_id",
            help="Contract Id",
            options_list=["--contract-id"],
        )
        c.argument(
            "name",
            help="The name of the secret in Key Vault",
            options_list=["--name"],
        )
        c.argument(
            "value",
            help="The secret value to wrap",
            options_list=["--value"],
        )
        c.argument(
            "secret_key_vault",
            help="The key vault to use to store the wrapped secret.",
            options_list=["--secret-key-vault"],
        )
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )

    with self.argument_context("cleanroom config add-datasource") as c:
        c.argument(
            "datastore_name",
            help="The name of the backing datastore.",
        )
        c.argument(
            "datastore_config_file",
            help="The configuration file storing information about the datastore.",
            options_list=["--datastore-config"],
        )
        c.argument(
            "identity",
            help="The identity to use for accessing the datastore.",
        )
        c.argument(
            "access_name",
            help="The name of the datasource within the clean room, defaults to datastore name.",
            options_list=["--name", "--access-name"],
            required=False,
            default="",
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )

    with self.argument_context("cleanroom config add-datasink") as c:
        c.argument(
            "datastore_name",
            help="The name of the backing datastore",
        )
        c.argument(
            "datastore_config_file",
            help="The configuration file storing information about the datastore.",
            options_list=["--datastore-config"],
        )
        c.argument(
            "identity",
            help="The identity to use for accessing the datastore.",
        )
        c.argument(
            "wrapped_dek_key_vault",
            help="The key vault to use to store the DEK.",
            options_list=["--key-vault"],
        )
        c.argument(
            "access_name",
            help="The name of the datasink within the clean room, defaults to datastore name.",
            options_list=["--name", "--access-name"],
            required=False,
            default="",
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )

    with self.argument_context("cleanroom config set-telemetry") as c:
        c.argument(
            "datastore_config_file",
            help="The configuration file storing information about the datastore.",
            options_list=["--datastore-config"],
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )
        c.argument(
            "storage_account",
            help="The storage account name",
        )
        c.argument(
            "identity",
            help="The identity to use for the datastore",
        )
        c.argument(
            "wrapped_dek_key_vault",
            help="The key vault to use to store the DEK.",
            options_list=["--key-vault"],
        )
        c.argument(
            "container_name",
            help="The container name to create in Azure storage account",
            required=False,
            default="",
        )

    with self.argument_context("cleanroom config set-logging") as c:
        c.argument(
            "datastore_config_file",
            help="The configuration file storing information about the datastore.",
            options_list=["--datastore-config"],
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config"],
        )
        c.argument(
            "storage_account",
            help="The storage account name",
        )
        c.argument(
            "identity",
            help="The identity to use for the datastore",
        )
        c.argument(
            "wrapped_dek_key_vault",
            help="The key vault to use to store the DEK.",
            options_list=["--key-vault"],
        )
        c.argument(
            "container_name",
            help="The container name to create in Azure storage account",
            required=False,
            default="",
        )

    with self.argument_context("cleanroom config add-identity") as c:
        c.argument("name", help="The name of the identity")
        c.argument("client_id", required=False)
        c.argument("tenant_id", required=False)

    with self.argument_context("cleanroom config add-identity az-federated") as c:
        c.argument("backing_identity", default="cleanroom_cgs_oidc")

    with self.argument_context("cleanroom config add-identity oidc-attested") as c:
        c.argument("issuer_url", default="https://cgs/oidc")

    with self.argument_context("cleanroom config add-identity az-secret") as c:
        c.argument("secret_name")
        c.argument("secret_store_url")
        c.argument(
            "backing_identity",
            type=str,
        )

    for scope in ["telemetry", "logs"]:
        with self.argument_context(f"cleanroom {scope} download") as c:
            c.argument(
                "cleanroom_config",
                help="The configuration file.",
                options_list=["--cleanroom-config"],
            )
            c.argument(
                "target_folder",
                help="The folder to which the data needs to be downloaded.",
                options_list=["--target-folder"],
            )

    for scope in ["telemetry", "logs"]:
        with self.argument_context(f"cleanroom {scope} decrypt") as c:
            c.argument(
                "cleanroom_config",
                help="The configuration file.",
                options_list=["--cleanroom-config"],
            )
            c.argument(
                "target_folder",
                help="The folder from which the data needs to be decrypted.",
                options_list=["--target-folder"],
            )

    with self.argument_context("cleanroom telemetry aspire-dashboard") as c:
        c.argument(
            "telemetry_folder",
            help="The location of the downloaded telemetry files.",
            options_list=["--telemetry-folder"],
        )

    # CCF provider
    with self.argument_context("cleanroom ccf provider deploy") as c:
        c.argument(
            "provider_client_name",
            help="Name to use for the provider client instance",
            options_list=["--name"],
            required=False,
            default="ccf-provider",
        )

    with self.argument_context("cleanroom ccf provider configure") as c:
        c.argument(
            "signing_cert",
            help="Path to the PEM-encoded operator signing cert",
            options_list=["--signing-cert"],
        )
        c.argument(
            "signing_key",
            help="Path to the PEM-encoded operator signing key",
            options_list=["--signing-key"],
        )
        c.argument(
            "provider_client_name",
            help="Name to use for the provider client instance",
            options_list=["--name"],
            required=False,
            default="ccf-provider",
        )

    with self.argument_context("cleanroom ccf provider remove") as c:
        c.argument(
            "provider_client_name",
            help="Name to use for the provider client instance",
            options_list=["--name"],
            required=False,
            default="ccf-provider",
        )

    with self.argument_context("cleanroom ccf provider show") as c:
        c.argument(
            "provider_client_name",
            help="Name to use for the provider client instance",
            options_list=["--name"],
            required=False,
            default="ccf-provider",
        )

    # CCF network
    with self.argument_context("cleanroom ccf network") as c:
        c.argument(
            "provider_client_name",
            help="Name of the client instance",
            options_list=["--provider-client"],
            required=False,
            default="ccf-provider",
        )
        c.argument(
            "infra_type",
            arg_type=get_enum_type(["caci", "virtual", "virtualaci"]),
            help="The platform used for hosting the CCF network",
            options_list=["--infra-type"],
            required=False,
            default="caci",
        )

    with self.argument_context("cleanroom ccf network up") as c:
        c.argument(
            "network_name",
            help="A unique name for the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "resource_group",
            help="A resource group under which to create the resources used by CCF",
            options_list=["--resource-group"],
        )
        c.argument(
            "ws_folder",
            help="An existing folder to use to place various configuration files that get created. If not specified then a folder gets automatically created under $HOME.",
            options_list=["--workspace-folder"],
            required=False,
        )
        c.argument(
            "location",
            help="The location to created Azure resources. Defaults to resource group's location if not specified.",
            options_list=["--location"],
            required=False,
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(["cached", "cached-debug", "allow-all"]),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )
        c.argument(
            "recovery_mode",
            arg_type=get_enum_type(["operator-recovery", "confidential-recovery"]),
            help="Whether to setup operator based recovery or confidential recovery service based recovery",
            options_list=["--recovery-mode"],
            required=False,
            default="operator-recovery",
        )

    with self.argument_context("cleanroom ccf network create") as c:
        c.argument(
            "network_name",
            help="A unique name for the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "members",
            help="Member details as JSON, or a path to a file containing a JSON description.",
            options_list=["--members"],
        )
        c.argument(
            "node_count",
            help="Number of nodes to create for the cluster. Node consensus requires odd number of nodes.",
            options_list=["--node-count"],
            required=False,
            default=1,
        )
        c.argument(
            "node_log_level",
            arg_type=get_enum_type(["Trace", "Debug", "Info", "Fail", "Fatal"]),
            help="A value as per https://microsoft.github.io/CCF/main/operations/configuration.html#logging",
            options_list=["--node-log-level"],
            required=False,
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )
        c.argument(
            "security_policy",
            help="Path to a file containing or a base64 encoded string itself that specifies the security policy to use instead of passing a --security-policy-creation-option value.",
            options_list=["--security-policy"],
            required=False,
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network delete") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "delete_option",
            arg_type=get_enum_type(["delete-storage", "retain-storage"]),
            help="Whether to delete the ledger/snapshots storage provisioned for the nodes or not.",
            options_list=["--delete-option"],
            required=False,
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network update") as c:
        c.argument(
            "network_name",
            help="A unique name for the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "node_count",
            help="Number of nodes to create for the cluster. Node consensus requires odd number of nodes. Select a number between 3 and 9.",
            options_list=["--node-count"],
        )
        c.argument(
            "node_log_level",
            arg_type=get_enum_type(["Trace", "Debug", "Info", "Fail", "Fatal"]),
            help="A value as per https://microsoft.github.io/CCF/main/operations/configuration.html#logging",
            options_list=["--node-log-level"],
            required=False,
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )
        c.argument(
            "security_policy",
            help="Path to a file containing or a base64 encoded string itself that specifies the security policy to use instead of passing a --security-policy-creation-option value.",
            options_list=["--security-policy"],
            required=False,
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network recover") as c:
        c.argument(
            "network_name",
            help="Name for the CCF network to recover",
            options_list=["--name"],
        )
        c.argument(
            "node_log_level",
            arg_type=get_enum_type(["Trace", "Debug", "Info", "Fail", "Fatal"]),
            help="A value as per https://microsoft.github.io/CCF/main/operations/configuration.html#logging",
            options_list=["--node-log-level"],
            required=False,
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )
        c.argument(
            "previous_service_cert",
            help="Path to the previous PEM-encoded service cert.",
            options_list=["--previous-service-cert"],
        )
        c.argument(
            "encryption_private_key",
            help="Path to the PEM-encoded private key",
            options_list=["--operator-recovery-encryption-private-key"],
            required=False,
        )
        c.argument(
            "recovery_service_name",
            help="The confidential recovery service to use.",
            options_list=["--confidential-recovery-service-name"],
            required=False,
        )
        c.argument(
            "member_name",
            help="A unique name for the confidential recovery member of the recovery service.",
            options_list=["--confidential-recovery-member-name"],
            required=False,
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network recover-public-network") as c:
        c.argument(
            "network_name",
            help="Name for the CCF network to recover",
            options_list=["--name"],
        )
        c.argument(
            "target_network_name",
            help="A unique name for the recovery CCF network to be created",
            options_list=["--target-network-name"],
            required=False,
        )
        c.argument(
            "node_count",
            help="Number of nodes to create for the cluster. Node consensus requires odd number of nodes. Select a number between 3 and 9.",
            options_list=["--node-count"],
        )
        c.argument(
            "node_log_level",
            arg_type=get_enum_type(["Trace", "Debug", "Info", "Fail", "Fatal"]),
            help="A value as per https://microsoft.github.io/CCF/main/operations/configuration.html#logging",
            options_list=["--node-log-level"],
            required=False,
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )
        c.argument(
            "security_policy",
            help="Path to a file containing or a base64 encoded string itself that specifies the security policy to use instead of passing a --security-policy-creation-option value.",
            options_list=["--security-policy"],
            required=False,
        )
        c.argument(
            "previous_service_cert",
            help="Path to the previous PEM-encoded service cert.",
            options_list=["--previous-service-cert"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network submit-recovery-share") as c:
        c.argument(
            "network_name",
            help="A unique name for the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "signing_cert",
            help="Path to the PEM-encoded signing cert",
            options_list=["--signing-cert"],
        )
        c.argument(
            "signing_key",
            help="Path to the PEM-encoded signing key",
            options_list=["--signing-key"],
        )
        c.argument(
            "encryption_private_key",
            help="Path to the PEM-encoded private key",
            options_list=["--encryption-private-key", "-s"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network show") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network join-policy generate") as c:
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(["cached", "cached-debug", "allow-all"]),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )

    with self.argument_context("cleanroom ccf network join-policy show") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context(
        "cleanroom ccf network join-policy add-snp-host-data"
    ) as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )
        c.argument(
            "host_data",
            help="The acceptable host data value for new nodes",
            options_list=["--host-data"],
        )
        c.argument(
            "security_policy",
            help="Optional path to the security policy value (rego) whose host data value was supplied.",
            options_list=["--security-policy"],
            required=False,
        )

    with self.argument_context(
        "cleanroom ccf network join-policy remove-snp-host-data"
    ) as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )
        c.argument(
            "host_data",
            help="The host data value to remove",
            options_list=["--host-data"],
        )

    with self.argument_context("cleanroom ccf network recovery-agent show") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network recovery-agent show-report") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    for scope in ["generate-member", "activate-member", "submit-recovery-share"]:
        with self.argument_context(
            f"cleanroom ccf network recovery-agent {scope}"
        ) as c:
            c.argument(
                "network_name",
                help="The name of the CCF network",
                options_list=["--network-name"],
            )
            c.argument(
                "provider_config",
                help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
                options_list=["--provider-config"],
                required=False,
            )
            c.argument(
                "agent_config",
                help="Recovery agent config details as JSON, or a path to a file containing a JSON description.",
                options_list=["--agent-config"],
            )
            c.argument(
                "member_name",
                help="A unique name for the recovery member.",
                options_list=["--member-name"],
            )

    with self.argument_context("cleanroom ccf network show-health") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network show-report") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network trigger-snapshot") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network transition-to-open") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "previous_service_cert",
            help="Path to the previous PEM-encoded service cert.",
            options_list=["--previous-service-cert"],
            required=False,
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network set-recovery-threshold") as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "recovery_threshold",
            help="Desired value.",
            options_list=["--recovery-threshold"],
            required=False,
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf network security-policy generate") as c:
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )

    with self.argument_context(
        "cleanroom ccf network security-policy generate-join-policy"
    ) as c:
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )

    with self.argument_context(
        "cleanroom ccf network security-policy generate-join-policy-from-network"
    ) as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context(
        "cleanroom ccf network configure-confidential-recovery"
    ) as c:
        c.argument(
            "network_name",
            help="The name of the CCF network",
            options_list=["--name"],
        )
        c.argument(
            "recovery_service_name",
            help="The confidential recovery service to use.",
            options_list=["--recovery-service-name"],
        )
        c.argument(
            "recovery_member_name",
            help="A unique name for the recovery member that will be created.",
            options_list=["--recovery-member-name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    # CCF recovery service
    with self.argument_context("cleanroom ccf recovery-service") as c:
        c.argument(
            "provider_client_name",
            help="Name of the client instance",
            options_list=["--provider-client"],
            required=False,
            default="ccf-provider",
        )
        c.argument(
            "infra_type",
            arg_type=get_enum_type(["caci", "virtual"]),
            help="The platform used for hosting the CCF recovery service",
            options_list=["--infra-type"],
            required=False,
            default="caci",
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom ccf recovery-service create") as c:
        c.argument(
            "service_name",
            help="A unique name for the CCF recovery service",
            options_list=["--name"],
        )
        c.argument(
            "key_vault",
            help="The key vault to use to store the recovery keys.",
            options_list=["--key-vault"],
        )
        c.argument(
            "maa_endpoint",
            help="The MAA endpoint.",
            options_list=["--maa-endpoint"],
        )
        c.argument(
            "identity",
            help="The identity to use to access the key vault",
            options_list=["--identity"],
            required=False,
        )
        c.argument(
            "ccf_network_join_policy",
            help="Path to a file containing a JSON that is the CCF network join policy document",
            options_list=["--ccf-network-join-policy"],
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_security_policy_creation_option,
        )
        c.argument(
            "security_policy",
            help="Path to a file containing or a base64 encoded string itself that specifies the security policy to use instead of passing a --security-policy-creation-option value.",
            options_list=["--security-policy"],
            required=False,
        )

    with self.argument_context("cleanroom ccf recovery-service delete") as c:
        c.argument(
            "service_name",
            help="The name of the CCF recovery service",
            options_list=["--name"],
        )

    with self.argument_context("cleanroom ccf recovery-service show") as c:
        c.argument(
            "service_name",
            help="The name of the CCF recovery service",
            options_list=["--name"],
        )

    with self.argument_context(
        "cleanroom ccf recovery-service security-policy generate"
    ) as c:
        c.argument(
            "ccf_network_join_policy",
            help="Path to a file containing a JSON that is the CCF network join policy document",
            options_list=["--ccf-network-join-policy"],
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
        )

    with self.argument_context("cleanroom ccf recovery-service api") as c:
        c.argument(
            "service_config",
            help="Recovery service config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--service-config"],
        )

    with self.argument_context("cleanroom ccf recovery-service api member show") as c:
        c.argument(
            "member_name",
            help="Any specifc member to show information for",
            options_list=["--member-name"],
            required=False,
        )

    with self.argument_context(
        "cleanroom ccf recovery-service api member show-report"
    ) as c:
        c.argument(
            "member_name",
            help="Any specifc member to show information for",
            options_list=["--member-name"],
            required=True,
        )

    with self.argument_context("cleanroom datastore") as c:
        c.argument(
            "datastore_name",
            help="The name of the datastore.",
            options_list=["--datastore-name", "--name"],
        )
        c.argument(
            "datastore_config_file",
            help="The configuration file storing information about the datastore.",
            options_list=["--datastore-config", "--config"],
        )
    with self.argument_context("cleanroom datastore add") as c:
        c.argument(
            "storage_account",
            help="The Azure Storage account backing the datastore.",
            options_list=["--storage-account", "--sa"],
        )
        c.argument(
            "container_name",
            help="The Azure Storage blob container backing the datastore.",
            options_list=["--container-name", "--container"],
        )
        c.argument(
            "encryption_mode",
            arg_type=get_enum_type(DatastoreEntry.EncryptionMode),
        )
        c.argument(
            "secretstore_config_file",
            help="The config file of the secret store",
            options_list=["--secretstore-config-file", "--secretstore-config"],
        )
        c.argument(
            "datastore_secret_store",
            help="The name of the secret store to use for the datastore",
            options_list=["--secretstore", "--datastore-secretstore"],
        )
        c.argument(
            "backingstore_type",
            arg_type=get_enum_type(DatastoreEntry.StoreType),
        )
    with self.argument_context("cleanroom datastore upload") as c:
        c.argument(
            "source_path",
            help="The local path from which data should be encrypted and uploaded.",
            options_list=["--source-path", "--src"],
        )
    with self.argument_context("cleanroom datastore download") as c:
        c.argument(
            "destination_path",
            help="The local path to which decrypted data should be downloaded.",
            options_list=["--destination-path", "--dst"],
        )

    with self.argument_context("cleanroom secretstore") as c:
        c.argument(
            "secretstore_name",
            help="The name of the secret store.",
            options_list=["--secretstore-name", "--name"],
        )
        c.argument(
            "secretstore_config_file",
            help="The configuration file storing information about the secret store.",
            options_list=["--secretstore-config", "--config"],
        )
