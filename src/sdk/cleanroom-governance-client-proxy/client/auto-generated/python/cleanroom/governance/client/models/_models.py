# pylint: disable=too-many-lines
# coding=utf-8
# pylint: disable=useless-super-delegation

from typing import TYPE_CHECKING, Any, Literal, Mapping, Optional, overload

from ..proxy._utils.model_base import Model as _Model
from ..proxy._utils.model_base import rest_field

if TYPE_CHECKING:
    from ... import models as _models2
    from .. import models as _models


class AccessTokenResponse(_Model):
    """Access token response.

    :ivar access_token: Required.
    :vartype access_token: str
    """

    access_token: str = rest_field(
        name="accessToken", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        access_token: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class AddUserIdentityRequest(_Model):
    """AddUserIdentityRequest.

    :ivar object_id:
    :vartype object_id: str
    :ivar account_type:
    :vartype account_type: str
    :ivar tenant_id:
    :vartype tenant_id: str
    :ivar identifier:
    :vartype identifier: str
    :ivar accepted_invitation_id:
    :vartype accepted_invitation_id: str
    """

    object_id: Optional[str] = rest_field(
        name="objectId", visibility=["read", "create", "update", "delete", "query"]
    )
    account_type: Optional[str] = rest_field(
        name="accountType", visibility=["read", "create", "update", "delete", "query"]
    )
    tenant_id: Optional[str] = rest_field(
        name="tenantId", visibility=["read", "create", "update", "delete", "query"]
    )
    identifier: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    accepted_invitation_id: Optional[str] = rest_field(
        name="acceptedInvitationId",
        visibility=["read", "create", "update", "delete", "query"],
    )

    @overload
    def __init__(
        self,
        *,
        object_id: Optional[str] = None,
        account_type: Optional[str] = None,
        tenant_id: Optional[str] = None,
        identifier: Optional[str] = None,
        accepted_invitation_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class AttestationRequest(_Model):
    """Base attestation request.

    :ivar attestation: SNP attestation evidence. Required.
    :vartype attestation: ~cleanroom.governance.models.SnpEvidence
    """

    attestation: "_models2.SnpEvidence" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """SNP attestation evidence. Required."""

    @overload
    def __init__(
        self,
        *,
        attestation: "_models2.SnpEvidence",
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class AttestedEventRequest(_Model):
    """Attested event request.

    :ivar timestamp: Event timestamp. Required.
    :vartype timestamp: str
    :ivar attestation: SNP attestation evidence. Required.
    :vartype attestation: ~cleanroom.governance.models.SnpEvidence
    :ivar sign: Request signature. Required.
    :vartype sign: ~cleanroom.governance.models.Sign
    :ivar data: Event data. Required.
    :vartype data: any
    """

    timestamp: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Event timestamp. Required."""
    attestation: "_models2.SnpEvidence" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """SNP attestation evidence. Required."""
    sign: "_models2.Sign" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Request signature. Required."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Event data. Required."""

    @overload
    def __init__(
        self,
        *,
        timestamp: str,
        attestation: "_models2.SnpEvidence",
        sign: "_models2.Sign",
        data: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class AttestedRequest(_Model):
    """Attested request with encryption and signature.

    :ivar attestation: SNP attestation evidence. Required.
    :vartype attestation: ~cleanroom.governance.models.SnpEvidence
    :ivar encrypt: Public key for encrypting response. Required.
    :vartype encrypt: ~cleanroom.governance.models.Encrypt
    :ivar sign: Request signature. Required.
    :vartype sign: ~cleanroom.governance.models.Sign
    :ivar data: Encrypted request data. Required.
    :vartype data: str
    """

    attestation: "_models2.SnpEvidence" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """SNP attestation evidence. Required."""
    encrypt: "_models2.Encrypt" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Public key for encrypting response. Required."""
    sign: "_models2.Sign" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Request signature. Required."""
    data: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Encrypted request data. Required."""

    @overload
    def __init__(
        self,
        *,
        attestation: "_models2.SnpEvidence",
        encrypt: "_models2.Encrypt",
        sign: "_models2.Sign",
        data: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CAInfoResponse(_Model):
    """CA information response.

    :ivar enabled: Required.
    :vartype enabled: bool
    :ivar ca_cert:
    :vartype ca_cert: str
    :ivar public_key:
    :vartype public_key: str
    :ivar proposal_ids:
    :vartype proposal_ids: list[str]
    """

    enabled: bool = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    ca_cert: Optional[str] = rest_field(
        name="caCert", visibility=["read", "create", "update", "delete", "query"]
    )
    public_key: Optional[str] = rest_field(
        name="publicKey", visibility=["read", "create", "update", "delete", "query"]
    )
    proposal_ids: Optional[list[str]] = rest_field(
        name="proposalIds", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        enabled: bool,
        ca_cert: Optional[str] = None,
        public_key: Optional[str] = None,
        proposal_ids: Optional[list[str]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CheckUpdateResponse(_Model):
    """CheckUpdateResponse.

    :ivar proposals: Required.
    :vartype proposals: list[~cleanroom.governance.client.models.PendingProposal]
    """

    proposals: list["_models.PendingProposal"] = rest_field(
        name="Proposals", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposals: list["_models.PendingProposal"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CleanRoomPolicyProposal(_Model):
    """CleanRoomPolicyProposal.

    :ivar type: Required.
    :vartype type: str
    :ivar claims: Required.
    :vartype claims: any
    :ivar contract_id:
    :vartype contract_id: str
    """

    type: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    claims: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    contract_id: Optional[str] = rest_field(
        name="contractId", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        type: str,
        claims: Any,
        contract_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CleanRoomPolicyResponse(_Model):
    """CleanRoomPolicyResponse.

    :ivar proposal_ids: Required.
    :vartype proposal_ids: list[str]
    :ivar policy: Required.
    :vartype policy: any
    """

    proposal_ids: list[str] = rest_field(
        name="proposalIds", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    policy: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_ids: list[str],
        policy: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ConsentCheckResponse(_Model):
    """Consent check response.

    :ivar result: Consent check result. Required. Is one of the following types:
     Literal["allowed"], Literal["denied"], Literal["pending"]
    :vartype result: str or str or str
    :ivar reason: Optional reason for denial.
    :vartype reason: str
    """

    result: Literal["allowed", "denied", "pending"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Consent check result. Required. Is one of the following types: Literal[\"allowed\"],
     Literal[\"denied\"], Literal[\"pending\"]"""
    reason: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Optional reason for denial."""

    @overload
    def __init__(
        self,
        *,
        result: Literal["allowed", "denied", "pending"],
        reason: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ContractProposal(_Model):
    """ContractProposal.

    :ivar version: Required.
    :vartype version: str
    :ivar data:
    :vartype data: any
    """

    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    data: Optional[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        version: str,
        data: Optional[Any] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ContractResponse(_Model):
    """ContractResponse.

    :ivar id: Required.
    :vartype id: str
    :ivar version:
    :vartype version: str
    :ivar data: Required.
    :vartype data: any
    :ivar state:
    :vartype state: str
    :ivar proposal_id:
    :vartype proposal_id: str
    :ivar final_votes:
    :vartype final_votes: list[any]
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    version: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    state: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    proposal_id: Optional[str] = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    final_votes: Optional[list[Any]] = rest_field(
        name="finalVotes", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
        data: Any,
        version: Optional[str] = None,
        state: Optional[str] = None,
        proposal_id: Optional[str] = None,
        final_votes: Optional[list[Any]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ContractsListResponse(_Model):
    """ContractsListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.ContractResponse]
    """

    value: list["_models.ContractResponse"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.ContractResponse"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CreateProposalRequest(_Model):
    """CreateProposalRequest.

    :ivar actions: Required.
    :vartype actions: list[any]
    """

    actions: list[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        actions: list[Any],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CustomBallot(_Model):
    """CustomBallot.

    :ivar ballot: Required.
    :vartype ballot: any
    """

    ballot: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        ballot: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class CustomBallotVote(_Model):
    """CustomBallotVote.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar ballot: Required.
    :vartype ballot: any
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    ballot: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        ballot: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DelegatePoliciesListResponse(_Model):
    """DelegatePoliciesListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.DelegatePolicyItem]
    """

    value: list["_models.DelegatePolicyItem"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.DelegatePolicyItem"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DelegatePolicyItem(_Model):
    """DelegatePolicyItem.

    :ivar delegate_type: Required.
    :vartype delegate_type: str
    :ivar delegate_id: Required.
    :vartype delegate_id: str
    """

    delegate_type: str = rest_field(
        name="delegateType", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    delegate_id: str = rest_field(
        name="delegateId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        delegate_type: str,
        delegate_id: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DelegatePolicyResponse(_Model):
    """DelegatePolicyResponse.

    :ivar claims: Required.
    :vartype claims: any
    """

    claims: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        claims: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DeploymentProposalResponse(_Model):
    """DeploymentProposalResponse.

    :ivar proposal_ids: Required.
    :vartype proposal_ids: list[str]
    :ivar data: Required.
    :vartype data: any
    """

    proposal_ids: list[str] = rest_field(
        name="proposalIds", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_ids: list[str],
        data: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DocumentData(_Model):
    """Document data.

    :ivar version: Required.
    :vartype version: str
    :ivar contract_id: Required.
    :vartype contract_id: str
    :ivar data: Required.
    :vartype data: any
    """

    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    contract_id: str = rest_field(
        name="contractId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        version: str,
        contract_id: str,
        data: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DocumentProposal(_Model):
    """Document proposal.

    :ivar version: Required.
    :vartype version: str
    """

    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        version: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class DocumentRetrievalRequest(_Model):
    """Document retrieval request with attestation.

    :ivar attestation: SNP attestation evidence. Required.
    :vartype attestation: ~cleanroom.governance.models.SnpEvidence
    :ivar encrypt: Public key for encrypting response. Required.
    :vartype encrypt: ~cleanroom.governance.models.Encrypt
    """

    attestation: "_models2.SnpEvidence" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """SNP attestation evidence. Required."""
    encrypt: "_models2.Encrypt" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Public key for encrypting response. Required."""

    @overload
    def __init__(
        self,
        *,
        attestation: "_models2.SnpEvidence",
        encrypt: "_models2.Encrypt",
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class EncryptedResponse(_Model):
    """Encrypted response wrapper.

    :ivar value: RSA-OAEP-AES-KWP encrypted payload. Required.
    :vartype value: str
    """

    value: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """RSA-OAEP-AES-KWP encrypted payload. Required."""

    @overload
    def __init__(
        self,
        *,
        value: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ErrorReason(_Model):
    """Error reason with code and message.

    :ivar code: Required.
    :vartype code: str
    :ivar message: Required.
    :vartype message: str
    """

    code: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    message: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        code: str,
        message: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class Event(_Model):
    """Event.

    :ivar timestamp: Required.
    :vartype timestamp: str
    :ivar timestamp_iso:
    :vartype timestamp_iso: str
    :ivar scope: Required.
    :vartype scope: str
    :ivar id: Required.
    :vartype id: str
    :ivar seqno: Required.
    :vartype seqno: int
    :ivar data: Required.
    :vartype data: any
    """

    timestamp: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    timestamp_iso: Optional[str] = rest_field(
        name="timestampIso", visibility=["read", "create", "update", "delete", "query"]
    )
    scope: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    seqno: int = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        timestamp: str,
        scope: str,
        id: str,  # pylint: disable=redefined-builtin
        seqno: int,
        data: Any,
        timestamp_iso: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class EventsResponse(_Model):
    """EventsResponse.

    :ivar next_link:
    :vartype next_link: str
    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.Event]
    """

    next_link: Optional[str] = rest_field(
        name="nextLink", visibility=["read", "create", "update", "delete", "query"]
    )
    value: list["_models.Event"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.Event"],
        next_link: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class InvitationProposalInput(_Model):
    """InvitationProposalInput.

    :ivar invitation_id:
    :vartype invitation_id: str
    :ivar user_name: Required.
    :vartype user_name: str
    :ivar tenant_id:
    :vartype tenant_id: str
    :ivar identity_type: Required. Is either a Literal["User"] type or a
     Literal["ServicePrincipal"] type.
    :vartype identity_type: str or str
    :ivar account_type: Required. Default value is "microsoft".
    :vartype account_type: str
    """

    invitation_id: Optional[str] = rest_field(
        name="InvitationId", visibility=["read", "create", "update", "delete", "query"]
    )
    user_name: str = rest_field(
        name="UserName", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    tenant_id: Optional[str] = rest_field(
        name="TenantId", visibility=["read", "create", "update", "delete", "query"]
    )
    identity_type: Literal["User", "ServicePrincipal"] = rest_field(
        name="IdentityType", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required. Is either a Literal[\"User\"] type or a Literal[\"ServicePrincipal\"] type."""
    account_type: Literal["microsoft"] = rest_field(
        name="AccountType", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required. Default value is \"microsoft\"."""

    @overload
    def __init__(
        self,
        *,
        user_name: str,
        identity_type: Literal["User", "ServicePrincipal"],
        invitation_id: Optional[str] = None,
        tenant_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self.account_type: Literal["microsoft"] = "microsoft"


class InvitationProposalResponse(_Model):
    """InvitationProposalResponse.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar invitation_id: Required.
    :vartype invitation_id: str
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    invitation_id: str = rest_field(
        name="invitationId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        invitation_id: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class IssuerInfoResponse(_Model):
    """IssuerInfoResponse.

    :ivar enabled: Required.
    :vartype enabled: bool
    :ivar issuer_url:
    :vartype issuer_url: str
    :ivar tenant_data:
    :vartype tenant_data: any
    """

    enabled: bool = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    issuer_url: Optional[str] = rest_field(
        name="issuerUrl", visibility=["read", "create", "update", "delete", "query"]
    )
    tenant_data: Optional[Any] = rest_field(
        name="tenantData", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        enabled: bool,
        issuer_url: Optional[str] = None,
        tenant_data: Optional[Any] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class IssuerUrlConfig(_Model):
    """IssuerUrlConfig.

    :ivar issuer_url: Required.
    :vartype issuer_url: str
    """

    issuer_url: str = rest_field(
        name="issuerUrl", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        issuer_url: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class Member(_Model):
    """Member information.

    :ivar member_id: Member identifier. Required.
    :vartype member_id: str
    :ivar status: Member status.
    :vartype status: str
    :ivar member_data: Additional member data.
    :vartype member_data: any
    """

    member_id: str = rest_field(
        name="memberId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Member identifier. Required."""
    status: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Member status."""
    member_data: Optional[Any] = rest_field(
        name="memberData", visibility=["read", "create", "update", "delete", "query"]
    )
    """Additional member data."""

    @overload
    def __init__(
        self,
        *,
        member_id: str,
        status: Optional[str] = None,
        member_data: Optional[Any] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class MemberDocumentItem(_Model):
    """MemberDocumentItem.

    :ivar id: Required.
    :vartype id: str
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class MemberDocumentsList(_Model):
    """MemberDocumentsList.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.MemberDocumentItem]
    """

    value: list["_models.MemberDocumentItem"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.MemberDocumentItem"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class MembersResponse(_Model):
    """Members list response.

    :ivar value: List of members. Required.
    :vartype value: list[~cleanroom.governance.client.models.Member]
    """

    value: list["_models.Member"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """List of members. Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.Member"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class NetworkInfoResponse(_Model):
    """NetworkInfoResponse.

    :ivar service: Required.
    :vartype service: any
    """

    service: Any = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        service: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class PendingProposal(_Model):
    """PendingProposal.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar action_name: Required.
    :vartype action_name: str
    """

    proposal_id: str = rest_field(
        name="ProposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    action_name: str = rest_field(
        name="ActionName", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        action_name: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalActionsResponse(_Model):
    """ProposalActionsResponse.

    :ivar actions: Required.
    :vartype actions: list[any]
    """

    actions: list[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        actions: list[Any],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalDetailsResponse(_Model):
    """ProposalDetailsResponse.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar proposal_state: Required.
    :vartype proposal_state: str
    :ivar ballot_count:
    :vartype ballot_count: int
    :ivar actions:
    :vartype actions: list[any]
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    proposal_state: str = rest_field(
        name="proposalState", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    ballot_count: Optional[int] = rest_field(
        name="ballotCount", visibility=["read", "create", "update", "delete", "query"]
    )
    actions: Optional[list[Any]] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        proposal_state: str,
        ballot_count: Optional[int] = None,
        actions: Optional[list[Any]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalHistoricalResponse(_Model):
    """ProposalHistoricalResponse.

    :ivar next_link:
    :vartype next_link: str
    :ivar value: Required.
    :vartype value: list[any]
    """

    next_link: Optional[str] = rest_field(
        name="nextLink", visibility=["read", "create", "update", "delete", "query"]
    )
    value: list[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list[Any],
        next_link: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalItem(_Model):
    """ProposalItem.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar proposal_state: Required.
    :vartype proposal_state: str
    :ivar seqno:
    :vartype seqno: int
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    proposal_state: str = rest_field(
        name="proposalState", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    seqno: Optional[int] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        proposal_state: str,
        seqno: Optional[int] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalResponse(_Model):
    """Base proposal response.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar ballot_count:
    :vartype ballot_count: int
    :ivar state:
    :vartype state: str
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    ballot_count: Optional[int] = rest_field(
        name="ballotCount", visibility=["read", "create", "update", "delete", "query"]
    )
    state: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        ballot_count: Optional[int] = None,
        state: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalsListResponse(_Model):
    """ProposalsListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.ProposalItem]
    """

    value: list["_models.ProposalItem"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.ProposalItem"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalStatusResponse(_Model):
    """ProposalStatusResponse.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar state: Required.
    :vartype state: str
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    state: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        state: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ProposalVote(_Model):
    """Proposal vote input.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ReadyResponse(_Model):
    """ReadyResponse.

    :ivar status: Required. Default value is "up".
    :vartype status: str
    """

    status: Literal["up"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required. Default value is \"up\"."""

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self.status: Literal["up"] = "up"


class RuntimeOptionStatusResponse(_Model):
    """Runtime option status response.

    :ivar status: Required.
    :vartype status: str
    :ivar reason:
    :vartype reason: ~cleanroom.governance.client.models.ErrorReason
    """

    status: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    reason: Optional["_models.ErrorReason"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        status: str,
        reason: Optional["_models.ErrorReason"] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class RuntimeStatusResponse(_Model):
    """Runtime status response.

    :ivar status: Required.
    :vartype status: str
    :ivar reason:
    :vartype reason: ~cleanroom.governance.client.models.ErrorReason
    :ivar proposal_ids:
    :vartype proposal_ids: list[str]
    """

    status: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    reason: Optional["_models.ErrorReason"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    proposal_ids: Optional[list[str]] = rest_field(
        name="proposalIds", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        status: str,
        reason: Optional["_models.ErrorReason"] = None,
        proposal_ids: Optional[list[str]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SecretData(_Model):
    """Secret data.

    :ivar value: Required.
    :vartype value: str
    """

    value: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SecretPolicyResponse(_Model):
    """SecretPolicyResponse.

    :ivar policy: Required.
    :vartype policy: any
    """

    policy: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        policy: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SecretRetrievalRequest(_Model):
    """Secret retrieval request with attestation.

    :ivar attestation: SNP attestation evidence. Required.
    :vartype attestation: ~cleanroom.governance.models.SnpEvidence
    :ivar encrypt: Public key for encrypting response. Required.
    :vartype encrypt: ~cleanroom.governance.models.Encrypt
    """

    attestation: "_models2.SnpEvidence" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """SNP attestation evidence. Required."""
    encrypt: "_models2.Encrypt" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Public key for encrypting response. Required."""

    @overload
    def __init__(
        self,
        *,
        attestation: "_models2.SnpEvidence",
        encrypt: "_models2.Encrypt",
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SecretsListResponse(_Model):
    """SecretsListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.models.ListSecretResponse]
    """

    value: list["_models2.ListSecretResponse"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models2.ListSecretResponse"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SecretStoreResponse(_Model):
    """Secret store response.

    :ivar secret_id: Required.
    :vartype secret_id: str
    """

    secret_id: str = rest_field(
        name="secretId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        secret_id: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SigningKeyResponse(_Model):
    """Signing key response.

    :ivar kid:
    :vartype kid: str
    :ivar public_key:
    :vartype public_key: str
    """

    kid: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    public_key: Optional[str] = rest_field(
        name="publicKey", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        kid: Optional[str] = None,
        public_key: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class StateDigestResponse(_Model):
    """StateDigestResponse.

    :ivar state_digest: Required.
    :vartype state_digest: str
    """

    state_digest: str = rest_field(
        name="stateDigest", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        state_digest: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class TokenRequest(_Model):
    """Token request with attestation.

    :ivar attestation: SNP attestation evidence. Required.
    :vartype attestation: ~cleanroom.governance.models.SnpEvidence
    :ivar encrypt: Public key for encrypting response. Required.
    :vartype encrypt: ~cleanroom.governance.models.Encrypt
    """

    attestation: "_models2.SnpEvidence" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """SNP attestation evidence. Required."""
    encrypt: "_models2.Encrypt" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Public key for encrypting response. Required."""

    @overload
    def __init__(
        self,
        *,
        attestation: "_models2.SnpEvidence",
        encrypt: "_models2.Encrypt",
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class TokenSubjectPolicyResponse(_Model):
    """TokenSubjectPolicyResponse.

    :ivar policy: Required.
    :vartype policy: any
    """

    policy: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        policy: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class User(_Model):
    """User.

    :ivar user_id: Required.
    :vartype user_id: str
    :ivar data:
    :vartype data: any
    """

    user_id: str = rest_field(
        name="userId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    data: Optional[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        user_id: str,
        data: Optional[Any] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserActiveResponse(_Model):
    """User active status response.

    :ivar is_active: Whether user is active. Required.
    :vartype is_active: bool
    :ivar user_id: User identifier if active.
    :vartype user_id: str
    """

    is_active: bool = rest_field(
        name="isActive", visibility=["read", "create", "update", "delete", "query"]
    )
    """Whether user is active. Required."""
    user_id: Optional[str] = rest_field(
        name="userId", visibility=["read", "create", "update", "delete", "query"]
    )
    """User identifier if active."""

    @overload
    def __init__(
        self,
        *,
        is_active: bool,
        user_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserData(_Model):
    """User data.

    :ivar tenant_id:
    :vartype tenant_id: str
    """

    tenant_id: Optional[str] = rest_field(
        name="tenantId", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        tenant_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserDocumentItem(_Model):
    """UserDocumentItem.

    :ivar id: Required.
    :vartype id: str
    :ivar labels: Required.
    :vartype labels: dict[str, str]
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    labels: dict[str, str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
        labels: dict[str, str],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserDocumentProposal(_Model):
    """UserDocumentProposal.

    :ivar version: Required.
    :vartype version: str
    """

    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        version: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserDocumentResponse(_Model):
    """UserDocumentResponse.

    :ivar id: Required.
    :vartype id: str
    :ivar contract_id: Required.
    :vartype contract_id: str
    :ivar version: Required.
    :vartype version: str
    :ivar approvers:
    :vartype approvers: list[~cleanroom.governance.models.UserProposalApprover]
    :ivar data: Required.
    :vartype data: any
    :ivar labels: Required.
    :vartype labels: dict[str, str]
    :ivar state:
    :vartype state: str
    :ivar proposal_id:
    :vartype proposal_id: str
    :ivar proposer_id:
    :vartype proposer_id: str
    :ivar final_votes:
    :vartype final_votes: list[~cleanroom.governance.client.models.UserVote]
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    contract_id: str = rest_field(
        name="contractId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    approvers: Optional[list["_models2.UserProposalApprover"]] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    labels: dict[str, str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    state: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    proposal_id: Optional[str] = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    proposer_id: Optional[str] = rest_field(
        name="proposerId", visibility=["read", "create", "update", "delete", "query"]
    )
    final_votes: Optional[list["_models.UserVote"]] = rest_field(
        name="finalVotes", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
        contract_id: str,
        version: str,
        data: Any,
        labels: dict[str, str],
        approvers: Optional[list["_models2.UserProposalApprover"]] = None,
        state: Optional[str] = None,
        proposal_id: Optional[str] = None,
        proposer_id: Optional[str] = None,
        final_votes: Optional[list["_models.UserVote"]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserDocumentsListResponse(_Model):
    """UserDocumentsListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.UserDocumentItem]
    """

    value: list["_models.UserDocumentItem"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.UserDocumentItem"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserIdentitiesResponse(_Model):
    """UserIdentitiesResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.UserIdentity]
    """

    value: list["_models.UserIdentity"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.UserIdentity"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserIdentity(_Model):
    """UserIdentity.

    :ivar id: Required.
    :vartype id: str
    :ivar account_type: Required.
    :vartype account_type: str
    :ivar invitation_id:
    :vartype invitation_id: str
    :ivar data: Required.
    :vartype data: ~cleanroom.governance.client.models.UserIdentityData
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    account_type: str = rest_field(
        name="accountType", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    invitation_id: Optional[str] = rest_field(
        name="invitationId", visibility=["read", "create", "update", "delete", "query"]
    )
    data: "_models.UserIdentityData" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
        account_type: str,
        data: "_models.UserIdentityData",
        invitation_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserIdentityData(_Model):
    """User identity data.

    :ivar tenant_id: Required.
    :vartype tenant_id: str
    :ivar identifier: Required.
    :vartype identifier: str
    """

    tenant_id: str = rest_field(
        name="tenantId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    identifier: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        tenant_id: str,
        identifier: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserIdentityResponse(_Model):
    """UserIdentityResponse.

    :ivar id: Required.
    :vartype id: str
    :ivar account_type: Required.
    :vartype account_type: str
    :ivar invitation_id:
    :vartype invitation_id: str
    :ivar data: Required.
    :vartype data: ~cleanroom.governance.client.models.UserIdentityData
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    account_type: str = rest_field(
        name="accountType", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    invitation_id: Optional[str] = rest_field(
        name="invitationId", visibility=["read", "create", "update", "delete", "query"]
    )
    data: "_models.UserIdentityData" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
        account_type: str,
        data: "_models.UserIdentityData",
        invitation_id: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserInfo(_Model):
    """User information.

    :ivar user_id: Required.
    :vartype user_id: str
    :ivar data: Required.
    :vartype data: ~cleanroom.governance.client.models.UserData
    """

    user_id: str = rest_field(
        name="userId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    data: "_models.UserData" = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        user_id: str,
        data: "_models.UserData",
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserInvitation(_Model):
    """UserInvitation.

    :ivar invitation_id: Required.
    :vartype invitation_id: str
    :ivar account_type: Required.
    :vartype account_type: str
    :ivar tenant_id:
    :vartype tenant_id: str
    :ivar claims:
    :vartype claims: any
    :ivar status:
    :vartype status: str
    :ivar user_info:
    :vartype user_info: ~cleanroom.governance.client.models.UserInfo
    """

    invitation_id: str = rest_field(
        name="invitationId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    account_type: str = rest_field(
        name="accountType", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    tenant_id: Optional[str] = rest_field(
        name="tenantId", visibility=["read", "create", "update", "delete", "query"]
    )
    claims: Optional[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    status: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    user_info: Optional["_models.UserInfo"] = rest_field(
        name="userInfo", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        invitation_id: str,
        account_type: str,
        tenant_id: Optional[str] = None,
        claims: Optional[Any] = None,
        status: Optional[str] = None,
        user_info: Optional["_models.UserInfo"] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserInvitationResponse(UserInvitation):
    """UserInvitationResponse.

    :ivar invitation_id: Required.
    :vartype invitation_id: str
    :ivar account_type: Required.
    :vartype account_type: str
    :ivar tenant_id:
    :vartype tenant_id: str
    :ivar claims:
    :vartype claims: any
    :ivar status:
    :vartype status: str
    :ivar user_info:
    :vartype user_info: ~cleanroom.governance.client.models.UserInfo
    """

    @overload
    def __init__(
        self,
        *,
        invitation_id: str,
        account_type: str,
        tenant_id: Optional[str] = None,
        claims: Optional[Any] = None,
        status: Optional[str] = None,
        user_info: Optional["_models.UserInfo"] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserInvitationsResponse(_Model):
    """UserInvitationsResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.UserInvitation]
    """

    value: list["_models.UserInvitation"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.UserInvitation"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserProposalRequest(_Model):
    """UserProposalRequest.

    :ivar actions: Required.
    :vartype actions: list[any]
    """

    actions: list[Any] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        actions: list[Any],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserProposalResponse(_Model):
    """UserProposalResponse.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar name: Required.
    :vartype name: str
    :ivar approvers: Required.
    :vartype approvers: list[~cleanroom.governance.models.UserProposalApprover]
    :ivar args: Required.
    :vartype args: any
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    name: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""
    approvers: list["_models2.UserProposalApprover"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    args: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        name: str,
        approvers: list["_models2.UserProposalApprover"],
        args: Any,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserResponse(User):
    """UserResponse.

    :ivar user_id: Required.
    :vartype user_id: str
    :ivar data:
    :vartype data: any
    """

    @overload
    def __init__(
        self,
        *,
        user_id: str,
        data: Optional[Any] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UsersListResponse(_Model):
    """UsersListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.User]
    """

    value: list["_models.User"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.User"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserVote(_Model):
    """User vote.

    :ivar approver_id: Required.
    :vartype approver_id: str
    :ivar ballot: Required.
    :vartype ballot: str
    """

    approver_id: str = rest_field(
        name="approverId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    ballot: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        approver_id: str,
        ballot: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class Vote(_Model):
    """Vote.

    :ivar member_id: Required.
    :vartype member_id: str
    :ivar vote: Required.
    :vartype vote: bool
    """

    member_id: str = rest_field(
        name="memberId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    vote: bool = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        member_id: str,
        vote: bool,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class VoteResponse(_Model):
    """Vote response.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar state:
    :vartype state: str
    :ivar ballot_count:
    :vartype ballot_count: int
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    state: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    ballot_count: Optional[int] = rest_field(
        name="ballotCount", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        state: Optional[str] = None,
        ballot_count: Optional[int] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class VotesListResponse(_Model):
    """VotesListResponse.

    :ivar value: Required.
    :vartype value: list[~cleanroom.governance.client.models.Vote]
    """

    value: list["_models.Vote"] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""

    @overload
    def __init__(
        self,
        *,
        value: list["_models.Vote"],
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class WithdrawResponse(_Model):
    """Withdraw response.

    :ivar proposal_id: Required.
    :vartype proposal_id: str
    :ivar state: Required.
    :vartype state: str
    """

    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    state: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Required."""

    @overload
    def __init__(
        self,
        *,
        proposal_id: str,
        state: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class WorkspaceConfiguration(_Model):
    """WorkspaceConfiguration.

    :ivar ccf_endpoint: Required.
    :vartype ccf_endpoint: str
    :ivar signing_cert:
    :vartype signing_cert: str
    :ivar signing_key:
    :vartype signing_key: str
    :ivar service_cert:
    :vartype service_cert: str
    """

    ccf_endpoint: str = rest_field(
        name="ccfEndpoint", visibility=["read", "create", "update", "delete", "query"]
    )
    """Required."""
    signing_cert: Optional[str] = rest_field(
        name="signingCert", visibility=["read", "create", "update", "delete", "query"]
    )
    signing_key: Optional[str] = rest_field(
        name="signingKey", visibility=["read", "create", "update", "delete", "query"]
    )
    service_cert: Optional[str] = rest_field(
        name="serviceCert", visibility=["read", "create", "update", "delete", "query"]
    )

    @overload
    def __init__(
        self,
        *,
        ccf_endpoint: str,
        signing_cert: Optional[str] = None,
        signing_key: Optional[str] = None,
        service_cert: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class WorkspaceConfigurationModel(_Model):
    """Workspace configuration.

    :ivar ccf_endpoint:
    :vartype ccf_endpoint: str
    :ivar signing_cert_id:
    :vartype signing_cert_id: str
    :ivar auth_mode:
    :vartype auth_mode: str
    :ivar service_cert_discovery:
    :vartype service_cert_discovery: str
    """

    ccf_endpoint: Optional[str] = rest_field(
        name="CcfEndpoint", visibility=["read", "create", "update", "delete", "query"]
    )
    signing_cert_id: Optional[str] = rest_field(
        name="SigningCertId", visibility=["read", "create", "update", "delete", "query"]
    )
    auth_mode: Optional[str] = rest_field(
        name="AuthMode", visibility=["read", "create", "update", "delete", "query"]
    )
    service_cert_discovery: Optional[str] = rest_field(
        name="ServiceCertDiscovery",
        visibility=["read", "create", "update", "delete", "query"],
    )

    @overload
    def __init__(
        self,
        *,
        ccf_endpoint: Optional[str] = None,
        signing_cert_id: Optional[str] = None,
        auth_mode: Optional[str] = None,
        service_cert_discovery: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
