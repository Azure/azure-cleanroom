# coding=utf-8
# pylint: disable=useless-super-delegation

from typing import TYPE_CHECKING, Any, Mapping, Optional, overload

from ..client.proxy._utils.model_base import Model as _Model
from ..client.proxy._utils.model_base import rest_field

if TYPE_CHECKING:
    from .. import models as _models


class Encrypt(_Model):
    """Encryption information.

    :ivar public_key: PEM encoded public key. Required.
    :vartype public_key: str
    """

    public_key: str = rest_field(
        name="publicKey", visibility=["read", "create", "update", "delete", "query"]
    )
    """PEM encoded public key. Required."""

    @overload
    def __init__(
        self,
        *,
        public_key: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class GetMemberDocumentResponse(_Model):
    """Get member document response.

    :ivar id: Document ID. Required.
    :vartype id: str
    :ivar contract_id: Contract ID. Required.
    :vartype contract_id: str
    :ivar version: Document version. Required.
    :vartype version: str
    :ivar data: Document data. Required.
    :vartype data: any
    :ivar state: Document state. Required.
    :vartype state: str
    :ivar proposal_id: Proposal ID. Required.
    :vartype proposal_id: str
    :ivar final_votes: Final votes.
    :vartype final_votes: list[~cleanroom.governance.models.MemberVote]
    """

    id: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Document ID. Required."""
    contract_id: str = rest_field(
        name="contractId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Contract ID. Required."""
    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Document version. Required."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Document data. Required."""
    state: str = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Document state. Required."""
    proposal_id: str = rest_field(
        name="proposalId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Proposal ID. Required."""
    final_votes: Optional[list["_models.MemberVote"]] = rest_field(
        name="finalVotes", visibility=["read", "create", "update", "delete", "query"]
    )
    """Final votes."""

    @overload
    def __init__(
        self,
        *,
        id: str,  # pylint: disable=redefined-builtin
        contract_id: str,
        version: str,
        data: Any,
        state: str,
        proposal_id: str,
        final_votes: Optional[list["_models.MemberVote"]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class ListSecretResponse(_Model):
    """List secret response.

    :ivar secret_id: Secret ID. Required.
    :vartype secret_id: str
    """

    secret_id: str = rest_field(
        name="secretId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Secret ID. Required."""

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


class MemberVote(_Model):
    """Member vote.

    :ivar member_id: Member ID. Required.
    :vartype member_id: str
    :ivar vote: Vote value. Required.
    :vartype vote: bool
    """

    member_id: str = rest_field(
        name="memberId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Member ID. Required."""
    vote: bool = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Vote value. Required."""

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


class PutContractRequest(_Model):
    """Put contract request.

    :ivar version: Contract version. Required.
    :vartype version: str
    :ivar data: Contract data. Required.
    :vartype data: any
    """

    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Contract version. Required."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Contract data. Required."""

    @overload
    def __init__(
        self,
        *,
        version: str,
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


class PutUserDocumentRequest(_Model):
    """Put user document request.

    :ivar version: Document version. Required.
    :vartype version: str
    :ivar contract_id: Contract ID. Required.
    :vartype contract_id: str
    :ivar approvers: Approvers.
    :vartype approvers: list[~cleanroom.governance.models.UserProposalApprover]
    :ivar data: Document data. Required.
    :vartype data: any
    :ivar labels: Labels. Required.
    :vartype labels: dict[str, str]
    """

    version: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Document version. Required."""
    contract_id: str = rest_field(
        name="contractId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Contract ID. Required."""
    approvers: Optional[list["_models.UserProposalApprover"]] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Approvers."""
    data: Any = rest_field(visibility=["read", "create", "update", "delete", "query"])
    """Document data. Required."""
    labels: dict[str, str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Labels. Required."""

    @overload
    def __init__(
        self,
        *,
        version: str,
        contract_id: str,
        data: Any,
        labels: dict[str, str],
        approvers: Optional[list["_models.UserProposalApprover"]] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class Sign(_Model):
    """Digital signature information.

    :ivar signature: Signature value. Required.
    :vartype signature: str
    :ivar public_key: PEM encoded public key. Required.
    :vartype public_key: str
    """

    signature: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Signature value. Required."""
    public_key: str = rest_field(
        name="publicKey", visibility=["read", "create", "update", "delete", "query"]
    )
    """PEM encoded public key. Required."""

    @overload
    def __init__(
        self,
        *,
        signature: str,
        public_key: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class SnpEvidence(_Model):
    """SNP attestation evidence.

    :ivar evidence: Base64 encoded evidence. Required.
    :vartype evidence: str
    :ivar endorsements: Base64 encoded endorsements. Required.
    :vartype endorsements: str
    :ivar uvm_endorsements: Base64 encoded UVM endorsements. Required.
    :vartype uvm_endorsements: str
    :ivar endorsed_tcb: Endorsed TCB version.
    :vartype endorsed_tcb: str
    """

    evidence: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Base64 encoded evidence. Required."""
    endorsements: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Base64 encoded endorsements. Required."""
    uvm_endorsements: str = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Base64 encoded UVM endorsements. Required."""
    endorsed_tcb: Optional[str] = rest_field(
        visibility=["read", "create", "update", "delete", "query"]
    )
    """Endorsed TCB version."""

    @overload
    def __init__(
        self,
        *,
        evidence: str,
        endorsements: str,
        uvm_endorsements: str,
        endorsed_tcb: Optional[str] = None,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)


class UserProposalApprover(_Model):
    """User proposal approver.

    :ivar approver_id: Approver ID. Required.
    :vartype approver_id: str
    :ivar approver_id_type: Approver ID type. Required.
    :vartype approver_id_type: str
    """

    approver_id: str = rest_field(
        name="approverId", visibility=["read", "create", "update", "delete", "query"]
    )
    """Approver ID. Required."""
    approver_id_type: str = rest_field(
        name="approverIdType",
        visibility=["read", "create", "update", "delete", "query"],
    )
    """Approver ID type. Required."""

    @overload
    def __init__(
        self,
        *,
        approver_id: str,
        approver_id_type: str,
    ) -> None: ...

    @overload
    def __init__(self, mapping: Mapping[str, Any]) -> None:
        """
        :param mapping: raw JSON to initialize the model.
        :type mapping: Mapping[str, Any]
        """

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
