# pylint: disable=too-many-lines
# coding=utf-8
import json
from collections.abc import MutableMapping
from io import IOBase
from typing import IO, Any, Callable, Literal, Optional, TypeVar, Union, overload

from corehttp.exceptions import (
    ClientAuthenticationError,
    HttpResponseError,
    ResourceExistsError,
    ResourceNotFoundError,
    ResourceNotModifiedError,
    StreamClosedError,
    StreamConsumedError,
    map_error,
)
from corehttp.rest import HttpRequest, HttpResponse
from corehttp.runtime import PipelineClient
from corehttp.runtime.pipeline import PipelineResponse
from corehttp.utils import case_insensitive_dict

from .... import models as _models3
from ... import models as _models2
from .. import models as _models1
from .._configuration import ProxyClientConfiguration
from .._utils.model_base import SdkJSONEncoder, _deserialize
from .._utils.serialization import Deserializer, Serializer

JSON = MutableMapping[str, Any]
T = TypeVar("T")
ClsType = Optional[
    Callable[[PipelineResponse[HttpRequest, HttpResponse], T, dict[str, Any]], Any]
]

_SERIALIZER = Serializer()
_SERIALIZER.client_side_validation = False


def build_contracts_list_contracts_request(
    *, state: Optional[str] = None, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts"

    # Construct parameters
    if state is not None:
        _params["state"] = _SERIALIZER.query("state", state, "str")

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(
        method="GET", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_contracts_get_contract_request(
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_contracts_update_contract_request(
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/contracts/{contractId}"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(method="PUT", url=_url, headers=_headers, **kwargs)


def build_contracts_propose_contract_change_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/propose"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contracts_vote_accept_contract_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/vote_accept"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contracts_vote_reject_contract_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/vote_reject"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contracts_vote_on_contract_request(
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/vote"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_certificate_authority_generate_signing_key_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/ca/generateSigningKey"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_certificate_authority_get_info_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/ca/info"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_certificate_authority_attestation_generate_endorsed_cert_request(  # pylint: disable=name-too-long
    **kwargs: Any,
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/ca/generateEndorsedCert"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_clean_room_policy_get_clean_room_policy_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/cleanroompolicy"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_clean_room_policy_propose_clean_room_policy_change_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/cleanroompolicy/propose"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_clean_room_policy_get_delegate_policy_request(  # pylint: disable=name-too-long
    contract_id: str, delegate_type: str, delegate_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = (
        "/contracts/{contractId}/cleanroompolicy/delegates/{delegateType}/{delegateId}"
    )
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
        "delegateType": _SERIALIZER.url("delegate_type", delegate_type, "str"),
        "delegateId": _SERIALIZER.url("delegate_id", delegate_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_clean_room_policy_list_delegate_policies_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/cleanroompolicy/delegates"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_clean_room_policy_delegates_attestation_set_delegate_policy_request(  # pylint: disable=name-too-long
    delegate_type: str, delegate_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/cleanroompolicy/delegates/{delegateType}/{delegateId}"
    path_format_arguments = {
        "delegateType": _SERIALIZER.url("delegate_type", delegate_type, "str"),
        "delegateId": _SERIALIZER.url("delegate_id", delegate_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_proposals_get_deployment_proposal_request(  # pylint: disable=name-too-long
    contract_id: str,
    proposal_type: Literal["deploymentspec", "deploymentinfo"],
    **kwargs: Any,
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/{proposalType}"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
        "proposalType": _SERIALIZER.url("proposal_type", proposal_type, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_contract_proposals_propose_deployment_request(  # pylint: disable=name-too-long
    contract_id: str,
    proposal_type: Literal["deploymentspec", "deploymentinfo"],
    **kwargs: Any,
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: str = kwargs.pop("content_type")
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/{proposalType}/propose"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
        "proposalType": _SERIALIZER.url("proposal_type", proposal_type, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Content-Type"] = _SERIALIZER.header("content_type", content_type, "str")
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_enable_execution_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    # Construct URL
    _url = "/contracts/{contractId}/execution/enable"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    return HttpRequest(method="POST", url=_url, **kwargs)


def build_contract_runtime_options_disable_execution_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    # Construct URL
    _url = "/contracts/{contractId}/execution/disable"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    return HttpRequest(method="POST", url=_url, **kwargs)


def build_contract_runtime_options_propose_enable_logging_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/logging/propose-enable"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_propose_disable_logging_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/logging/propose-disable"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_propose_enable_telemetry_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/telemetry/propose-enable"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_propose_disable_telemetry_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/telemetry/propose-disable"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_check_execution_status_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/checkstatus/execution"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_check_logging_status_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/checkstatus/logging"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_contract_runtime_options_check_telemetry_status_request(  # pylint: disable=name-too-long
    contract_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/checkstatus/telemetry"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_consent_check_check_execution_consent_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/consentcheck/execution"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_consent_check_check_logging_consent_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/consentcheck/logging"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_consent_check_check_telemetry_consent_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/consentcheck/telemetry"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_events_get_events_request(
    contract_id: str,
    *,
    id: Optional[str] = None,
    scope: Optional[str] = None,
    from_seqno: Optional[int] = None,
    to_seqno: Optional[int] = None,
    max_seqno_per_page: Optional[int] = None,
    **kwargs: Any,
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/events"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct parameters
    if id is not None:
        _params["id"] = _SERIALIZER.query("id", id, "str")
    if scope is not None:
        _params["scope"] = _SERIALIZER.query("scope", scope, "str")
    if from_seqno is not None:
        _params["from_seqno"] = _SERIALIZER.query("from_seqno", from_seqno, "int")
    if to_seqno is not None:
        _params["to_seqno"] = _SERIALIZER.query("to_seqno", to_seqno, "int")
    if max_seqno_per_page is not None:
        _params["max_seqno_per_page"] = _SERIALIZER.query(
            "max_seqno_per_page", max_seqno_per_page, "int"
        )

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(
        method="GET", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_events_attestation_store_event_request(  # pylint: disable=name-too-long
    *, id: Optional[str] = None, scope: Optional[str] = None, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/events"

    # Construct parameters
    if id is not None:
        _params["id"] = _SERIALIZER.query("id", id, "str")
    if scope is not None:
        _params["scope"] = _SERIALIZER.query("scope", scope, "str")

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(
        method="PUT", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_member_documents_list_member_documents_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_member_documents_get_member_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments/{documentId}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_member_documents_update_member_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/memberdocuments/{documentId}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(method="PUT", url=_url, headers=_headers, **kwargs)


def build_member_documents_propose_member_document_change_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments/{documentId}/propose"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_member_documents_vote_accept_member_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments/{documentId}/vote_accept"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_member_documents_vote_reject_member_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments/{documentId}/vote_reject"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_member_documents_vote_on_member_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments/{documentId}/vote"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_member_documents_get_member_document_with_attestation_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/memberdocuments/{documentId}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_members_list_members_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/members"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_members_update_state_digest_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/members/statedigests/update"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_members_acknowledge_state_digest_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "text/plain")

    # Construct URL
    _url = "/members/statedigests/ack"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_network_show_network_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/network/show"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_node_check_app_ready_request(**kwargs: Any) -> HttpRequest:
    # Construct URL
    _url = "/node/ready/app"

    return HttpRequest(method="GET", url=_url, **kwargs)


def build_node_check_gov_ready_request(**kwargs: Any) -> HttpRequest:
    # Construct URL
    _url = "/node/ready/gov"

    return HttpRequest(method="GET", url=_url, **kwargs)


def build_oauth_get_token_subject_policy_request(  # pylint: disable=name-too-long
    contract_id: str, subject_name: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/oauth/federation/subjects/{subjectName}/cleanroompolicy"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
        "subjectName": _SERIALIZER.url("subject_name", subject_name, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_oauth_attestation_get_token_request(  # pylint: disable=name-too-long
    *, sub: str, tenant_id: str, aud: str, iss: Optional[str] = None, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/oauth/token"

    # Construct parameters
    _params["sub"] = _SERIALIZER.query("sub", sub, "str")
    _params["tenantId"] = _SERIALIZER.query("tenant_id", tenant_id, "str")
    _params["aud"] = _SERIALIZER.query("aud", aud, "str")
    if iss is not None:
        _params["iss"] = _SERIALIZER.query("iss", iss, "str")

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(
        method="POST", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_oidc_generate_issuer_signing_key_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/oidc/generateSigningKey"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_oidc_set_issuer_url_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/oidc/setIssuerUrl"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_oidc_get_issuer_info_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/oidc/issuerInfo"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_proposals_list_proposals_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_proposals_get_proposal_request(
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_proposals_get_proposal_votes_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/votes"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_proposals_get_proposal_historical_request(  # pylint: disable=name-too-long
    proposal_id: str, *, query_string: Optional[str] = None, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/historical"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct parameters
    if query_string is not None:
        _params["queryString"] = _SERIALIZER.query("query_string", query_string, "str")

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(
        method="GET", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_proposals_get_proposal_actions_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/actions"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_proposals_withdraw_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/withdraw"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_proposals_create_proposal_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/create"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_proposals_vote_accept_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/ballots/vote_accept"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_proposals_vote_reject_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/ballots/vote_reject"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_proposals_vote_on_proposal_request(
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/proposals/{proposalId}/ballots/vote"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_runtime_options_propose_runtime_option_request(  # pylint: disable=name-too-long
    option: str, action: Literal["enable", "disable"], **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/runtimeoptions/{option}/propose-{action}"
    path_format_arguments = {
        "option": _SERIALIZER.url("option", option, "str"),
        "action": _SERIALIZER.url("action", action, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_runtime_options_check_runtime_option_status_request(  # pylint: disable=name-too-long
    option: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/runtimeoptions/checkstatus/{option}"
    path_format_arguments = {
        "option": _SERIALIZER.url("option", option, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_secrets_list_secrets_request(contract_id: str, **kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/secrets"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_secrets_store_secret_request(
    contract_id: str, secret_name: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/secrets/{secretName}"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
        "secretName": _SERIALIZER.url("secret_name", secret_name, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="PUT", url=_url, headers=_headers, **kwargs)


def build_secrets_get_secret_policy_request(
    contract_id: str, secret_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/contracts/{contractId}/secrets/{secretId}/cleanroompolicy"
    path_format_arguments = {
        "contractId": _SERIALIZER.url("contract_id", contract_id, "str"),
        "secretId": _SERIALIZER.url("secret_id", secret_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_secrets_attestation_get_secret_request(  # pylint: disable=name-too-long
    secret_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/secrets/{secretId}"
    path_format_arguments = {
        "secretId": _SERIALIZER.url("secret_id", secret_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_secrets_attestation_store_secret_with_attestation_request(  # pylint: disable=name-too-long
    secret_name: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/secrets/{secretName}"
    path_format_arguments = {
        "secretName": _SERIALIZER.url("secret_name", secret_name, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="PUT", url=_url, headers=_headers, **kwargs)


def build_secrets_attestation_set_secret_policy_request(  # pylint: disable=name-too-long
    secret_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/secrets/{secretId}/cleanroompolicy"
    path_format_arguments = {
        "secretId": _SERIALIZER.url("secret_id", secret_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_updates_check_updates_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/checkUpdates"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_documents_list_user_documents_request(  # pylint: disable=name-too-long
    *, label_selector: Optional[str] = None, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments"

    # Construct parameters
    if label_selector is not None:
        _params["labelSelector"] = _SERIALIZER.query(
            "label_selector", label_selector, "str"
        )

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(
        method="GET", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_user_documents_get_user_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_documents_update_user_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    # Construct URL
    _url = "/userdocuments/{documentId}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )

    return HttpRequest(method="PUT", url=_url, headers=_headers, **kwargs)


def build_user_documents_propose_user_document_change_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/propose"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_vote_accept_user_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/vote_accept"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_vote_reject_user_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/vote_reject"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_vote_on_user_document_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/vote"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_enable_user_document_runtime_option_request(  # pylint: disable=name-too-long
    document_id: str, runtime_option: str, **kwargs: Any
) -> HttpRequest:
    # Construct URL
    _url = "/userdocuments/{documentId}/runtimeoptions/{runtimeOption}/enable"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
        "runtimeOption": _SERIALIZER.url("runtime_option", runtime_option, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    return HttpRequest(method="POST", url=_url, **kwargs)


def build_user_documents_disable_user_document_runtime_option_request(  # pylint: disable=name-too-long
    document_id: str, runtime_option: str, **kwargs: Any
) -> HttpRequest:
    # Construct URL
    _url = "/userdocuments/{documentId}/runtimeoptions/{runtimeOption}/disable"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
        "runtimeOption": _SERIALIZER.url("runtime_option", runtime_option, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    return HttpRequest(method="POST", url=_url, **kwargs)


def build_user_documents_check_user_document_runtime_option_status_request(  # pylint: disable=name-too-long
    document_id: str, runtime_option: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/checkstatus/{runtimeOption}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
        "runtimeOption": _SERIALIZER.url("runtime_option", runtime_option, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_get_user_document_with_attestation_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_check_execution_consent_attestation_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/consentcheck/execution"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_documents_check_telemetry_consent_attestation_request(  # pylint: disable=name-too-long
    document_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/userdocuments/{documentId}/consentcheck/telemetry"
    path_format_arguments = {
        "documentId": _SERIALIZER.url("document_id", document_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_identities_list_user_identities_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/identities"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_identities_get_user_identity_request(  # pylint: disable=name-too-long
    identity_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/identities/{identityId}"
    path_format_arguments = {
        "identityId": _SERIALIZER.url("identity_id", identity_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_identities_add_user_identity_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/identities/add"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_invitations_list_user_invitations_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/invitations"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_invitations_get_user_invitation_request(  # pylint: disable=name-too-long
    invitation_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/invitations/{invitationId}"
    path_format_arguments = {
        "invitationId": _SERIALIZER.url("invitation_id", invitation_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_invitations_accept_invitation_request(  # pylint: disable=name-too-long
    invitation_id: str, **kwargs: Any
) -> HttpRequest:
    # Construct URL
    _url = "/users/invitations/{invitationId}/accept"
    path_format_arguments = {
        "invitationId": _SERIALIZER.url("invitation_id", invitation_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    return HttpRequest(method="POST", url=_url, **kwargs)


def build_user_invitations_propose_invitation_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/invitations/propose"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_proposals_create_user_proposal_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/proposals/create"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_proposals_get_user_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/proposals/{proposalId}"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_proposals_get_user_proposal_status_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/proposals/{proposalId}/status"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_user_proposals_withdraw_user_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/proposals/{proposalId}/withdraw"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_proposals_vote_accept_user_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/proposals/{proposalId}/ballots/vote_accept"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_user_proposals_vote_reject_user_proposal_request(  # pylint: disable=name-too-long
    proposal_id: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/proposals/{proposalId}/ballots/vote_reject"
    path_format_arguments = {
        "proposalId": _SERIALIZER.url("proposal_id", proposal_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_users_list_users_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_users_get_user_request(user_id: str, **kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/{userId}"
    path_format_arguments = {
        "userId": _SERIALIZER.url("user_id", user_id, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_users_is_user_active_request(
    *, authorization: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/users/isactive"

    # Construct headers
    _headers["authorization"] = _SERIALIZER.header(
        "authorization", authorization, "str"
    )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_workspace_check_ready_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/ready"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_configure_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    content_type: Optional[str] = kwargs.pop(
        "content_type", _headers.pop("Content-Type", None)
    )
    accept = _headers.pop("Accept", "text/plain")

    # Construct URL
    _url = "/configure"

    # Construct headers
    if content_type is not None:
        _headers["Content-Type"] = _SERIALIZER.header(
            "content_type", content_type, "str"
        )
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="POST", url=_url, headers=_headers, **kwargs)


def build_workspace_get_access_token_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/identity/accessToken"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_show_configuration_request(  # pylint: disable=name-too-long
    *, signing_key: Optional[bool] = None, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
    _params = case_insensitive_dict(kwargs.pop("params", {}) or {})

    accept = _headers.pop("Accept", None)

    # Construct URL
    _url = "/show"

    # Construct parameters
    if signing_key is not None:
        _params["signingKey"] = _SERIALIZER.query("signing_key", signing_key, "bool")

    # Construct headers
    if accept is not None:
        _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(
        method="GET", url=_url, params=_params, headers=_headers, **kwargs
    )


def build_workspace_get_constitution_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "text/plain")

    # Construct URL
    _url = "/constitution"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_get_service_info_request(**kwargs: Any) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "text/plain")

    # Construct URL
    _url = "/service/info"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_get_js_app_endpoints_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "text/plain")

    # Construct URL
    _url = "/jsapp/endpoints"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_get_js_app_modules_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/jsapp/modules"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_list_js_app_modules_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/jsapp/modules/list"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_get_js_app_module_request(  # pylint: disable=name-too-long
    module_name: str, **kwargs: Any
) -> HttpRequest:
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "text/plain")

    # Construct URL
    _url = "/jsapp/modules/{moduleName}"
    path_format_arguments = {
        "moduleName": _SERIALIZER.url("module_name", module_name, "str"),
    }

    _url: str = _url.format(**path_format_arguments)  # type: ignore

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


def build_workspace_get_js_app_bundle_request(
    **kwargs: Any,
) -> HttpRequest:  # pylint: disable=name-too-long
    _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})

    accept = _headers.pop("Accept", "application/json")

    # Construct URL
    _url = "/jsapp/bundle"

    # Construct headers
    _headers["Accept"] = _SERIALIZER.header("accept", accept, "str")

    return HttpRequest(method="GET", url=_url, headers=_headers, **kwargs)


class ContractsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`contracts` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_contracts(
        self, *, state: Optional[str] = None, **kwargs: Any
    ) -> _models2.ContractsListResponse:
        """Lists all contracts. Token mode only.

        :keyword state: Default value is None.
        :paramtype state: str
        :return: ContractsListResponse. The ContractsListResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ContractsListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ContractsListResponse] = kwargs.pop("cls", None)

        _request = build_contracts_list_contracts_request(
            state=state,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ContractsListResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_contract(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.ContractResponse:
        """Gets contract details. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: ContractResponse. The ContractResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ContractResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ContractResponse] = kwargs.pop("cls", None)

        _request = build_contracts_get_contract_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ContractResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def update_contract(
        self,
        contract_id: str,
        contract: _models3.PutContractRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param contract: Required.
        :type contract: ~cleanroom.governance.models.PutContractRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def update_contract(
        self,
        contract_id: str,
        contract: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param contract: Required.
        :type contract: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def update_contract(
        self,
        contract_id: str,
        contract: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param contract: Required.
        :type contract: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def update_contract(  # pylint: disable=inconsistent-return-statements
        self,
        contract_id: str,
        contract: Union[_models3.PutContractRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> None:
        """Updates a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param contract: Is one of the following types: PutContractRequest, JSON, IO[bytes] Required.
        :type contract: ~cleanroom.governance.models.PutContractRequest or JSON or IO[bytes]
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(contract, (IOBase, bytes)):
            _content = contract
        else:
            _content = json.dumps(contract, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_contracts_update_contract_request(
            contract_id=contract_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    @overload
    def propose_contract_change(
        self,
        contract_id: str,
        proposal: _models2.ContractProposal,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a contract change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Required.
        :type proposal: ~cleanroom.governance.client.models.ContractProposal
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_contract_change(
        self,
        contract_id: str,
        proposal: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a contract change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Required.
        :type proposal: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_contract_change(
        self,
        contract_id: str,
        proposal: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a contract change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Required.
        :type proposal: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def propose_contract_change(
        self,
        contract_id: str,
        proposal: Union[_models2.ContractProposal, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a contract change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Is one of the following types: ContractProposal, JSON, IO[bytes] Required.
        :type proposal: ~cleanroom.governance.client.models.ContractProposal or JSON or IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(proposal, (IOBase, bytes)):
            _content = proposal
        else:
            _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_contracts_propose_contract_change_request(
            contract_id=contract_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_accept_contract(
        self,
        contract_id: str,
        vote: _models2.ProposalVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_accept_contract(
        self,
        contract_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_accept_contract(
        self,
        contract_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_accept_contract(
        self,
        contract_id: str,
        vote: Union[_models2.ProposalVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Is one of the following types: ProposalVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_contracts_vote_accept_contract_request(
            contract_id=contract_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_reject_contract(
        self,
        contract_id: str,
        vote: _models2.ProposalVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_reject_contract(
        self,
        contract_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_reject_contract(
        self,
        contract_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_reject_contract(
        self,
        contract_id: str,
        vote: Union[_models2.ProposalVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Is one of the following types: ProposalVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_contracts_vote_reject_contract_request(
            contract_id=contract_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_on_contract(
        self,
        contract_id: str,
        vote: _models2.CustomBallotVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts a custom ballot vote on a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallotVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_contract(
        self,
        contract_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts a custom ballot vote on a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_contract(
        self,
        contract_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts a custom ballot vote on a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_on_contract(
        self,
        contract_id: str,
        vote: Union[_models2.CustomBallotVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts a custom ballot vote on a contract. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param vote: Is one of the following types: CustomBallotVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallotVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_contracts_vote_on_contract_request(
            contract_id=contract_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class CertificateAuthorityOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`certificate_authority` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def generate_signing_key(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.SigningKeyResponse:
        """Generates a signing key. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: SigningKeyResponse. The SigningKeyResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SigningKeyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.SigningKeyResponse] = kwargs.pop("cls", None)

        _request = build_certificate_authority_generate_signing_key_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.SigningKeyResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_info(self, contract_id: str, **kwargs: Any) -> _models2.CAInfoResponse:
        """Gets CA information. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: CAInfoResponse. The CAInfoResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.CAInfoResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.CAInfoResponse] = kwargs.pop("cls", None)

        _request = build_certificate_authority_get_info_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.CAInfoResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class CertificateAuthorityAttestationOperations:  # pylint: disable=name-too-long
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`certificate_authority_attestation` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def generate_endorsed_cert(
        self,
        request: _models2.AttestedRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Generates an endorsed certificate using attestation. Attestation mode only. Request must be
        signed and include SNP attestation report. Response is encrypted.

        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def generate_endorsed_cert(
        self, request: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.EncryptedResponse:
        """Generates an endorsed certificate using attestation. Attestation mode only. Request must be
        signed and include SNP attestation report. Response is encrypted.

        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def generate_endorsed_cert(
        self,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Generates an endorsed certificate using attestation. Attestation mode only. Request must be
        signed and include SNP attestation report. Response is encrypted.

        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def generate_endorsed_cert(
        self, request: Union[_models2.AttestedRequest, JSON, IO[bytes]], **kwargs: Any
    ) -> _models2.EncryptedResponse:
        """Generates an endorsed certificate using attestation. Attestation mode only. Request must be
        signed and include SNP attestation report. Response is encrypted.

        :param request: Is one of the following types: AttestedRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest or JSON or IO[bytes]
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.EncryptedResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = (
            build_certificate_authority_attestation_generate_endorsed_cert_request(
                content_type=content_type,
                content=_content,
                headers=_headers,
                params=_params,
            )
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.EncryptedResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class CleanRoomPolicyOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`clean_room_policy` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def get_clean_room_policy(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.CleanRoomPolicyResponse:
        """Gets clean room policy. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: CleanRoomPolicyResponse. The CleanRoomPolicyResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.CleanRoomPolicyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.CleanRoomPolicyResponse] = kwargs.pop("cls", None)

        _request = build_clean_room_policy_get_clean_room_policy_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.CleanRoomPolicyResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def propose_clean_room_policy_change(
        self,
        contract_id: str,
        proposal: _models2.CleanRoomPolicyProposal,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a clean room policy change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Required.
        :type proposal: ~cleanroom.governance.client.models.CleanRoomPolicyProposal
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_clean_room_policy_change(
        self,
        contract_id: str,
        proposal: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a clean room policy change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Required.
        :type proposal: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_clean_room_policy_change(
        self,
        contract_id: str,
        proposal: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a clean room policy change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Required.
        :type proposal: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def propose_clean_room_policy_change(
        self,
        contract_id: str,
        proposal: Union[_models2.CleanRoomPolicyProposal, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a clean room policy change. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal: Is one of the following types: CleanRoomPolicyProposal, JSON, IO[bytes]
         Required.
        :type proposal: ~cleanroom.governance.client.models.CleanRoomPolicyProposal or JSON or
         IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(proposal, (IOBase, bytes)):
            _content = proposal
        else:
            _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_clean_room_policy_propose_clean_room_policy_change_request(
            contract_id=contract_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_delegate_policy(
        self, contract_id: str, delegate_type: str, delegate_id: str, **kwargs: Any
    ) -> _models2.DelegatePolicyResponse:
        """Gets delegate policy. Works in both modes.

        :param contract_id: Required.
        :type contract_id: str
        :param delegate_type: Required.
        :type delegate_type: str
        :param delegate_id: Required.
        :type delegate_id: str
        :return: DelegatePolicyResponse. The DelegatePolicyResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.DelegatePolicyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.DelegatePolicyResponse] = kwargs.pop("cls", None)

        _request = build_clean_room_policy_get_delegate_policy_request(
            contract_id=contract_id,
            delegate_type=delegate_type,
            delegate_id=delegate_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.DelegatePolicyResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def list_delegate_policies(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.DelegatePoliciesListResponse:
        """Lists delegate policies. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: DelegatePoliciesListResponse. The DelegatePoliciesListResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.DelegatePoliciesListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.DelegatePoliciesListResponse] = kwargs.pop("cls", None)

        _request = build_clean_room_policy_list_delegate_policies_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.DelegatePoliciesListResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class CleanRoomPolicyDelegatesAttestationOperations:  # pylint: disable=name-too-long
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`clean_room_policy_delegates_attestation` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def set_delegate_policy(
        self,
        delegate_type: str,
        delegate_id: str,
        request: _models2.AttestedRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets clean room delegate policy with attestation. Attestation mode only. Request must be signed
        and include SNP attestation report.

        :param delegate_type: Required.
        :type delegate_type: str
        :param delegate_id: Required.
        :type delegate_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def set_delegate_policy(
        self,
        delegate_type: str,
        delegate_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets clean room delegate policy with attestation. Attestation mode only. Request must be signed
        and include SNP attestation report.

        :param delegate_type: Required.
        :type delegate_type: str
        :param delegate_id: Required.
        :type delegate_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def set_delegate_policy(
        self,
        delegate_type: str,
        delegate_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets clean room delegate policy with attestation. Attestation mode only. Request must be signed
        and include SNP attestation report.

        :param delegate_type: Required.
        :type delegate_type: str
        :param delegate_id: Required.
        :type delegate_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def set_delegate_policy(  # pylint: disable=inconsistent-return-statements
        self,
        delegate_type: str,
        delegate_id: str,
        request: Union[_models2.AttestedRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> None:
        """Sets clean room delegate policy with attestation. Attestation mode only. Request must be signed
        and include SNP attestation report.

        :param delegate_type: Required.
        :type delegate_type: str
        :param delegate_id: Required.
        :type delegate_id: str
        :param request: Is one of the following types: AttestedRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest or JSON or IO[bytes]
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = (
            build_clean_room_policy_delegates_attestation_set_delegate_policy_request(
                delegate_type=delegate_type,
                delegate_id=delegate_id,
                content_type=content_type,
                content=_content,
                headers=_headers,
                params=_params,
            )
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore


class ContractProposalsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`contract_proposals` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def get_deployment_proposal(
        self,
        contract_id: str,
        proposal_type: Literal["deploymentspec", "deploymentinfo"],
        **kwargs: Any,
    ) -> _models2.DeploymentProposalResponse:
        """Gets deployment proposal. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal_type: Is either a Literal["deploymentspec"] type or a Literal["deploymentinfo"]
         type. Required.
        :type proposal_type: str or str
        :return: DeploymentProposalResponse. The DeploymentProposalResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.DeploymentProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.DeploymentProposalResponse] = kwargs.pop("cls", None)

        _request = build_contract_proposals_get_deployment_proposal_request(
            contract_id=contract_id,
            proposal_type=proposal_type,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.DeploymentProposalResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def propose_deployment(
        self,
        contract_id: str,
        proposal_type: Literal["deploymentspec", "deploymentinfo"],
        proposal: Any,
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes a deployment. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param proposal_type: Is either a Literal["deploymentspec"] type or a Literal["deploymentinfo"]
         type. Required.
        :type proposal_type: str or str
        :param proposal: Required.
        :type proposal: any
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: str = kwargs.pop(
            "content_type", _headers.pop("Content-Type", "application/json")
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_contract_proposals_propose_deployment_request(
            contract_id=contract_id,
            proposal_type=proposal_type,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class ContractRuntimeOptionsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`contract_runtime_options` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def enable_execution(  # pylint: disable=inconsistent-return-statements
        self, contract_id: str, **kwargs: Any
    ) -> None:
        """Enables execution. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_enable_execution_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    def disable_execution(  # pylint: disable=inconsistent-return-statements
        self, contract_id: str, **kwargs: Any
    ) -> None:
        """Disables execution. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_disable_execution_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    def propose_enable_logging(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Proposes to enable logging. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_propose_enable_logging_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def propose_disable_logging(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Proposes to disable logging. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_propose_disable_logging_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def propose_enable_telemetry(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Proposes to enable telemetry. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_propose_enable_telemetry_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def propose_disable_telemetry(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Proposes to disable telemetry. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_propose_disable_telemetry_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def check_execution_status(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.RuntimeStatusResponse:
        """Checks execution status. Works in both modes.

        :param contract_id: Required.
        :type contract_id: str
        :return: RuntimeStatusResponse. The RuntimeStatusResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.RuntimeStatusResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.RuntimeStatusResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_check_execution_status_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.RuntimeStatusResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def check_logging_status(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.RuntimeStatusResponse:
        """Checks logging status. Works in both modes.

        :param contract_id: Required.
        :type contract_id: str
        :return: RuntimeStatusResponse. The RuntimeStatusResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.RuntimeStatusResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.RuntimeStatusResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_check_logging_status_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.RuntimeStatusResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def check_telemetry_status(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.RuntimeStatusResponse:
        """Checks telemetry status. Works in both modes.

        :param contract_id: Required.
        :type contract_id: str
        :return: RuntimeStatusResponse. The RuntimeStatusResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.RuntimeStatusResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.RuntimeStatusResponse] = kwargs.pop("cls", None)

        _request = build_contract_runtime_options_check_telemetry_status_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.RuntimeStatusResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class ConsentCheckOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`consent_check` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def check_execution_consent(
        self,
        request: _models2.AttestationRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks execution consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_execution_consent(
        self, request: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.ConsentCheckResponse:
        """Checks execution consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_execution_consent(
        self,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks execution consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def check_execution_consent(
        self,
        request: Union[_models2.AttestationRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks execution consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Is one of the following types: AttestationRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest or JSON or IO[bytes]
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ConsentCheckResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_consent_check_check_execution_consent_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ConsentCheckResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def check_logging_consent(
        self,
        request: _models2.AttestationRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks logging consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_logging_consent(
        self, request: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.ConsentCheckResponse:
        """Checks logging consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_logging_consent(
        self,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks logging consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def check_logging_consent(
        self,
        request: Union[_models2.AttestationRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks logging consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Is one of the following types: AttestationRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest or JSON or IO[bytes]
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ConsentCheckResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_consent_check_check_logging_consent_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ConsentCheckResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def check_telemetry_consent(
        self,
        request: _models2.AttestationRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks telemetry consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_telemetry_consent(
        self, request: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.ConsentCheckResponse:
        """Checks telemetry consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_telemetry_consent(
        self,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks telemetry consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def check_telemetry_consent(
        self,
        request: Union[_models2.AttestationRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks telemetry consent with attestation. Attestation mode only. Request must include SNP
        attestation report.

        :param request: Is one of the following types: AttestationRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest or JSON or IO[bytes]
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ConsentCheckResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_consent_check_check_telemetry_consent_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ConsentCheckResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class EventsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`events` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def get_events(
        self,
        contract_id: str,
        *,
        id: Optional[str] = None,
        scope: Optional[str] = None,
        from_seqno: Optional[int] = None,
        to_seqno: Optional[int] = None,
        max_seqno_per_page: Optional[int] = None,
        **kwargs: Any,
    ) -> _models2.EventsResponse:
        """Gets events. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :keyword id: Default value is None.
        :paramtype id: str
        :keyword scope: Default value is None.
        :paramtype scope: str
        :keyword from_seqno: Default value is None.
        :paramtype from_seqno: int
        :keyword to_seqno: Default value is None.
        :paramtype to_seqno: int
        :keyword max_seqno_per_page: Default value is None.
        :paramtype max_seqno_per_page: int
        :return: EventsResponse. The EventsResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EventsResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.EventsResponse] = kwargs.pop("cls", None)

        _request = build_events_get_events_request(
            contract_id=contract_id,
            id=id,
            scope=scope,
            from_seqno=from_seqno,
            to_seqno=to_seqno,
            max_seqno_per_page=max_seqno_per_page,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.EventsResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class EventsAttestationOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`events_attestation` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def store_event(
        self,
        event: _models2.AttestedEventRequest,
        *,
        id: Optional[str] = None,
        scope: Optional[str] = None,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Stores an event with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param event: Required.
        :type event: ~cleanroom.governance.client.models.AttestedEventRequest
        :keyword id: Default value is None.
        :paramtype id: str
        :keyword scope: Default value is None.
        :paramtype scope: str
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def store_event(
        self,
        event: JSON,
        *,
        id: Optional[str] = None,
        scope: Optional[str] = None,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Stores an event with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param event: Required.
        :type event: JSON
        :keyword id: Default value is None.
        :paramtype id: str
        :keyword scope: Default value is None.
        :paramtype scope: str
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def store_event(
        self,
        event: IO[bytes],
        *,
        id: Optional[str] = None,
        scope: Optional[str] = None,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Stores an event with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param event: Required.
        :type event: IO[bytes]
        :keyword id: Default value is None.
        :paramtype id: str
        :keyword scope: Default value is None.
        :paramtype scope: str
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def store_event(  # pylint: disable=inconsistent-return-statements
        self,
        event: Union[_models2.AttestedEventRequest, JSON, IO[bytes]],
        *,
        id: Optional[str] = None,
        scope: Optional[str] = None,
        **kwargs: Any,
    ) -> None:
        """Stores an event with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param event: Is one of the following types: AttestedEventRequest, JSON, IO[bytes] Required.
        :type event: ~cleanroom.governance.client.models.AttestedEventRequest or JSON or IO[bytes]
        :keyword id: Default value is None.
        :paramtype id: str
        :keyword scope: Default value is None.
        :paramtype scope: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(event, (IOBase, bytes)):
            _content = event
        else:
            _content = json.dumps(event, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_events_attestation_store_event_request(
            id=id,
            scope=scope,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore


class MemberDocumentsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`member_documents` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_member_documents(self, **kwargs: Any) -> _models2.MemberDocumentsList:
        """Lists member documents. Token mode only.

        :return: MemberDocumentsList. The MemberDocumentsList is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.MemberDocumentsList
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.MemberDocumentsList] = kwargs.pop("cls", None)

        _request = build_member_documents_list_member_documents_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.MemberDocumentsList, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_member_document(
        self, document_id: str, **kwargs: Any
    ) -> _models3.GetMemberDocumentResponse:
        """Gets member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :return: GetMemberDocumentResponse. The GetMemberDocumentResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.models.GetMemberDocumentResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models3.GetMemberDocumentResponse] = kwargs.pop("cls", None)

        _request = build_member_documents_get_member_document_request(
            document_id=document_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models3.GetMemberDocumentResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def update_member_document(
        self,
        document_id: str,
        document: _models2.DocumentData,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Required.
        :type document: ~cleanroom.governance.client.models.DocumentData
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def update_member_document(
        self,
        document_id: str,
        document: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Required.
        :type document: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def update_member_document(
        self,
        document_id: str,
        document: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Required.
        :type document: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def update_member_document(  # pylint: disable=inconsistent-return-statements
        self,
        document_id: str,
        document: Union[_models2.DocumentData, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> None:
        """Updates member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Is one of the following types: DocumentData, JSON, IO[bytes] Required.
        :type document: ~cleanroom.governance.client.models.DocumentData or JSON or IO[bytes]
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(document, (IOBase, bytes)):
            _content = document
        else:
            _content = json.dumps(document, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_member_documents_update_member_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    @overload
    def propose_member_document_change(
        self,
        document_id: str,
        proposal: _models2.DocumentProposal,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes member document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Required.
        :type proposal: ~cleanroom.governance.client.models.DocumentProposal
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_member_document_change(
        self,
        document_id: str,
        proposal: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes member document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Required.
        :type proposal: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_member_document_change(
        self,
        document_id: str,
        proposal: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes member document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Required.
        :type proposal: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def propose_member_document_change(
        self,
        document_id: str,
        proposal: Union[_models2.DocumentProposal, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes member document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Is one of the following types: DocumentProposal, JSON, IO[bytes] Required.
        :type proposal: ~cleanroom.governance.client.models.DocumentProposal or JSON or IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(proposal, (IOBase, bytes)):
            _content = proposal
        else:
            _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_member_documents_propose_member_document_change_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_accept_member_document(
        self,
        document_id: str,
        vote: _models2.ProposalVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_accept_member_document(
        self,
        document_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_accept_member_document(
        self,
        document_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_accept_member_document(
        self,
        document_id: str,
        vote: Union[_models2.ProposalVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Is one of the following types: ProposalVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_member_documents_vote_accept_member_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_reject_member_document(
        self,
        document_id: str,
        vote: _models2.ProposalVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_reject_member_document(
        self,
        document_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_reject_member_document(
        self,
        document_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_reject_member_document(
        self,
        document_id: str,
        vote: Union[_models2.ProposalVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Is one of the following types: ProposalVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_member_documents_vote_reject_member_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_on_member_document(
        self,
        document_id: str,
        vote: _models2.CustomBallotVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallotVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_member_document(
        self,
        document_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_member_document(
        self,
        document_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_on_member_document(
        self,
        document_id: str,
        vote: Union[_models2.CustomBallotVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on member document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Is one of the following types: CustomBallotVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallotVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_member_documents_vote_on_member_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def get_member_document_with_attestation(
        self,
        document_id: str,
        request: _models2.DocumentRetrievalRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves member document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.DocumentRetrievalRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_member_document_with_attestation(
        self,
        document_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves member document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_member_document_with_attestation(
        self,
        document_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves member document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def get_member_document_with_attestation(
        self,
        document_id: str,
        request: Union[_models2.DocumentRetrievalRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves member document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Is one of the following types: DocumentRetrievalRequest, JSON, IO[bytes]
         Required.
        :type request: ~cleanroom.governance.client.models.DocumentRetrievalRequest or JSON or
         IO[bytes]
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.EncryptedResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_member_documents_get_member_document_with_attestation_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.EncryptedResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class MembersOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`members` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_members(self, **kwargs: Any) -> _models2.MembersResponse:
        """Lists all members. Works in both modes.

        :return: MembersResponse. The MembersResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.MembersResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.MembersResponse] = kwargs.pop("cls", None)

        _request = build_members_list_members_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.MembersResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def update_state_digest(self, **kwargs: Any) -> _models2.StateDigestResponse:
        """Updates state digest. Token mode only.

        :return: StateDigestResponse. The StateDigestResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.StateDigestResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.StateDigestResponse] = kwargs.pop("cls", None)

        _request = build_members_update_state_digest_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.StateDigestResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def acknowledge_state_digest(self, **kwargs: Any) -> str:
        """Acknowledges state digest. Token mode only.

        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[str] = kwargs.pop("cls", None)

        _request = build_members_acknowledge_state_digest_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(str, response.text())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class NetworkOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`network` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def show_network(self, **kwargs: Any) -> _models2.NetworkInfoResponse:
        """Shows network information. Token mode only.

        :return: NetworkInfoResponse. The NetworkInfoResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.NetworkInfoResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.NetworkInfoResponse] = kwargs.pop("cls", None)

        _request = build_network_show_network_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.NetworkInfoResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class NodeOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`node` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def check_app_ready(
        self, **kwargs: Any
    ) -> None:  # pylint: disable=inconsistent-return-statements
        """Checks if application is ready. Token mode only.

        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_node_check_app_ready_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    def check_gov_ready(
        self, **kwargs: Any
    ) -> None:  # pylint: disable=inconsistent-return-statements
        """Checks if governance is ready. Token mode only.

        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_node_check_gov_ready_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore


class OAuthOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`oauth` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def get_token_subject_policy(
        self, contract_id: str, subject_name: str, **kwargs: Any
    ) -> _models2.TokenSubjectPolicyResponse:
        """Gets OAuth token subject policy. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param subject_name: Required.
        :type subject_name: str
        :return: TokenSubjectPolicyResponse. The TokenSubjectPolicyResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.TokenSubjectPolicyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.TokenSubjectPolicyResponse] = kwargs.pop("cls", None)

        _request = build_oauth_get_token_subject_policy_request(
            contract_id=contract_id,
            subject_name=subject_name,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.TokenSubjectPolicyResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class OAuthAttestationOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`oauth_attestation` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def get_token(
        self,
        request: _models2.TokenRequest,
        *,
        sub: str,
        tenant_id: str,
        aud: str,
        iss: Optional[str] = None,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Gets OAuth token with attestation. Attestation mode only. Requires public key and attestation
        report. Response contains encrypted JWT token.

        :param request: Required.
        :type request: ~cleanroom.governance.client.models.TokenRequest
        :keyword sub: Required.
        :paramtype sub: str
        :keyword tenant_id: Required.
        :paramtype tenant_id: str
        :keyword aud: Required.
        :paramtype aud: str
        :keyword iss: Default value is None.
        :paramtype iss: str
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_token(
        self,
        request: JSON,
        *,
        sub: str,
        tenant_id: str,
        aud: str,
        iss: Optional[str] = None,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Gets OAuth token with attestation. Attestation mode only. Requires public key and attestation
        report. Response contains encrypted JWT token.

        :param request: Required.
        :type request: JSON
        :keyword sub: Required.
        :paramtype sub: str
        :keyword tenant_id: Required.
        :paramtype tenant_id: str
        :keyword aud: Required.
        :paramtype aud: str
        :keyword iss: Default value is None.
        :paramtype iss: str
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_token(
        self,
        request: IO[bytes],
        *,
        sub: str,
        tenant_id: str,
        aud: str,
        iss: Optional[str] = None,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Gets OAuth token with attestation. Attestation mode only. Requires public key and attestation
        report. Response contains encrypted JWT token.

        :param request: Required.
        :type request: IO[bytes]
        :keyword sub: Required.
        :paramtype sub: str
        :keyword tenant_id: Required.
        :paramtype tenant_id: str
        :keyword aud: Required.
        :paramtype aud: str
        :keyword iss: Default value is None.
        :paramtype iss: str
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def get_token(
        self,
        request: Union[_models2.TokenRequest, JSON, IO[bytes]],
        *,
        sub: str,
        tenant_id: str,
        aud: str,
        iss: Optional[str] = None,
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Gets OAuth token with attestation. Attestation mode only. Requires public key and attestation
        report. Response contains encrypted JWT token.

        :param request: Is one of the following types: TokenRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.TokenRequest or JSON or IO[bytes]
        :keyword sub: Required.
        :paramtype sub: str
        :keyword tenant_id: Required.
        :paramtype tenant_id: str
        :keyword aud: Required.
        :paramtype aud: str
        :keyword iss: Default value is None.
        :paramtype iss: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.EncryptedResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_oauth_attestation_get_token_request(
            sub=sub,
            tenant_id=tenant_id,
            aud=aud,
            iss=iss,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.EncryptedResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class OIDCOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`oidc` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def generate_issuer_signing_key(self, **kwargs: Any) -> _models2.SigningKeyResponse:
        """Generates OIDC signing key. Token mode only.

        :return: SigningKeyResponse. The SigningKeyResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SigningKeyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.SigningKeyResponse] = kwargs.pop("cls", None)

        _request = build_oidc_generate_issuer_signing_key_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.SigningKeyResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def set_issuer_url(
        self,
        config: _models2.IssuerUrlConfig,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets OIDC issuer URL. Token mode only.

        :param config: Required.
        :type config: ~cleanroom.governance.client.models.IssuerUrlConfig
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def set_issuer_url(
        self, config: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> None:
        """Sets OIDC issuer URL. Token mode only.

        :param config: Required.
        :type config: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def set_issuer_url(
        self,
        config: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets OIDC issuer URL. Token mode only.

        :param config: Required.
        :type config: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def set_issuer_url(  # pylint: disable=inconsistent-return-statements
        self, config: Union[_models2.IssuerUrlConfig, JSON, IO[bytes]], **kwargs: Any
    ) -> None:
        """Sets OIDC issuer URL. Token mode only.

        :param config: Is one of the following types: IssuerUrlConfig, JSON, IO[bytes] Required.
        :type config: ~cleanroom.governance.client.models.IssuerUrlConfig or JSON or IO[bytes]
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(config, (IOBase, bytes)):
            _content = config
        else:
            _content = json.dumps(config, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_oidc_set_issuer_url_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    def get_issuer_info(self, **kwargs: Any) -> _models2.IssuerInfoResponse:
        """Gets OIDC issuer information. Token mode only.

        :return: IssuerInfoResponse. The IssuerInfoResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.IssuerInfoResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.IssuerInfoResponse] = kwargs.pop("cls", None)

        _request = build_oidc_get_issuer_info_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.IssuerInfoResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class ProposalsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`proposals` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_proposals(self, **kwargs: Any) -> _models2.ProposalsListResponse:
        """Lists all proposals. Token mode only.

        :return: ProposalsListResponse. The ProposalsListResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalsListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalsListResponse] = kwargs.pop("cls", None)

        _request = build_proposals_list_proposals_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalsListResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.ProposalDetailsResponse:
        """Gets proposal details. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: ProposalDetailsResponse. The ProposalDetailsResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalDetailsResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalDetailsResponse] = kwargs.pop("cls", None)

        _request = build_proposals_get_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.ProposalDetailsResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_proposal_votes(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.VotesListResponse:
        """Gets proposal votes. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: VotesListResponse. The VotesListResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VotesListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.VotesListResponse] = kwargs.pop("cls", None)

        _request = build_proposals_get_proposal_votes_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VotesListResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_proposal_historical(
        self, proposal_id: str, *, query_string: Optional[str] = None, **kwargs: Any
    ) -> _models2.ProposalHistoricalResponse:
        """Gets proposal history. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :keyword query_string: Default value is None.
        :paramtype query_string: str
        :return: ProposalHistoricalResponse. The ProposalHistoricalResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalHistoricalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalHistoricalResponse] = kwargs.pop("cls", None)

        _request = build_proposals_get_proposal_historical_request(
            proposal_id=proposal_id,
            query_string=query_string,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.ProposalHistoricalResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_proposal_actions(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.ProposalActionsResponse:
        """Gets proposal actions. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: ProposalActionsResponse. The ProposalActionsResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalActionsResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalActionsResponse] = kwargs.pop("cls", None)

        _request = build_proposals_get_proposal_actions_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.ProposalActionsResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def withdraw_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.WithdrawResponse:
        """Withdraws a proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: WithdrawResponse. The WithdrawResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.WithdrawResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.WithdrawResponse] = kwargs.pop("cls", None)

        _request = build_proposals_withdraw_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.WithdrawResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def create_proposal(
        self,
        proposal: _models2.CreateProposalRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Creates a new proposal. Token mode only.

        :param proposal: Required.
        :type proposal: ~cleanroom.governance.client.models.CreateProposalRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def create_proposal(
        self, proposal: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Creates a new proposal. Token mode only.

        :param proposal: Required.
        :type proposal: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def create_proposal(
        self,
        proposal: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Creates a new proposal. Token mode only.

        :param proposal: Required.
        :type proposal: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def create_proposal(
        self,
        proposal: Union[_models2.CreateProposalRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Creates a new proposal. Token mode only.

        :param proposal: Is one of the following types: CreateProposalRequest, JSON, IO[bytes]
         Required.
        :type proposal: ~cleanroom.governance.client.models.CreateProposalRequest or JSON or IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(proposal, (IOBase, bytes)):
            _content = proposal
        else:
            _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_proposals_create_proposal_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def vote_accept_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.VoteResponse:
        """Votes to accept proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        _request = build_proposals_vote_accept_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def vote_reject_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.VoteResponse:
        """Votes to reject proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        _request = build_proposals_vote_reject_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_on_proposal(
        self,
        proposal_id: str,
        vote: _models2.CustomBallot,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallot
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_proposal(
        self,
        proposal_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_proposal(
        self,
        proposal_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_on_proposal(
        self,
        proposal_id: str,
        vote: Union[_models2.CustomBallot, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :param vote: Is one of the following types: CustomBallot, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallot or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_proposals_vote_on_proposal_request(
            proposal_id=proposal_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class RuntimeOptionsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`runtime_options` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def propose_runtime_option(
        self, option: str, action: Literal["enable", "disable"], **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Proposes runtime option change. Token mode only.

        :param option: Required.
        :type option: str
        :param action: Is either a Literal["enable"] type or a Literal["disable"] type. Required.
        :type action: str or str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        _request = build_runtime_options_propose_runtime_option_request(
            option=option,
            action=action,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def check_runtime_option_status(
        self, option: str, **kwargs: Any
    ) -> _models2.RuntimeOptionStatusResponse:
        """Checks runtime option status. Works in both modes.

        :param option: Required.
        :type option: str
        :return: RuntimeOptionStatusResponse. The RuntimeOptionStatusResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.RuntimeOptionStatusResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.RuntimeOptionStatusResponse] = kwargs.pop("cls", None)

        _request = build_runtime_options_check_runtime_option_status_request(
            option=option,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.RuntimeOptionStatusResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class SecretsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`secrets` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_secrets(
        self, contract_id: str, **kwargs: Any
    ) -> _models2.SecretsListResponse:
        """Lists secrets. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :return: SecretsListResponse. The SecretsListResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretsListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.SecretsListResponse] = kwargs.pop("cls", None)

        _request = build_secrets_list_secrets_request(
            contract_id=contract_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.SecretsListResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def store_secret(
        self,
        contract_id: str,
        secret_name: str,
        secret: _models2.SecretData,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param secret_name: Required.
        :type secret_name: str
        :param secret: Required.
        :type secret: ~cleanroom.governance.client.models.SecretData
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def store_secret(
        self,
        contract_id: str,
        secret_name: str,
        secret: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param secret_name: Required.
        :type secret_name: str
        :param secret: Required.
        :type secret: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def store_secret(
        self,
        contract_id: str,
        secret_name: str,
        secret: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param secret_name: Required.
        :type secret_name: str
        :param secret: Required.
        :type secret: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def store_secret(
        self,
        contract_id: str,
        secret_name: str,
        secret: Union[_models2.SecretData, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param secret_name: Required.
        :type secret_name: str
        :param secret: Is one of the following types: SecretData, JSON, IO[bytes] Required.
        :type secret: ~cleanroom.governance.client.models.SecretData or JSON or IO[bytes]
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.SecretStoreResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(secret, (IOBase, bytes)):
            _content = secret
        else:
            _content = json.dumps(secret, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_secrets_store_secret_request(
            contract_id=contract_id,
            secret_name=secret_name,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.SecretStoreResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_secret_policy(
        self, contract_id: str, secret_id: str, **kwargs: Any
    ) -> _models2.SecretPolicyResponse:
        """Gets secret policy. Token mode only.

        :param contract_id: Required.
        :type contract_id: str
        :param secret_id: Required.
        :type secret_id: str
        :return: SecretPolicyResponse. The SecretPolicyResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretPolicyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.SecretPolicyResponse] = kwargs.pop("cls", None)

        _request = build_secrets_get_secret_policy_request(
            contract_id=contract_id,
            secret_id=secret_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.SecretPolicyResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class SecretsAttestationOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`secrets_attestation` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def get_secret(
        self,
        secret_id: str,
        request: _models2.SecretRetrievalRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves secret with attestation. Attestation mode only. Requires public key and attestation
        report. Response is encrypted with provided public key.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.SecretRetrievalRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_secret(
        self,
        secret_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves secret with attestation. Attestation mode only. Requires public key and attestation
        report. Response is encrypted with provided public key.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_secret(
        self,
        secret_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves secret with attestation. Attestation mode only. Requires public key and attestation
        report. Response is encrypted with provided public key.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def get_secret(
        self,
        secret_id: str,
        request: Union[_models2.SecretRetrievalRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves secret with attestation. Attestation mode only. Requires public key and attestation
        report. Response is encrypted with provided public key.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Is one of the following types: SecretRetrievalRequest, JSON, IO[bytes]
         Required.
        :type request: ~cleanroom.governance.client.models.SecretRetrievalRequest or JSON or IO[bytes]
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.EncryptedResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_secrets_attestation_get_secret_request(
            secret_id=secret_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.EncryptedResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def store_secret_with_attestation(
        self,
        secret_name: str,
        request: _models2.AttestedRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param secret_name: Required.
        :type secret_name: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def store_secret_with_attestation(
        self,
        secret_name: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param secret_name: Required.
        :type secret_name: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def store_secret_with_attestation(
        self,
        secret_name: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param secret_name: Required.
        :type secret_name: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def store_secret_with_attestation(
        self,
        secret_name: str,
        request: Union[_models2.AttestedRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.SecretStoreResponse:
        """Stores a secret with attestation. Attestation mode only. Request must be signed and include SNP
        attestation report.

        :param secret_name: Required.
        :type secret_name: str
        :param request: Is one of the following types: AttestedRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest or JSON or IO[bytes]
        :return: SecretStoreResponse. The SecretStoreResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.SecretStoreResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.SecretStoreResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_secrets_attestation_store_secret_with_attestation_request(
            secret_name=secret_name,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.SecretStoreResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def set_secret_policy(
        self,
        secret_id: str,
        request: _models2.AttestedRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets clean room policy for secret with attestation. Attestation mode only. Request must be
        signed and include SNP attestation report.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def set_secret_policy(
        self,
        secret_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets clean room policy for secret with attestation. Attestation mode only. Request must be
        signed and include SNP attestation report.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def set_secret_policy(
        self,
        secret_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Sets clean room policy for secret with attestation. Attestation mode only. Request must be
        signed and include SNP attestation report.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def set_secret_policy(  # pylint: disable=inconsistent-return-statements
        self,
        secret_id: str,
        request: Union[_models2.AttestedRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> None:
        """Sets clean room policy for secret with attestation. Attestation mode only. Request must be
        signed and include SNP attestation report.

        :param secret_id: Required.
        :type secret_id: str
        :param request: Is one of the following types: AttestedRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestedRequest or JSON or IO[bytes]
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_secrets_attestation_set_secret_policy_request(
            secret_id=secret_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore


class UpdatesOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`updates` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def check_updates(self, **kwargs: Any) -> _models2.CheckUpdateResponse:
        """Checks for available updates. Token mode only.

        :return: CheckUpdateResponse. The CheckUpdateResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.CheckUpdateResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.CheckUpdateResponse] = kwargs.pop("cls", None)

        _request = build_updates_check_updates_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.CheckUpdateResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class UserDocumentsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`user_documents` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_user_documents(
        self, *, label_selector: Optional[str] = None, **kwargs: Any
    ) -> _models2.UserDocumentsListResponse:
        """Lists user documents. Token mode only.

        :keyword label_selector: Default value is None.
        :paramtype label_selector: str
        :return: UserDocumentsListResponse. The UserDocumentsListResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserDocumentsListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserDocumentsListResponse] = kwargs.pop("cls", None)

        _request = build_user_documents_list_user_documents_request(
            label_selector=label_selector,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.UserDocumentsListResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_user_document(
        self, document_id: str, **kwargs: Any
    ) -> _models2.UserDocumentResponse:
        """Gets user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :return: UserDocumentResponse. The UserDocumentResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserDocumentResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserDocumentResponse] = kwargs.pop("cls", None)

        _request = build_user_documents_get_user_document_request(
            document_id=document_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.UserDocumentResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def update_user_document(
        self,
        document_id: str,
        document: _models3.PutUserDocumentRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Required.
        :type document: ~cleanroom.governance.models.PutUserDocumentRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def update_user_document(
        self,
        document_id: str,
        document: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Required.
        :type document: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def update_user_document(
        self,
        document_id: str,
        document: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> None:
        """Updates user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Required.
        :type document: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def update_user_document(  # pylint: disable=inconsistent-return-statements
        self,
        document_id: str,
        document: Union[_models3.PutUserDocumentRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> None:
        """Updates user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param document: Is one of the following types: PutUserDocumentRequest, JSON, IO[bytes]
         Required.
        :type document: ~cleanroom.governance.models.PutUserDocumentRequest or JSON or IO[bytes]
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[None] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(document, (IOBase, bytes)):
            _content = document
        else:
            _content = json.dumps(document, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_update_user_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    @overload
    def propose_user_document_change(
        self,
        document_id: str,
        proposal: _models2.UserDocumentProposal,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes user document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Required.
        :type proposal: ~cleanroom.governance.client.models.UserDocumentProposal
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_user_document_change(
        self,
        document_id: str,
        proposal: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes user document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Required.
        :type proposal: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_user_document_change(
        self,
        document_id: str,
        proposal: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes user document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Required.
        :type proposal: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def propose_user_document_change(
        self,
        document_id: str,
        proposal: Union[_models2.UserDocumentProposal, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Proposes user document change. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param proposal: Is one of the following types: UserDocumentProposal, JSON, IO[bytes] Required.
        :type proposal: ~cleanroom.governance.client.models.UserDocumentProposal or JSON or IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(proposal, (IOBase, bytes)):
            _content = proposal
        else:
            _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_propose_user_document_change_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_accept_user_document(
        self,
        document_id: str,
        vote: _models2.ProposalVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_accept_user_document(
        self,
        document_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_accept_user_document(
        self,
        document_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_accept_user_document(
        self,
        document_id: str,
        vote: Union[_models2.ProposalVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to accept user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Is one of the following types: ProposalVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_vote_accept_user_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_reject_user_document(
        self,
        document_id: str,
        vote: _models2.ProposalVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_reject_user_document(
        self,
        document_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_reject_user_document(
        self,
        document_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_reject_user_document(
        self,
        document_id: str,
        vote: Union[_models2.ProposalVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Votes to reject user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Is one of the following types: ProposalVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.ProposalVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_vote_reject_user_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def vote_on_user_document(
        self,
        document_id: str,
        vote: _models2.CustomBallotVote,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallotVote
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_user_document(
        self,
        document_id: str,
        vote: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def vote_on_user_document(
        self,
        document_id: str,
        vote: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Required.
        :type vote: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def vote_on_user_document(
        self,
        document_id: str,
        vote: Union[_models2.CustomBallotVote, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.VoteResponse:
        """Casts custom ballot on user document. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param vote: Is one of the following types: CustomBallotVote, JSON, IO[bytes] Required.
        :type vote: ~cleanroom.governance.client.models.CustomBallotVote or JSON or IO[bytes]
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(vote, (IOBase, bytes)):
            _content = vote
        else:
            _content = json.dumps(vote, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_vote_on_user_document_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def enable_user_document_runtime_option(  # pylint: disable=inconsistent-return-statements
        self, document_id: str, runtime_option: str, **kwargs: Any
    ) -> None:
        """Enables user document runtime option. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param runtime_option: Required.
        :type runtime_option: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_user_documents_enable_user_document_runtime_option_request(
            document_id=document_id,
            runtime_option=runtime_option,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    def disable_user_document_runtime_option(  # pylint: disable=inconsistent-return-statements
        self, document_id: str, runtime_option: str, **kwargs: Any
    ) -> None:
        """Disables user document runtime option. Token mode only.

        :param document_id: Required.
        :type document_id: str
        :param runtime_option: Required.
        :type runtime_option: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_user_documents_disable_user_document_runtime_option_request(
            document_id=document_id,
            runtime_option=runtime_option,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    def check_user_document_runtime_option_status(  # pylint: disable=name-too-long
        self, document_id: str, runtime_option: str, **kwargs: Any
    ) -> _models2.RuntimeOptionStatusResponse:
        """Checks user document runtime option status. Works in both modes.

        :param document_id: Required.
        :type document_id: str
        :param runtime_option: Required.
        :type runtime_option: str
        :return: RuntimeOptionStatusResponse. The RuntimeOptionStatusResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.RuntimeOptionStatusResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.RuntimeOptionStatusResponse] = kwargs.pop("cls", None)

        _request = (
            build_user_documents_check_user_document_runtime_option_status_request(
                document_id=document_id,
                runtime_option=runtime_option,
                headers=_headers,
                params=_params,
            )
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.RuntimeOptionStatusResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def get_user_document_with_attestation(
        self,
        document_id: str,
        request: _models2.DocumentRetrievalRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves user document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.DocumentRetrievalRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_user_document_with_attestation(
        self,
        document_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves user document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def get_user_document_with_attestation(
        self,
        document_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves user document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def get_user_document_with_attestation(
        self,
        document_id: str,
        request: Union[_models2.DocumentRetrievalRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.EncryptedResponse:
        """Retrieves user document with attestation. Attestation mode only. Requires public key and
        attestation report. Response is encrypted with provided public key.

        :param document_id: Required.
        :type document_id: str
        :param request: Is one of the following types: DocumentRetrievalRequest, JSON, IO[bytes]
         Required.
        :type request: ~cleanroom.governance.client.models.DocumentRetrievalRequest or JSON or
         IO[bytes]
        :return: EncryptedResponse. The EncryptedResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.EncryptedResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.EncryptedResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_get_user_document_with_attestation_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.EncryptedResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def check_execution_consent_attestation(
        self,
        document_id: str,
        request: _models2.AttestationRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document execution consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_execution_consent_attestation(
        self,
        document_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document execution consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_execution_consent_attestation(
        self,
        document_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document execution consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def check_execution_consent_attestation(
        self,
        document_id: str,
        request: Union[_models2.AttestationRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document execution consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Is one of the following types: AttestationRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest or JSON or IO[bytes]
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ConsentCheckResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_check_execution_consent_attestation_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ConsentCheckResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def check_telemetry_consent_attestation(
        self,
        document_id: str,
        request: _models2.AttestationRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document telemetry consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_telemetry_consent_attestation(
        self,
        document_id: str,
        request: JSON,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document telemetry consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def check_telemetry_consent_attestation(
        self,
        document_id: str,
        request: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document telemetry consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Required.
        :type request: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def check_telemetry_consent_attestation(
        self,
        document_id: str,
        request: Union[_models2.AttestationRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ConsentCheckResponse:
        """Checks user document telemetry consent with attestation. Attestation mode only. Request must
        include SNP attestation report.

        :param document_id: Required.
        :type document_id: str
        :param request: Is one of the following types: AttestationRequest, JSON, IO[bytes] Required.
        :type request: ~cleanroom.governance.client.models.AttestationRequest or JSON or IO[bytes]
        :return: ConsentCheckResponse. The ConsentCheckResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ConsentCheckResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ConsentCheckResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(request, (IOBase, bytes)):
            _content = request
        else:
            _content = json.dumps(request, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_documents_check_telemetry_consent_attestation_request(
            document_id=document_id,
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ConsentCheckResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class UserIdentitiesOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`user_identities` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_user_identities(self, **kwargs: Any) -> _models2.UserIdentitiesResponse:
        """Lists user identities. Token mode only.

        :return: UserIdentitiesResponse. The UserIdentitiesResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserIdentitiesResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserIdentitiesResponse] = kwargs.pop("cls", None)

        _request = build_user_identities_list_user_identities_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.UserIdentitiesResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_user_identity(
        self, identity_id: str, **kwargs: Any
    ) -> _models2.UserIdentityResponse:
        """Gets user identity. Token mode only.

        :param identity_id: Required.
        :type identity_id: str
        :return: UserIdentityResponse. The UserIdentityResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserIdentityResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserIdentityResponse] = kwargs.pop("cls", None)

        _request = build_user_identities_get_user_identity_request(
            identity_id=identity_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.UserIdentityResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def add_user_identity(
        self,
        identity: _models2.AddUserIdentityRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Adds user identity. Token mode only.

        :param identity: Required.
        :type identity: ~cleanroom.governance.client.models.AddUserIdentityRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def add_user_identity(
        self, identity: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Adds user identity. Token mode only.

        :param identity: Required.
        :type identity: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def add_user_identity(
        self,
        identity: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Adds user identity. Token mode only.

        :param identity: Required.
        :type identity: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def add_user_identity(
        self,
        identity: Union[_models2.AddUserIdentityRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Adds user identity. Token mode only.

        :param identity: Is one of the following types: AddUserIdentityRequest, JSON, IO[bytes]
         Required.
        :type identity: ~cleanroom.governance.client.models.AddUserIdentityRequest or JSON or IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(identity, (IOBase, bytes)):
            _content = identity
        else:
            _content = json.dumps(identity, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_identities_add_user_identity_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class UserInvitationsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`user_invitations` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_user_invitations(self, **kwargs: Any) -> _models2.UserInvitationsResponse:
        """Lists user invitations. Token mode only.

        :return: UserInvitationsResponse. The UserInvitationsResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserInvitationsResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserInvitationsResponse] = kwargs.pop("cls", None)

        _request = build_user_invitations_list_user_invitations_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.UserInvitationsResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_user_invitation(
        self, invitation_id: str, **kwargs: Any
    ) -> _models2.UserInvitationResponse:
        """Gets user invitation. Token mode only.

        :param invitation_id: Required.
        :type invitation_id: str
        :return: UserInvitationResponse. The UserInvitationResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserInvitationResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserInvitationResponse] = kwargs.pop("cls", None)

        _request = build_user_invitations_get_user_invitation_request(
            invitation_id=invitation_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.UserInvitationResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def accept_invitation(  # pylint: disable=inconsistent-return-statements
        self, invitation_id: str, **kwargs: Any
    ) -> None:
        """Accepts invitation. Token mode only.

        :param invitation_id: Required.
        :type invitation_id: str
        :return: None
        :rtype: None
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[None] = kwargs.pop("cls", None)

        _request = build_user_invitations_accept_invitation_request(
            invitation_id=invitation_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = False
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [204]:
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if cls:
            return cls(pipeline_response, None, {})  # type: ignore

    @overload
    def propose_invitation(
        self,
        invitation: _models2.InvitationProposalInput,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.InvitationProposalResponse:
        """Proposes invitation. Token mode only.

        :param invitation: Required.
        :type invitation: ~cleanroom.governance.client.models.InvitationProposalInput
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: InvitationProposalResponse. The InvitationProposalResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.InvitationProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_invitation(
        self, invitation: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.InvitationProposalResponse:
        """Proposes invitation. Token mode only.

        :param invitation: Required.
        :type invitation: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: InvitationProposalResponse. The InvitationProposalResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.InvitationProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def propose_invitation(
        self,
        invitation: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.InvitationProposalResponse:
        """Proposes invitation. Token mode only.

        :param invitation: Required.
        :type invitation: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: InvitationProposalResponse. The InvitationProposalResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.InvitationProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def propose_invitation(
        self,
        invitation: Union[_models2.InvitationProposalInput, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.InvitationProposalResponse:
        """Proposes invitation. Token mode only.

        :param invitation: Is one of the following types: InvitationProposalInput, JSON, IO[bytes]
         Required.
        :type invitation: ~cleanroom.governance.client.models.InvitationProposalInput or JSON or
         IO[bytes]
        :return: InvitationProposalResponse. The InvitationProposalResponse is compatible with
         MutableMapping
        :rtype: ~cleanroom.governance.client.models.InvitationProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.InvitationProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(invitation, (IOBase, bytes)):
            _content = invitation
        else:
            _content = json.dumps(invitation, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_invitations_propose_invitation_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.InvitationProposalResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class UserProposalsOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`user_proposals` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    @overload
    def create_user_proposal(
        self,
        proposal: _models2.UserProposalRequest,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Creates user proposal. Token mode only.

        :param proposal: Required.
        :type proposal: ~cleanroom.governance.client.models.UserProposalRequest
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def create_user_proposal(
        self, proposal: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> _models2.ProposalResponse:
        """Creates user proposal. Token mode only.

        :param proposal: Required.
        :type proposal: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def create_user_proposal(
        self,
        proposal: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Creates user proposal. Token mode only.

        :param proposal: Required.
        :type proposal: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def create_user_proposal(
        self,
        proposal: Union[_models2.UserProposalRequest, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> _models2.ProposalResponse:
        """Creates user proposal. Token mode only.

        :param proposal: Is one of the following types: UserProposalRequest, JSON, IO[bytes] Required.
        :type proposal: ~cleanroom.governance.client.models.UserProposalRequest or JSON or IO[bytes]
        :return: ProposalResponse. The ProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[_models2.ProposalResponse] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(proposal, (IOBase, bytes)):
            _content = proposal
        else:
            _content = json.dumps(proposal, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_user_proposals_create_user_proposal_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_user_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.UserProposalResponse:
        """Gets user proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: UserProposalResponse. The UserProposalResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserProposalResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserProposalResponse] = kwargs.pop("cls", None)

        _request = build_user_proposals_get_user_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.UserProposalResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_user_proposal_status(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.ProposalStatusResponse:
        """Gets user proposal status. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: ProposalStatusResponse. The ProposalStatusResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ProposalStatusResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ProposalStatusResponse] = kwargs.pop("cls", None)

        _request = build_user_proposals_get_user_proposal_status_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                _models2.ProposalStatusResponse, response.json()
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def withdraw_user_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.WithdrawResponse:
        """Withdraws user proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: WithdrawResponse. The WithdrawResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.WithdrawResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.WithdrawResponse] = kwargs.pop("cls", None)

        _request = build_user_proposals_withdraw_user_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.WithdrawResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def vote_accept_user_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.VoteResponse:
        """Votes to accept user proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        _request = build_user_proposals_vote_accept_user_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def vote_reject_user_proposal(
        self, proposal_id: str, **kwargs: Any
    ) -> _models2.VoteResponse:
        """Votes to reject user proposal. Token mode only.

        :param proposal_id: Required.
        :type proposal_id: str
        :return: VoteResponse. The VoteResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.VoteResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.VoteResponse] = kwargs.pop("cls", None)

        _request = build_user_proposals_vote_reject_user_proposal_request(
            proposal_id=proposal_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.VoteResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class UsersOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`users` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def list_users(self, **kwargs: Any) -> _models2.UsersListResponse:
        """Lists users. Token mode only.

        :return: UsersListResponse. The UsersListResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UsersListResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UsersListResponse] = kwargs.pop("cls", None)

        _request = build_users_list_users_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.UsersListResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_user(self, user_id: str, **kwargs: Any) -> _models2.UserResponse:
        """Gets user details. Token mode only.

        :param user_id: Required.
        :type user_id: str
        :return: UserResponse. The UserResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserResponse] = kwargs.pop("cls", None)

        _request = build_users_get_user_request(
            user_id=user_id,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.UserResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def is_user_active(
        self, *, authorization: str, **kwargs: Any
    ) -> _models2.UserActiveResponse:
        """Checks if user is active using JWT token from Authorization header. Works in both modes.

        :keyword authorization: Required.
        :paramtype authorization: str
        :return: UserActiveResponse. The UserActiveResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.UserActiveResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.UserActiveResponse] = kwargs.pop("cls", None)

        _request = build_users_is_user_active_request(
            authorization=authorization,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.UserActiveResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore


class WorkspaceOperations:
    """
    .. warning::
        **DO NOT** instantiate this class directly.

        Instead, you should access the following operations through
        :class:`~cleanroom.governance.client.proxy.ProxyClient`'s
        :attr:`workspace` attribute.
    """

    def __init__(self, *args, **kwargs) -> None:
        input_args = list(args)
        self._client: PipelineClient = (
            input_args.pop(0) if input_args else kwargs.pop("client")
        )
        self._config: ProxyClientConfiguration = (
            input_args.pop(0) if input_args else kwargs.pop("config")
        )
        self._serialize: Serializer = (
            input_args.pop(0) if input_args else kwargs.pop("serializer")
        )
        self._deserialize: Deserializer = (
            input_args.pop(0) if input_args else kwargs.pop("deserializer")
        )

    def check_ready(self, **kwargs: Any) -> _models2.ReadyResponse:
        """Checks service readiness. Works in both modes.

        :return: ReadyResponse. The ReadyResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.ReadyResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.ReadyResponse] = kwargs.pop("cls", None)

        _request = build_workspace_check_ready_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.ReadyResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    @overload
    def configure(
        self,
        config: _models2.WorkspaceConfigurationModel,
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> str:
        """Configures workspace. Token mode only.

        :param config: Required.
        :type config: ~cleanroom.governance.client.models.WorkspaceConfigurationModel
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def configure(
        self, config: JSON, *, content_type: str = "application/json", **kwargs: Any
    ) -> str:
        """Configures workspace. Token mode only.

        :param config: Required.
        :type config: JSON
        :keyword content_type: Body Parameter content-type. Content type parameter for JSON body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    @overload
    def configure(
        self,
        config: IO[bytes],
        *,
        content_type: str = "application/json",
        **kwargs: Any,
    ) -> str:
        """Configures workspace. Token mode only.

        :param config: Required.
        :type config: IO[bytes]
        :keyword content_type: Body Parameter content-type. Content type parameter for binary body.
         Default value is "application/json".
        :paramtype content_type: str
        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """

    def configure(
        self,
        config: Union[_models2.WorkspaceConfigurationModel, JSON, IO[bytes]],
        **kwargs: Any,
    ) -> str:
        """Configures workspace. Token mode only.

        :param config: Is one of the following types: WorkspaceConfigurationModel, JSON, IO[bytes]
         Required.
        :type config: ~cleanroom.governance.client.models.WorkspaceConfigurationModel or JSON or
         IO[bytes]
        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = case_insensitive_dict(kwargs.pop("headers", {}) or {})
        _params = kwargs.pop("params", {}) or {}

        content_type: Optional[str] = kwargs.pop(
            "content_type", _headers.pop("Content-Type", None)
        )
        cls: ClsType[str] = kwargs.pop("cls", None)

        content_type = content_type or "application/json"
        _content = None
        if isinstance(config, (IOBase, bytes)):
            _content = config
        else:
            _content = json.dumps(config, cls=SdkJSONEncoder, exclude_readonly=True)  # type: ignore

        _request = build_workspace_configure_request(
            content_type=content_type,
            content=_content,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(str, response.text())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_access_token(self, **kwargs: Any) -> _models2.AccessTokenResponse:
        """Gets identity access token. Token mode only.

        :return: AccessTokenResponse. The AccessTokenResponse is compatible with MutableMapping
        :rtype: ~cleanroom.governance.client.models.AccessTokenResponse
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[_models2.AccessTokenResponse] = kwargs.pop("cls", None)

        _request = build_workspace_get_access_token_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(_models2.AccessTokenResponse, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def show_configuration(
        self, *, signing_key: Optional[bool] = None, **kwargs: Any
    ) -> Union[
        _models2.WorkspaceConfiguration, _models1.SidecarWorkspaceConfigurationModel
    ]:
        """Shows workspace configuration. Works in both modes (returns mode-specific config).

        :keyword signing_key: Default value is None.
        :paramtype signing_key: bool
        :return: WorkspaceConfiguration or SidecarWorkspaceConfigurationModel
        :rtype: ~cleanroom.governance.client.models.WorkspaceConfiguration or
         ~cleanroom.governance.client.proxy.models.SidecarWorkspaceConfigurationModel
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[
            Union[
                _models2.WorkspaceConfiguration,
                _models1.SidecarWorkspaceConfigurationModel,
            ]
        ] = kwargs.pop("cls", None)

        _request = build_workspace_show_configuration_request(
            signing_key=signing_key,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(
                Union[
                    _models2.WorkspaceConfiguration,
                    _models1.SidecarWorkspaceConfigurationModel,
                ],
                response.json(),
            )

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_constitution(self, **kwargs: Any) -> str:
        """Gets constitution. Token mode only.

        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[str] = kwargs.pop("cls", None)

        _request = build_workspace_get_constitution_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(str, response.text())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_service_info(self, **kwargs: Any) -> str:
        """Gets service information. Token mode only.

        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[str] = kwargs.pop("cls", None)

        _request = build_workspace_get_service_info_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(str, response.text())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_js_app_endpoints(self, **kwargs: Any) -> str:
        """Gets JavaScript app endpoints. Token mode only.

        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[str] = kwargs.pop("cls", None)

        _request = build_workspace_get_js_app_endpoints_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(str, response.text())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_js_app_modules(self, **kwargs: Any) -> Any:
        """Gets JavaScript app modules. Token mode only.

        :return: any
        :rtype: any
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[Any] = kwargs.pop("cls", None)

        _request = build_workspace_get_js_app_modules_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(Any, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def list_js_app_modules(self, **kwargs: Any) -> Any:
        """Lists JavaScript app modules. Token mode only.

        :return: any
        :rtype: any
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[Any] = kwargs.pop("cls", None)

        _request = build_workspace_list_js_app_modules_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(Any, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_js_app_module(self, module_name: str, **kwargs: Any) -> str:
        """Gets specific JavaScript app module. Token mode only.

        :param module_name: Required.
        :type module_name: str
        :return: str
        :rtype: str
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[str] = kwargs.pop("cls", None)

        _request = build_workspace_get_js_app_module_request(
            module_name=module_name,
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(str, response.text())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore

    def get_js_app_bundle(self, **kwargs: Any) -> Any:
        """Gets JavaScript app bundle. Token mode only.

        :return: any
        :rtype: any
        :raises ~corehttp.exceptions.HttpResponseError:
        """
        error_map: MutableMapping = {
            401: ClientAuthenticationError,
            404: ResourceNotFoundError,
            409: ResourceExistsError,
            304: ResourceNotModifiedError,
        }
        error_map.update(kwargs.pop("error_map", {}) or {})

        _headers = kwargs.pop("headers", {}) or {}
        _params = kwargs.pop("params", {}) or {}

        cls: ClsType[Any] = kwargs.pop("cls", None)

        _request = build_workspace_get_js_app_bundle_request(
            headers=_headers,
            params=_params,
        )
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }
        _request.url = self._client.format_url(_request.url, **path_format_arguments)

        _stream = kwargs.pop("stream", False)
        pipeline_response: PipelineResponse = self._client.pipeline.run(
            _request, stream=_stream, **kwargs
        )

        response = pipeline_response.http_response

        if response.status_code not in [200]:
            if _stream:
                try:
                    response.read()  # Load the body in memory and close the socket
                except (StreamConsumedError, StreamClosedError):
                    pass
            map_error(
                status_code=response.status_code, response=response, error_map=error_map
            )
            raise HttpResponseError(response=response)

        if _stream:
            deserialized = response.iter_bytes()
        else:
            deserialized = _deserialize(Any, response.json())

        if cls:
            return cls(pipeline_response, deserialized, {})  # type: ignore

        return deserialized  # type: ignore
