# coding=utf-8

from copy import deepcopy
from typing import Any, Awaitable

from corehttp.rest import AsyncHttpResponse, HttpRequest
from corehttp.runtime import AsyncPipelineClient, policies
from typing_extensions import Self

from .._utils.serialization import Deserializer, Serializer
from ._configuration import ProxyClientConfiguration
from .operations import (
    CertificateAuthorityAttestationOperations,
    CertificateAuthorityOperations,
    CleanRoomPolicyDelegatesAttestationOperations,
    CleanRoomPolicyOperations,
    ConsentCheckOperations,
    ContractProposalsOperations,
    ContractRuntimeOptionsOperations,
    ContractsOperations,
    EventsAttestationOperations,
    EventsOperations,
    MemberDocumentsOperations,
    MembersOperations,
    NetworkOperations,
    NodeOperations,
    OAuthAttestationOperations,
    OAuthOperations,
    OIDCOperations,
    ProposalsOperations,
    RuntimeOptionsOperations,
    SecretsAttestationOperations,
    SecretsOperations,
    UpdatesOperations,
    UserDocumentsOperations,
    UserIdentitiesOperations,
    UserInvitationsOperations,
    UserProposalsOperations,
    UsersOperations,
    WorkspaceOperations,
)


class ProxyClient:  # pylint: disable=client-accepts-api-version-keyword,too-many-instance-attributes
    """HTTP proxy wrapper around the Cleanroom Governance Client Library, supporting both
    authentication modes:

    **Token-Based Authentication** (member certificates, JWT):

    * Contract management, proposals, voting
    * Member and user management
    * OIDC issuer configuration

    **Attestation-Based Authentication** (SNP attestation):

    * Secure secret retrieval with encrypted responses
    * Runtime consent checks
    * Endorsed certificate generation
    * Event auditing

    The proxy initializes in either token or attestation mode based on configuration.
    Operations unavailable in the current mode return HTTP 501 Not Implemented.

    :ivar contracts: ContractsOperations operations
    :vartype contracts: cleanroom.governance.client.proxy.aio.operations.ContractsOperations
    :ivar certificate_authority: CertificateAuthorityOperations operations
    :vartype certificate_authority:
     cleanroom.governance.client.proxy.aio.operations.CertificateAuthorityOperations
    :ivar certificate_authority_attestation: CertificateAuthorityAttestationOperations operations
    :vartype certificate_authority_attestation:
     cleanroom.governance.client.proxy.aio.operations.CertificateAuthorityAttestationOperations
    :ivar clean_room_policy: CleanRoomPolicyOperations operations
    :vartype clean_room_policy:
     cleanroom.governance.client.proxy.aio.operations.CleanRoomPolicyOperations
    :ivar clean_room_policy_delegates_attestation: CleanRoomPolicyDelegatesAttestationOperations
     operations
    :vartype clean_room_policy_delegates_attestation:
     cleanroom.governance.client.proxy.aio.operations.CleanRoomPolicyDelegatesAttestationOperations
    :ivar contract_proposals: ContractProposalsOperations operations
    :vartype contract_proposals:
     cleanroom.governance.client.proxy.aio.operations.ContractProposalsOperations
    :ivar contract_runtime_options: ContractRuntimeOptionsOperations operations
    :vartype contract_runtime_options:
     cleanroom.governance.client.proxy.aio.operations.ContractRuntimeOptionsOperations
    :ivar consent_check: ConsentCheckOperations operations
    :vartype consent_check: cleanroom.governance.client.proxy.aio.operations.ConsentCheckOperations
    :ivar events: EventsOperations operations
    :vartype events: cleanroom.governance.client.proxy.aio.operations.EventsOperations
    :ivar events_attestation: EventsAttestationOperations operations
    :vartype events_attestation:
     cleanroom.governance.client.proxy.aio.operations.EventsAttestationOperations
    :ivar member_documents: MemberDocumentsOperations operations
    :vartype member_documents:
     cleanroom.governance.client.proxy.aio.operations.MemberDocumentsOperations
    :ivar members: MembersOperations operations
    :vartype members: cleanroom.governance.client.proxy.aio.operations.MembersOperations
    :ivar network: NetworkOperations operations
    :vartype network: cleanroom.governance.client.proxy.aio.operations.NetworkOperations
    :ivar node: NodeOperations operations
    :vartype node: cleanroom.governance.client.proxy.aio.operations.NodeOperations
    :ivar oauth: OAuthOperations operations
    :vartype oauth: cleanroom.governance.client.proxy.aio.operations.OAuthOperations
    :ivar oauth_attestation: OAuthAttestationOperations operations
    :vartype oauth_attestation:
     cleanroom.governance.client.proxy.aio.operations.OAuthAttestationOperations
    :ivar oidc: OIDCOperations operations
    :vartype oidc: cleanroom.governance.client.proxy.aio.operations.OIDCOperations
    :ivar proposals: ProposalsOperations operations
    :vartype proposals: cleanroom.governance.client.proxy.aio.operations.ProposalsOperations
    :ivar runtime_options: RuntimeOptionsOperations operations
    :vartype runtime_options:
     cleanroom.governance.client.proxy.aio.operations.RuntimeOptionsOperations
    :ivar secrets: SecretsOperations operations
    :vartype secrets: cleanroom.governance.client.proxy.aio.operations.SecretsOperations
    :ivar secrets_attestation: SecretsAttestationOperations operations
    :vartype secrets_attestation:
     cleanroom.governance.client.proxy.aio.operations.SecretsAttestationOperations
    :ivar updates: UpdatesOperations operations
    :vartype updates: cleanroom.governance.client.proxy.aio.operations.UpdatesOperations
    :ivar user_documents: UserDocumentsOperations operations
    :vartype user_documents:
     cleanroom.governance.client.proxy.aio.operations.UserDocumentsOperations
    :ivar user_identities: UserIdentitiesOperations operations
    :vartype user_identities:
     cleanroom.governance.client.proxy.aio.operations.UserIdentitiesOperations
    :ivar user_invitations: UserInvitationsOperations operations
    :vartype user_invitations:
     cleanroom.governance.client.proxy.aio.operations.UserInvitationsOperations
    :ivar user_proposals: UserProposalsOperations operations
    :vartype user_proposals:
     cleanroom.governance.client.proxy.aio.operations.UserProposalsOperations
    :ivar users: UsersOperations operations
    :vartype users: cleanroom.governance.client.proxy.aio.operations.UsersOperations
    :ivar workspace: WorkspaceOperations operations
    :vartype workspace: cleanroom.governance.client.proxy.aio.operations.WorkspaceOperations
    :param endpoint: Service host. Required.
    :type endpoint: str
    """

    def __init__(  # pylint: disable=missing-client-constructor-parameter-credential
        self, endpoint: str, **kwargs: Any
    ) -> None:
        _endpoint = "{endpoint}"
        self._config = ProxyClientConfiguration(endpoint=endpoint, **kwargs)

        _policies = kwargs.pop("policies", None)
        if _policies is None:
            _policies = [
                self._config.headers_policy,
                self._config.user_agent_policy,
                self._config.proxy_policy,
                policies.ContentDecodePolicy(**kwargs),
                self._config.retry_policy,
                self._config.authentication_policy,
                self._config.logging_policy,
            ]
        self._client: AsyncPipelineClient = AsyncPipelineClient(
            endpoint=_endpoint, policies=_policies, **kwargs
        )

        self._serialize = Serializer()
        self._deserialize = Deserializer()
        self._serialize.client_side_validation = False
        self.contracts = ContractsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.certificate_authority = CertificateAuthorityOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.certificate_authority_attestation = (
            CertificateAuthorityAttestationOperations(
                self._client, self._config, self._serialize, self._deserialize
            )
        )
        self.clean_room_policy = CleanRoomPolicyOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.clean_room_policy_delegates_attestation = (
            CleanRoomPolicyDelegatesAttestationOperations(
                self._client, self._config, self._serialize, self._deserialize
            )
        )
        self.contract_proposals = ContractProposalsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.contract_runtime_options = ContractRuntimeOptionsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.consent_check = ConsentCheckOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.events = EventsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.events_attestation = EventsAttestationOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.member_documents = MemberDocumentsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.members = MembersOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.network = NetworkOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.node = NodeOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.oauth = OAuthOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.oauth_attestation = OAuthAttestationOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.oidc = OIDCOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.proposals = ProposalsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.runtime_options = RuntimeOptionsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.secrets = SecretsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.secrets_attestation = SecretsAttestationOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.updates = UpdatesOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.user_documents = UserDocumentsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.user_identities = UserIdentitiesOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.user_invitations = UserInvitationsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.user_proposals = UserProposalsOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.users = UsersOperations(
            self._client, self._config, self._serialize, self._deserialize
        )
        self.workspace = WorkspaceOperations(
            self._client, self._config, self._serialize, self._deserialize
        )

    def send_request(
        self, request: HttpRequest, *, stream: bool = False, **kwargs: Any
    ) -> Awaitable[AsyncHttpResponse]:
        """Runs the network request through the client's chained policies.

        >>> from corehttp.rest import HttpRequest
        >>> request = HttpRequest("GET", "https://www.example.org/")
        <HttpRequest [GET], url: 'https://www.example.org/'>
        >>> response = await client.send_request(request)
        <AsyncHttpResponse: 200 OK>

        For more information on this code flow, see https://aka.ms/azsdk/dpcodegen/python/send_request

        :param request: The network request you want to make. Required.
        :type request: ~corehttp.rest.HttpRequest
        :keyword bool stream: Whether the response payload will be streamed. Defaults to False.
        :return: The response of your network call. Does not do error handling on your response.
        :rtype: ~corehttp.rest.AsyncHttpResponse
        """

        request_copy = deepcopy(request)
        path_format_arguments = {
            "endpoint": self._serialize.url(
                "self._config.endpoint", self._config.endpoint, "str", skip_quote=True
            ),
        }

        request_copy.url = self._client.format_url(
            request_copy.url, **path_format_arguments
        )
        return self._client.send_request(request_copy, stream=stream, **kwargs)  # type: ignore

    async def close(self) -> None:
        await self._client.close()

    async def __aenter__(self) -> Self:
        await self._client.__aenter__()
        return self

    async def __aexit__(self, *exc_details: Any) -> None:
        await self._client.__aexit__(*exc_details)
