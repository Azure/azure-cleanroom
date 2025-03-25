from knack.log import get_logger
from azure.cli.core.util import CLIError
from cleanroom_common.azure_cleanroom_core.exceptions.exception import (
    CleanroomSpecificationError,
    ErrorCode,
)

from cleanroom_common.azure_cleanroom_core.models.model import *

logger = get_logger(__name__)


def get_deployment_template_internal(
    cleanroom_spec: CleanRoomSpecification,
    contract_id: str,
    ccf_endpoint: str,
    sslServerCertBase64: str,
    debug_mode: bool,
):
    from cleanroom_common.azure_cleanroom_core.utilities.helpers import (
        get_deployment_template,
    )

    try:
        return get_deployment_template(
            cleanroom_spec,
            contract_id,
            ccf_endpoint,
            sslServerCertBase64,
            debug_mode,
            logger,
        )
    except CleanroomSpecificationError as e:
        raise CLIError(f"Error generating deployment template: {e}")
