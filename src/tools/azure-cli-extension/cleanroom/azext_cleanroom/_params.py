# pylint: disable=line-too-long
# pylint: disable=too-many-statements
# pylint: disable=anomalous-backslash-in-string
# pylint: disable=missing-module-docstring
# pylint: disable=missing-function-docstring

from azure.cli.core.commands.parameters import (
    get_enum_type,
    name_type,
    resource_group_name_type,
)
from cleanroom_common.azure_cleanroom_core.models.datastore import DataStoreEntry
from cleanroom_common.azure_cleanroom_core.models.network import TrafficDirection
from knack.arguments import CLIArgumentType


def validate_key_value(string, separator="="):
    """Extracts a single tag in key[=value] format"""
    result = {}
    if string:
        comps = string.split(separator, 1)
        result = {comps[0]: comps[1]} if len(comps) > 1 else {string: ""}
    return result


def validate_env_vars(ns):
    """Extracts multiple space-separated tags in key[=value] format"""
    if isinstance(ns.env_vars, list):
        env_vars_dict = {}
        for item in ns.env_vars:
            env_vars_dict.update(validate_key_value(item))
        ns.env_vars = env_vars_dict


def validate_ips(ns):
    """Extracts multiple space-separated tags in key[=value] format"""
    if isinstance(ns.allowed_ips, list):
        allowed_ips = {}
        for item in ns.allowed_ips:
            allowed_ips.update(validate_key_value(item, separator=":"))
        ns.allowed_ips = allowed_ips


def validate_datasources(ns):
    """Extracts multiple space-separated tags in key[=value] format"""
    if isinstance(ns.datasources, list):
        datasources_dict = {}
        for item in ns.datasources:
            datasources_dict.update(validate_key_value(item))
        ns.datasources = datasources_dict


def validate_datasinks(ns):
    """Extracts multiple space-separated tags in key[=value] format"""
    if isinstance(ns.datasinks, list):
        datasinks_dict = {}
        for item in ns.datasinks:
            datasinks_dict.update(validate_key_value(item))
        ns.datasinks = datasinks_dict


default_security_policy_creation_option = "cached"

# TODO (gsinha): Changed to "cached" once policy generation is stable and available on mcr.
default_workloads_security_policy_creation_option = "allow-all"


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
            "signing_cert_id",
            help="URI for the signing certificate stored in Azure Key Vault or a path to a file containing the URI",
            options_list=["--signing-cert-id"],
            required=False,
        )
        c.argument(
            "signing_cert",
            help="Path to the PEM-encoded signing cert",
            options_list=["--signing-cert"],
            required=False,
        )
        c.argument(
            "signing_key",
            help="Path to the PEM-encoded signing key",
            options_list=["--signing-key"],
            required=False,
        )
        c.argument(
            "service_cert",
            help="Path to the PEM-encoded service cert",
            options_list=["--service-cert", "-s"],
            required=False,
        )
        c.argument(
            "service_cert_discovery_endpoint",
            help="Endpoint to obtain the CCF service cert and SNP attestation report for the same",
            options_list=["--service-cert-discovery-endpoint"],
            required=False,
        )
        c.argument(
            "service_cert_discovery_snp_host_data",
            help="Expected SNP attestation report host data value to verify for the report fetched via the service cert discovery endpoint",
            options_list=["--service-cert-discovery-snp-host-data"],
            required=False,
        )
        c.argument(
            "service_cert_discovery_constitution_digest",
            help="Expected constitution digest value to verify for the report fetched via the service cert discovery endpoint. If no value is specified then the digest check is skipped.",
            options_list=["--service-cert-discovery-constitution-digest"],
            required=False,
        )
        c.argument(
            "service_cert_discovery_jsapp_bundle_digest",
            help="Expected jsapp bundle digest value to verify for the report fetched via the service cert discovery endpoint. If no value is specified then the digest check is skipped.",
            options_list=["--service-cert-discovery-jsapp-bundle-digest"],
            required=False,
        )
        c.argument(
            "use_azlogin_identity",
            help="Whether to use the az login identity to request the access token for user JWT authentication to the CCF network (https://microsoft.github.io/CCF/main/build_apps/auth/jwt.html)",
            options_list=["--use-azlogin-identity"],
            action="store_true",
            required=False,
        )
        c.argument(
            "use_microsoft_identity",
            help="Whether to use Microsoft Login identity to request the access token for user JWT authentication to the CCF network (https://microsoft.github.io/CCF/main/build_apps/auth/jwt.html)",
            options_list=["--use-microsoft-identity"],
            action="store_true",
            required=False,
        )
        c.argument(
            "use_local_identity",
            help="Whether to use Local Identity Provider to request the access token for user JWT authentication to the CCF network (https://microsoft.github.io/CCF/main/build_apps/auth/jwt.html)",
            options_list=["--use-local-identity"],
            action="store_true",
            required=False,
        )
        c.argument(
            "local_identity_endpoint",
            help="The IDP endpoint. Must be used in conjunction with --use-local-idp.",
            options_list=["--local-identity-endpoint"],
            required=False,
        )

    # governance contract
    with self.argument_context("cleanroom governance service") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance service deploy") as c:
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use",
            options_list=["--governance-client"],
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

    with self.argument_context("cleanroom governance user-identity") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance user-identity add") as c:
        c.argument(
            "object_id",
            help="The object ID of the user/service principal to be added",
            options_list=["--object-id"],
            required=False,
        )
        c.argument(
            "accepted_invitation_id",
            help="The invitation ID for an accepted invitation for which the user/service principal is to be added",
            options_list=["--accepted-invitation-id"],
            required=False,
        )
        c.argument(
            "identifier",
            help="A unique identifier for the user (for easy identification)",
            options_list=["--identifier"],
            required=False,
        )
        c.argument(
            "tenant_id",
            help="The tenant ID for the object ID of the user/service principal to be added",
            options_list=["--tenant-id"],
            required=False,
        )
        c.argument(
            "account_type",
            arg_type=get_enum_type(["microsoft"]),
            help="The type of account associated with the object ID",
            options_list=["--account-type"],
            required=False,
        )

    with self.argument_context("cleanroom governance user-identity remove") as c:
        c.argument(
            "object_id",
            help="The object ID of the user/service principal in Microsoft Entra ID to be removed",
            options_list=["--object-id"],
        )

    with self.argument_context(
        "cleanroom governance user-identity invitation create"
    ) as c:
        c.argument(
            "invitation_id",
            help="The id to use for this invitation. Generated randomly if not provided.",
            options_list=["--invitation-id"],
            required=False,
        )
        c.argument(
            "username",
            help="The email address for a user of application Id for a service principal being invited",
            options_list=["--username", "-u"],
        )
        c.argument(
            "tenant_id",
            help="The tenant id for the service principal being invited",
            options_list=["--tenant-id"],
            required=False,
        )
        c.argument(
            "account_type",
            arg_type=get_enum_type(["microsoft"]),
            help="The type of account associated with the email",
            options_list=["--account-type"],
            required=False,
            default="microsoft",
        )
        c.argument(
            "identity_type",
            arg_type=get_enum_type(["user", "service-principal"]),
            help="To indicate the type of identity being invited",
            options_list=["--identity-type"],
            default="user",
        )

    with self.argument_context(
        "cleanroom governance user-identity invitation accept"
    ) as c:
        c.argument(
            "invitation_id",
            help="The id of the invitation being accepted",
            options_list=["--invitation-id"],
        )

    with self.argument_context("cleanroom governance proposal") as c:
        c.argument(
            "gov_client_name",
            help="Name of the client instance",
            options_list=["--governance-client"],
        )

    with self.argument_context("cleanroom governance proposal create") as c:
        c.argument(
            "content",
            help="The proposal content",
            options_list=["--content"],
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

    with self.argument_context("cleanroom governance member-document") as c:
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

    with self.argument_context("cleanroom governance member-document create") as c:
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

    with self.argument_context("cleanroom governance member-document propose") as c:
        c.argument(
            "version",
            help="Document version being proposed",
            options_list=["--version"],
        )

    with self.argument_context("cleanroom governance member-document vote") as c:
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

    with self.argument_context("cleanroom governance user-document") as c:
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

    with self.argument_context("cleanroom governance user-document create") as c:
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
        c.argument(
            "approvers",
            help="Approver list as JSON, or a path to a file containing a JSON description.",
            options_list=["--approvers"],
            required=False,
        )

    with self.argument_context("cleanroom governance user-document propose") as c:
        c.argument(
            "version",
            help="Document version being proposed",
            options_list=["--version"],
        )

    with self.argument_context("cleanroom governance user-document vote") as c:
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

    with self.argument_context(
        "cleanroom governance user-document runtime-option"
    ) as c:
        c.argument(
            "document_id",
            help="The document Id",
            options_list=["--document-id"],
        )

    with self.argument_context(
        "cleanroom governance user-document runtime-option get"
    ) as c:
        c.argument(
            "option_name",
            arg_type=get_enum_type(["execution", "telemetry"]),
            help="The option name",
            options_list=["--option"],
        )

    with self.argument_context(
        "cleanroom governance user-document runtime-option set"
    ) as c:
        c.argument(
            "option_name",
            arg_type=get_enum_type(["execution", "telemetry"]),
            help="The option name",
            options_list=["--option"],
        )
        c.argument(
            "action",
            arg_type=get_enum_type(["enable", "disable"]),
            help="The action",
            options_list=["--action"],
        )

    with self.argument_context("cleanroom governance network") as c:
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use",
            options_list=["--governance-client"],
        )

    with self.argument_context(
        "cleanroom governance network set-recovery-threshold"
    ) as c:
        c.argument(
            "recovery_threshold",
            help="The desired value",
            options_list=["--recovery-threshold"],
        )

    with self.argument_context("cleanroom governance member") as c:
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use",
            options_list=["--governance-client"],
        )

    with self.argument_context(
        "cleanroom governance member get-default-certificate-policy"
    ) as c:
        c.argument(
            "member_name",
            help="A unique name to use for the member",
            options_list=["--member-name"],
        )

    with self.argument_context(
        "cleanroom governance member generate-identity-certificate"
    ) as c:
        c.argument(
            "member_name",
            help="A unique name to use for the member",
            options_list=["--member-name"],
        )
        c.argument(
            "vault_name",
            help="The Azure Key Vault to create the certificate in",
            options_list=["--vault-name"],
        )
        c.argument(
            "output_dir",
            help="The output directory where files will get created",
            options_list=["--output-dir"],
        )

    with self.argument_context(
        "cleanroom governance member generate-encryption-key"
    ) as c:
        c.argument(
            "member_name",
            help="A unique name to use for the member",
            options_list=["--member-name"],
        )
        c.argument(
            "vault_name",
            help="The Azure Key Vault to create the certificate in",
            options_list=["--vault-name"],
        )
        c.argument(
            "output_dir",
            help="The output directory where files will get created",
            options_list=["--output-dir"],
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
            help="Encryption public key for the member for recovery",
            options_list=["--encryption-public-key"],
            required=False,
        )
        c.argument(
            "recovery_role",
            arg_type=get_enum_type(["participant", "owner"]),
            help="Whether the member is a participant or owner in recovery. Defaults to participant.",
            options_list=["--recovery-role"],
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
            "auto_start",
            help="Whether the application needs to be started automatically or not.",
            action="store_true",
            options_list=["--auto-start"],
            required=False,
        )
        c.argument(
            "command_line",
            help="The command to run.",
            options_list=["--command-line"],
        )
        c.argument(
            "ports",
            help="The ports to be exposed for incoming traffic to the application.",
            options_list=["--ports"],
            type=int,
            nargs="*",
            required=False,
        )
        c.argument(
            "datasources",
            help="The datasources to expose.",
            options_list=["--datasources"],
            validator=validate_datasources,
            nargs="*",
        )
        c.argument(
            "datasinks",
            help="The datasinks to expose.",
            options_list=["--datasinks"],
            validator=validate_datasinks,
            nargs="*",
        )
        c.argument(
            "env_vars",
            help="The environment variables to expose.",
            options_list=["--env-vars"],
            validator=validate_env_vars,
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

    with self.argument_context("cleanroom config network http enable") as c:
        c.argument(
            "direction",
            arg_type=get_enum_type(TrafficDirection),
        )
        c.argument(
            "policy_bundle_url",
            help="The policy bundle URL",
            options_list=["--policy-bundle-url", "--policy"],
            default="",
        )

    with self.argument_context("cleanroom config network http disable") as c:
        c.argument(
            "direction",
            arg_type=get_enum_type(TrafficDirection),
        )

    with self.argument_context("cleanroom config network tcp enable") as c:
        c.argument(
            "allowed_ips",
            help="The network IPs to which egress is allowed from the clean room. Please specify in the format of 'IP_ADDRESS1:PORT1 IP_ADDRESS2:PORT2 ...'",
            options_list=["--allowed-ips"],
            validator=validate_ips,
            nargs="*",
        )

    with self.argument_context("cleanroom config network dns enable") as c:
        c.argument(
            "port",
            help="The port at which the DNS server is running. Default is 53",
            options_list=["--port"],
            type=int,
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
            required=False,
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
            required=False,
        )
        c.argument(
            "identity",
            help="The identity to use for accessing the datastore.",
            required=False,
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
            required=False,
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
            required=False,
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
        c.argument("issuer_url", required=False)

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

    # Clean Room cluster provider
    with self.argument_context("cleanroom cluster provider deploy") as c:
        c.argument(
            "provider_client_name",
            help="Name to use for the provider client instance",
            options_list=["--name"],
            required=False,
            default="cleanroom-cluster-provider",
        )
    with self.argument_context("cleanroom cluster provider remove") as c:
        c.argument(
            "provider_client_name",
            help="Name to use for the provider client instance",
            options_list=["--name"],
            required=False,
            default="cleanroom-cluster-provider",
        )

    # cluster
    with self.argument_context("cleanroom cluster") as c:
        c.argument(
            "provider_client_name",
            help="Name of the client instance",
            options_list=["--provider-client"],
            required=False,
            default="cleanroom-cluster-provider",
        )
        c.argument(
            "infra_type",
            arg_type=get_enum_type(["caci", "virtual"]),
            help="The platform used for hosting the cluster",
            options_list=["--infra-type"],
            required=False,
            default="caci",
        )

    with self.argument_context("cleanroom cluster up") as c:
        c.argument(
            "cluster_name",
            help="A unique name for the cluster",
            options_list=["--name"],
        )
        c.argument(
            "resource_group",
            help="A resource group under which to create the resources used by the cluster",
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

    with self.argument_context("cleanroom cluster create") as c:
        c.argument(
            "cluster_name",
            help="A unique name for the cluster",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )
        c.argument(
            "enable_observability",
            action="store_true",
            help="Whether to enable observability on the cluster",
            options_list=["--enable-observability"],
            required=False,
        )
        c.argument(
            "enable_analytics_workload",
            action="store_true",
            help="Whether to enable analytics workload support on the cluster",
            options_list=["--enable-analytics-workload"],
            required=False,
        )
        c.argument(
            "analytics_workload_config_url",
            help="The url containing the inputs required to enable analytics workload on the cluster",
            options_list=["--analytics-workload-config-url"],
            required=False,
        )
        c.argument(
            "analytics_workload_config_url_ca_cert",
            help="CA certificate to verify the peer for the url",
            options_list=["--analytics-workload-config-url-ca-cert"],
            required=False,
        )
        c.argument(
            "analytics_workload_disable_telemetry_collection",
            action="store_true",
            help="Whether to disable telemetry collection for analytics workload",
            options_list=["--analytics-workload-disable-telemetry-collection"],
            required=False,
            default=False,
        )
        c.argument(
            "analytics_workload_security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--analytics-workload-security-policy-creation-option"],
            required=False,
            default=default_workloads_security_policy_creation_option,
        )
        c.argument(
            "analytics_workload_security_policy",
            help="Path to a file containing or a base64 encoded string itself that specifies the security policy to use instead of passing a --analytics-workload-security-policy-creation-option value.",
            options_list=["--analytics-workload-security-policy"],
            required=False,
        )

    with self.argument_context("cleanroom cluster update") as c:
        c.argument(
            "cluster_name",
            help="The name of the cluster",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )
        c.argument(
            "enable_observability",
            action="store_true",
            help="Whether to enable observability on the cluster",
            options_list=["--enable-observability"],
            required=False,
            default=False,
        )
        c.argument(
            "enable_analytics_workload",
            action="store_true",
            help="Whether to enable analytics workload support on the cluster",
            options_list=["--enable-analytics-workload"],
            required=False,
        )
        c.argument(
            "analytics_workload_config_url",
            help="The url containing the inputs required to enable analytics workload on the cluster",
            options_list=["--analytics-workload-config-url"],
            required=False,
        )
        c.argument(
            "analytics_workload_config_url_ca_cert",
            help="CA certificate to verify the peer for the url",
            options_list=["--analytics-workload-config-url-ca-cert"],
            required=False,
        )
        c.argument(
            "analytics_workload_disable_telemetry_collection",
            action="store_true",
            help="Whether to disable telemetry collection for analytics workload",
            options_list=["--analytics-workload-disable-telemetry-collection"],
            required=False,
            default=False,
        )
        c.argument(
            "analytics_workload_security_policy_creation_option",
            arg_type=get_enum_type(
                ["cached", "cached-debug", "allow-all", "user-supplied"]
            ),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--analytics-workload-security-policy-creation-option"],
            required=False,
            default=default_workloads_security_policy_creation_option,
        )
        c.argument(
            "analytics_workload_security_policy",
            help="Path to a file containing or a base64 encoded string itself that specifies the security policy to use instead of passing a --analytics-workload-security-policy-creation-option value.",
            options_list=["--analytics-workload-security-policy"],
            required=False,
        )

    with self.argument_context("cleanroom cluster show") as c:
        c.argument(
            "cluster_name",
            help="The name of the cluster",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom cluster delete") as c:
        c.argument(
            "cluster_name",
            help="The name of the cluster",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )

    with self.argument_context("cleanroom cluster get-kubeconfig") as c:
        c.argument(
            "cluster_name",
            help="The name of the cluster",
            options_list=["--name"],
        )
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )
        c.argument(
            "file",
            help="The kubernetes configuration file to create.",
            options_list=["--file", "-f"],
        )

    with self.argument_context(
        "cleanroom cluster analytics-workload deployment generate"
    ) as c:
        c.argument(
            "provider_config",
            help="Infra specific provider_config details as JSON, or a path to a file containing a JSON description.",
            options_list=["--provider-config"],
            required=False,
        )
        c.argument(
            "contract_id",
            help="The contract containing the inputs required to enable analytics workload on the cluster",
            options_list=["--contract-id"],
        )
        c.argument(
            "disable_telemetry_collection",
            help="Defines whether to disable telemetry collection in the analytics workload.",
            options_list=["--disable-telemetry-collection"],
            action="store_true",
            required=False,
            default=False,
        )
        c.argument(
            "gov_client_name",
            help="Name of the governance client instance to use to access the contract",
            options_list=["--governance-client"],
        )
        c.argument(
            "security_policy_creation_option",
            arg_type=get_enum_type(["cached", "cached-debug", "allow-all"]),
            help="Whether to use the cached policy files or use the allow all security policy",
            options_list=["--security-policy-creation-option"],
            required=False,
            default=default_workloads_security_policy_creation_option,
        )
        c.argument(
            "output_dir",
            help="The output directory where files will get created",
            options_list=["--output-dir"],
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
            "signing_cert_id",
            help="URI for the operator signing certificate stored in Azure Key Vault or a path to a file containing the URI",
            options_list=["--signing-cert-id"],
            required=False,
        )
        c.argument(
            "signing_cert",
            help="Path to the PEM-encoded operator signing cert",
            options_list=["--signing-cert"],
            required=False,
        )
        c.argument(
            "signing_key",
            help="Path to the PEM-encoded operator signing key",
            options_list=["--signing-key"],
            required=False,
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
            arg_type=get_enum_type(["caci", "virtual"]),
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
            "encryption_private_key",
            help="Path to the PEM-encoded private key",
            options_list=["--operator-recovery-encryption-private-key"],
            required=False,
        )
        c.argument(
            "encryption_key_id",
            help="URI for the encryption key stored in Azure Key Vault or a path to a file containing the URI",
            options_list=["--operator-recovery-encryption-key-id"],
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
            "encryption_private_key",
            help="Path to the PEM-encoded private key",
            options_list=["--encryption-private-key"],
            required=False,
        )
        c.argument(
            "encryption_key_id",
            help="URI for the encryption key stored in Azure Key Vault or a path to a file containing the URI",
            options_list=["--encryption-key-id"],
            required=False,
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

    with self.argument_context(
        "cleanroom ccf network recovery-agent show-network-report"
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

    # CCF consortium manager
    with self.argument_context("cleanroom ccf consortium-manager") as c:
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
            help="The platform used for hosting the CCF consortium manager",
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

    with self.argument_context("cleanroom ccf consortium-manager create") as c:
        c.argument(
            "consortium_manager_name",
            help="A unique name for the CCF consortium manager",
            options_list=["--name"],
        )

    with self.argument_context("cleanroom ccf consortium-manager show") as c:
        c.argument(
            "consortium_manager_name",
            help="A unique name for the CCF consortium manager",
            options_list=["--name"],
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
            options_list=["--datastore-config-file", "--config"],
            required=False,
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
            arg_type=get_enum_type(DataStoreEntry.EncryptionMode),
            required=False,
        )
        c.argument(
            "secretstore_config_file",
            help="The config file of the secret store",
            options_list=["--secretstore-config-file", "--secretstore-config"],
            required=False,
        )
        c.argument(
            "datastore_secret_store",
            help="The name of the secret store to use for the datastore",
            options_list=["--secretstore", "--datastore-secretstore"],
            required=False,
        )
        c.argument(
            "backingstore_type",
            arg_type=get_enum_type(DataStoreEntry.StoreType),
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
