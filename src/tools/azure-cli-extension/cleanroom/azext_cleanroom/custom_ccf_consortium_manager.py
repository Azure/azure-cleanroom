# pylint: disable=line-too-long,too-many-statements,too-many-lines
# pylint: disable=too-many-return-statements
# pylint: disable=too-many-locals
# pylint: disable=protected-access
# pylint: disable=broad-except
# pylint: disable=too-many-branches
# pylint: disable=missing-timeout
# pylint: disable=missing-function-docstring
# pylint: disable=missing-module-docstring


# Note (gsinha): Various imports are also mentioned inline in the code at the point of usage.
# This is done to speed up command execution as having all the imports listed at top level is making
# execution slow for every command even if the top level imported packaged will not be used by that
# command.

import requests
from azure.cli.core.util import CLIError
from knack.log import get_logger

from .custom import response_error_message
from .custom_ccf import (
    get_provider_client_endpoint,
    parse_provider_config,
)

logger = get_logger(__name__)


def ccf_consortium_manager_create(
    cmd,
    consortium_manager_name,
    infra_type,
    key_vault,
    maa_endpoint,
    identity,
    provider_config,
    provider_client_name,
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)

    from .utilities._azcli_helpers import az_cli

    key_vault_url = az_cli(
        f"resource show --id {key_vault} --query properties.vaultUri"
    )
    content = {
        "infraType": infra_type,
        "akvEndpoint": key_vault_url,
        "maaEndpoint": maa_endpoint,
        "managedIdentityId": identity,
        "providerConfig": provider_config,
    }

    logger.warning(
        f"Run `docker compose -p {provider_client_name} logs -f` to monitor consortium manager creation progress."
    )
    r = requests.post(
        f"{provider_endpoint}/consortiummanagers/{consortium_manager_name}/create",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()


def ccf_consortium_manager_show(
    cmd, consortium_manager_name, infra_type, provider_config, provider_client_name
):
    provider_endpoint = get_provider_client_endpoint(cmd, provider_client_name)
    provider_config = parse_provider_config(provider_config, infra_type)
    content = {
        "infraType": infra_type,
        "providerConfig": provider_config,
    }
    r = requests.post(
        f"{provider_endpoint}/consortiummanagers/{consortium_manager_name}/get",
        json=content,
    )
    if r.status_code != 200:
        raise CLIError(response_error_message(r))
    return r.json()
