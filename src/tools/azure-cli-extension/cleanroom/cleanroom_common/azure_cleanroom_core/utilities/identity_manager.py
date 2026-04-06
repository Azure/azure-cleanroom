from logging import Logger
from typing import List

from ..models.cleanroom import (
    AttestationBasedTokenIssuer,
    CleanroomSecret,
    FederatedIdentityBasedTokenIssuer,
    Identity,
    ProtocolType,
    Resource,
    ResourceType,
    SecretBasedTokenIssuer,
    SecretType,
    ServiceEndpoint,
)


class IdentityManager:
    def __init__(self, identities: List[Identity], logger: Logger):
        """
        Initialize IdentityManager with a list of identities to manage.

        Args:
            identities: List of Identity objects to manage
        """
        self._identities = identities
        self._logger = logger

        default_identity = Identity(
            name="cleanroom_cgs_oidc",
            clientId="",
            tenantId="",
            tokenIssuer=AttestationBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.Attested_OIDC,
                    url="https://cgs/oidc",
                ),
                issuerType="AttestationBasedTokenIssuer",
            ),
        )
        self._upsert_identity(default_identity)

    @property
    def identities(self) -> List[Identity]:
        """Get the current list of identities."""
        return self._identities

    def add_identity_az_federated(
        self,
        name: str,
        client_id: str,
        tenant_id: str,
        token_issuer_url: str,
        backing_identity_name: str,
    ) -> None:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        try:
            backing_identity = self.get_identity(backing_identity_name)
        except CleanroomSpecificationError as error:
            if error.code == ErrorCode.IdentityConfigurationNotFound:
                raise CleanroomSpecificationError(
                    ErrorCode.BackingIdentityNotFound,
                    f"The specified backing identity {backing_identity_name} could not be found.",
                )
            raise error

        identity = Identity(
            name=name,
            clientId=client_id,
            tenantId=tenant_id,
            tokenIssuer=FederatedIdentityBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.AzureAD_Federated,
                    url=token_issuer_url,
                ),
                federatedIdentity=backing_identity,
                issuerType="FederatedIdentityBasedTokenIssuer",
            ),
        )

        self._upsert_identity(identity)

    def add_identity_az_secret(
        self,
        name: str,
        client_id: str,
        tenant_id: str,
        secret_name: str,
        secret_store_url: str,
        backing_identity_name: str,
    ) -> None:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        try:
            backing_identity = self.get_identity(backing_identity_name)
        except CleanroomSpecificationError as error:
            if error.code == ErrorCode.IdentityConfigurationNotFound:
                raise CleanroomSpecificationError(
                    ErrorCode.BackingIdentityNotFound,
                    f"The specified backing identity {backing_identity_name} could not be found.",
                )
            raise error

        identity = Identity(
            name=name,
            clientId=client_id,
            tenantId=tenant_id,
            tokenIssuer=SecretBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.AzureAD_Secret,
                    url="https://AzureAD",
                ),
                secret=CleanroomSecret(
                    secretType=SecretType.Secret,
                    backingResource=Resource(
                        name=secret_name,
                        id=secret_name,
                        provider=ServiceEndpoint(
                            protocol=ProtocolType.AzureKeyVault_Secret,
                            url=secret_store_url,
                        ),
                        type=ResourceType.AzureKeyVault,
                    ),
                ),
                secretAccessIdentity=backing_identity,
                issuerType="SecretBasedTokenIssuer",
            ),
        )

        self._upsert_identity(identity)

    def add_identity_oidc_attested(
        self,
        name: str,
        client_id: str,
        tenant_id: str,
        issuer_url: str,
    ) -> None:
        attested_identity = Identity(
            name=name,
            clientId=client_id,
            tenantId=tenant_id,
            tokenIssuer=AttestationBasedTokenIssuer(
                issuer=ServiceEndpoint(
                    protocol=ProtocolType.Attested_OIDC, url=issuer_url
                ),
                issuerType="AttestationBasedTokenIssuer",
            ),
        )

        self._upsert_identity(attested_identity)

    def get_identity(self, identity_name: str) -> Identity:
        from ..exceptions.exception import CleanroomSpecificationError, ErrorCode

        candidates = [x for x in self._identities if x.name == identity_name]
        if len(candidates) == 0:
            raise CleanroomSpecificationError(
                ErrorCode.IdentityConfigurationNotFound,
                (f"Identity {identity_name} not found in the configuration."),
            )

        return candidates[0]

    def _upsert_identity(self, identity: Identity) -> None:
        index = next(
            (i for i, x in enumerate(self._identities) if x.name == identity.name), None
        )
        if index is None:
            self._logger.info(
                f"Adding entry for identity {identity.name} in configuration."
            )
            self._identities.append(identity)
        else:
            self._logger.info(f"Patching identity {identity.name} in configuration.")
            self._identities[index] = identity
