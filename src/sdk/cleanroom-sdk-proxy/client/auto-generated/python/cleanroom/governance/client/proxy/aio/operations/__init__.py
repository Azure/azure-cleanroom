# coding=utf-8
# pylint: disable=wrong-import-position

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ._patch import *  # pylint: disable=unused-wildcard-import

from ._operations import (
    CertificateAuthorityAttestationOperations,  # type: ignore
    CertificateAuthorityOperations,  # type: ignore
    CleanRoomPolicyDelegatesAttestationOperations,  # type: ignore
    CleanRoomPolicyOperations,  # type: ignore
    ConsentCheckOperations,  # type: ignore
    ContractProposalsOperations,  # type: ignore
    ContractRuntimeOptionsOperations,  # type: ignore
    ContractsOperations,  # type: ignore
    EventsAttestationOperations,  # type: ignore
    EventsOperations,  # type: ignore
    MemberDocumentsOperations,  # type: ignore
    MembersOperations,  # type: ignore
    NetworkOperations,  # type: ignore
    NodeOperations,  # type: ignore
    OAuthAttestationOperations,  # type: ignore
    OAuthOperations,  # type: ignore
    OIDCOperations,  # type: ignore
    ProposalsOperations,  # type: ignore
    RuntimeOptionsOperations,  # type: ignore
    SecretsAttestationOperations,  # type: ignore
    SecretsOperations,  # type: ignore
    UpdatesOperations,  # type: ignore
    UserDocumentsOperations,  # type: ignore
    UserIdentitiesOperations,  # type: ignore
    UserInvitationsOperations,  # type: ignore
    UserProposalsOperations,  # type: ignore
    UsersOperations,  # type: ignore
    WorkspaceOperations,  # type: ignore
)
from ._patch import *
from ._patch import __all__ as _patch_all
from ._patch import patch_sdk as _patch_sdk

__all__ = [
    "ContractsOperations",
    "CertificateAuthorityOperations",
    "CertificateAuthorityAttestationOperations",
    "CleanRoomPolicyOperations",
    "CleanRoomPolicyDelegatesAttestationOperations",
    "ContractProposalsOperations",
    "ContractRuntimeOptionsOperations",
    "ConsentCheckOperations",
    "EventsOperations",
    "EventsAttestationOperations",
    "MemberDocumentsOperations",
    "MembersOperations",
    "NetworkOperations",
    "NodeOperations",
    "OAuthOperations",
    "OAuthAttestationOperations",
    "OIDCOperations",
    "ProposalsOperations",
    "RuntimeOptionsOperations",
    "SecretsOperations",
    "SecretsAttestationOperations",
    "UpdatesOperations",
    "UserDocumentsOperations",
    "UserIdentitiesOperations",
    "UserInvitationsOperations",
    "UserProposalsOperations",
    "UsersOperations",
    "WorkspaceOperations",
]
__all__.extend([p for p in _patch_all if p not in __all__])  # pyright: ignore
_patch_sdk()
