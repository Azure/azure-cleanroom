package azure

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"strings"

	"github.com/Azure/azure-sdk-for-go/sdk/azcore"
	"github.com/Azure/azure-sdk-for-go/sdk/azcore/policy"
	"github.com/Azure/azure-sdk-for-go/sdk/azcore/to"
	"github.com/Azure/azure-sdk-for-go/sdk/azidentity"
	"github.com/Azure/azure-sdk-for-go/sdk/resourcemanager/authorization/armauthorization/v2"
	"github.com/Azure/azure-sdk-for-go/sdk/resourcemanager/msi/armmsi"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob/blob"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob/bloberror"
	"github.com/google/uuid"
)

// Client wraps Azure SDK operations needed by the
// ModelRegistration controller for OIDC issuer and access
// setup.
type Client struct {
	credential azcore.TokenCredential
}

// NewClient creates an Azure client using
// DefaultAzureCredential (supports workload identity
// when running in-cluster).
func NewClient() (*Client, error) {
	cred, err := azidentity.NewDefaultAzureCredential(
		nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating Azure credential: %w", err,
		)
	}
	return &Client{credential: cred}, nil
}

// UploadBlobToStaticWebsite uploads content to the $web
// container of a storage account's static website.
func (c *Client) UploadBlobToStaticWebsite(
	ctx context.Context,
	storageAccountName string,
	blobPath string,
	content []byte,
	contentType string,
) error {
	serviceURL := fmt.Sprintf(
		"https://%s.blob.core.windows.net",
		storageAccountName,
	)

	blobClient, err := azblob.NewClient(
		serviceURL, c.credential, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating blob client: %w", err,
		)
	}

	_, err = blobClient.UploadBuffer(
		ctx, "$web", blobPath, content,
		&azblob.UploadBufferOptions{
			HTTPHeaders: &blob.HTTPHeaders{
				BlobContentType: to.Ptr(contentType),
			},
		},
	)
	if err != nil {
		return fmt.Errorf(
			"uploading blob %s: %w", blobPath, err,
		)
	}

	return nil
}

// GetStaticWebsiteURL returns the primary web endpoint
// for a storage account by constructing the conventional
// Azure static website URL.
func GetStaticWebsiteURL(
	storageAccountName string,
) string {
	return fmt.Sprintf(
		"https://%s.z22.web.core.windows.net",
		storageAccountName,
	)
}

// ManagedIdentityInfo holds the principal, client, and
// tenant IDs of a user-assigned managed identity.
type ManagedIdentityInfo struct {
	PrincipalID string
	ClientID    string
	TenantID    string
}

// GetManagedIdentityPrincipalID retrieves the principal,
// client, and tenant IDs of a user-assigned managed
// identity.
func (c *Client) GetManagedIdentityPrincipalID(
	ctx context.Context,
	subscriptionID string,
	resourceGroup string,
	identityName string,
) (ManagedIdentityInfo, error) {
	clientFactory, err :=
		armmsi.NewClientFactory(
			subscriptionID, c.credential, nil,
		)
	if err != nil {
		return ManagedIdentityInfo{}, fmt.Errorf(
			"creating MSI client factory: %w", err,
		)
	}

	resp, err := clientFactory.
		NewUserAssignedIdentitiesClient().
		Get(ctx, resourceGroup, identityName, nil)
	if err != nil {
		return ManagedIdentityInfo{}, fmt.Errorf(
			"getting identity %s/%s: %w",
			resourceGroup, identityName, err,
		)
	}

	if resp.Properties == nil ||
		resp.Properties.PrincipalID == nil {
		return ManagedIdentityInfo{}, fmt.Errorf(
			"identity %s has no principalId",
			identityName,
		)
	}

	info := ManagedIdentityInfo{
		PrincipalID: *resp.Properties.PrincipalID,
	}
	if resp.Properties.ClientID != nil {
		info.ClientID = *resp.Properties.ClientID
	}
	if resp.Properties.TenantID != nil {
		info.TenantID = *resp.Properties.TenantID
	}
	return info, nil
}

// GetBlobEndpointFromArmID constructs the conventional
// Azure Blob Storage endpoint URL from a storage account
// ARM resource ID.
func GetBlobEndpointFromArmID(armID string) string {
	parts := strings.Split(armID, "/")
	name := parts[len(parts)-1]
	return fmt.Sprintf(
		"https://%s.blob.core.windows.net/", name,
	)
}

// CreateFederatedCredential creates a federated identity
// credential on a user-assigned managed identity.
func (c *Client) CreateFederatedCredential(
	ctx context.Context,
	subscriptionID string,
	resourceGroup string,
	identityName string,
	fedCredName string,
	issuerURL string,
	subject string,
) error {
	clientFactory, err :=
		armmsi.NewClientFactory(
			subscriptionID, c.credential, nil,
		)
	if err != nil {
		return fmt.Errorf(
			"creating MSI client factory: %w", err,
		)
	}

	_, err = clientFactory.
		NewFederatedIdentityCredentialsClient().
		CreateOrUpdate(
			ctx, resourceGroup, identityName,
			fedCredName,
			armmsi.FederatedIdentityCredential{
				Properties: &armmsi.FederatedIdentityCredentialProperties{
					Issuer:    to.Ptr(issuerURL),
					Subject:   to.Ptr(subject),
					Audiences: []*string{to.Ptr("api://AzureADTokenExchange")},
				},
			}, nil,
		)
	if err != nil {
		return fmt.Errorf(
			"creating federated credential %s: %w",
			fedCredName, err,
		)
	}

	return nil
}

// AssignRole assigns an RBAC role to a principal on a
// given scope.
func (c *Client) AssignRole(
	ctx context.Context,
	subscriptionID string,
	scope string,
	roleDefinitionID string,
	principalID string,
) error {
	client, err :=
		armauthorization.NewRoleAssignmentsClient(
			subscriptionID, c.credential, nil,
		)
	if err != nil {
		return fmt.Errorf(
			"creating role assignments client: %w",
			err,
		)
	}

	assignmentID := uuid.New().String()
	_, err = client.Create(
		ctx, scope, assignmentID,
		armauthorization.RoleAssignmentCreateParameters{
			Properties: &armauthorization.RoleAssignmentProperties{
				RoleDefinitionID: to.Ptr(roleDefinitionID),
				PrincipalID:      to.Ptr(principalID),
				PrincipalType: to.Ptr(
					armauthorization.PrincipalTypeServicePrincipal,
				),
			},
		}, nil,
	)
	if err != nil {
		// Ignore "already exists" (RoleAssignmentExists).
		if isRoleAssignmentExistsError(err) {
			return nil
		}
		return fmt.Errorf(
			"creating role assignment: %w", err,
		)
	}

	return nil
}

// StorageBlobDataContributorRoleID is the well-known
// role definition ID for Storage Blob Data Contributor.
const StorageBlobDataContributorRoleID = "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe"

// GetOwnObjectID returns the object (principal) ID of the
// identity backing this client's credential, by acquiring an
// ARM token and reading the "oid" claim. This lets the
// in-cluster operator discover its own workload-identity
// principal without querying Microsoft Graph or knowing the
// identity's resource group / name.
func (c *Client) GetOwnObjectID(
	ctx context.Context,
) (string, error) {
	tok, err := c.credential.GetToken(
		ctx, policy.TokenRequestOptions{
			Scopes: []string{
				"https://management.azure.com/.default",
			},
		},
	)
	if err != nil {
		return "", fmt.Errorf(
			"acquiring token: %w", err,
		)
	}

	parts := strings.Split(tok.Token, ".")
	if len(parts) < 2 {
		return "", fmt.Errorf(
			"unexpected access token format",
		)
	}
	payload, err := base64.RawURLEncoding.DecodeString(
		parts[1],
	)
	if err != nil {
		return "", fmt.Errorf(
			"decoding token payload: %w", err,
		)
	}
	var claims struct {
		Oid string `json:"oid"`
	}
	if err := json.Unmarshal(payload, &claims); err != nil {
		return "", fmt.Errorf(
			"parsing token claims: %w", err,
		)
	}
	if claims.Oid == "" {
		return "", fmt.Errorf(
			"access token has no oid claim",
		)
	}
	return claims.Oid, nil
}

// EnsureBlobContainer creates the blob container if it does
// not already exist, using this client's credential.
func (c *Client) EnsureBlobContainer(
	ctx context.Context,
	storageAccountName string,
	containerName string,
) error {
	serviceURL := fmt.Sprintf(
		"https://%s.blob.core.windows.net",
		storageAccountName,
	)
	client, err := azblob.NewClient(
		serviceURL, c.credential, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating blob client: %w", err,
		)
	}
	_, err = client.CreateContainer(
		ctx, containerName, nil,
	)
	if err != nil && !bloberror.HasCode(
		err, bloberror.ContainerAlreadyExists,
	) {
		return fmt.Errorf(
			"creating container %s: %w",
			containerName, err,
		)
	}
	return nil
}

// IsAuthorizationError reports whether err is a blob
// authorization failure, which typically means an RBAC role
// assignment has not yet propagated and the operation should
// be retried after a short delay.
func IsAuthorizationError(err error) bool {
	return bloberror.HasCode(
		err, bloberror.AuthorizationPermissionMismatch,
	) || bloberror.HasCode(
		err, bloberror.AuthorizationFailure,
	) || bloberror.HasCode(
		err, bloberror.InsufficientAccountPermissions,
	)
}

// ParseArmResource extracts the subscription ID,
// resource group, and resource name from an ARM resource
// ID.
func ParseArmResource(
	armID string,
) (subscriptionID, resourceGroup, name string, err error) {
	parts := strings.Split(armID, "/")
	for i, p := range parts {
		if strings.EqualFold(p, "subscriptions") &&
			i+1 < len(parts) {
			subscriptionID = parts[i+1]
		}
		if strings.EqualFold(p, "resourceGroups") &&
			i+1 < len(parts) {
			resourceGroup = parts[i+1]
		}
	}
	if subscriptionID == "" {
		return "", "", "", fmt.Errorf(
			"cannot parse subscriptionId from %q",
			armID,
		)
	}
	if resourceGroup == "" {
		return "", "", "", fmt.Errorf(
			"cannot parse resourceGroup from %q",
			armID,
		)
	}
	name = parts[len(parts)-1]
	if name == "" {
		return "", "", "", fmt.Errorf(
			"cannot parse resource name from %q",
			armID,
		)
	}
	return subscriptionID, resourceGroup, name, nil
}

// isRoleAssignmentExistsError checks if the error
// indicates the role assignment already exists.
func isRoleAssignmentExistsError(err error) bool {
	return strings.Contains(
		err.Error(), "RoleAssignmentExists",
	)
}

// UploadOidcDocuments uploads the OpenID configuration
// and JWKS documents to the OIDC storage account's
// static website $web container.
func (c *Client) UploadOidcDocuments(
	ctx context.Context,
	oidcStorageAccount string,
	oidcContainer string,
	issuerURL string,
	jwksData []byte,
) error {
	// 1. Generate openid-configuration.
	oidcConfig := fmt.Sprintf(
		`{"issuer":"%s",`+
			`"jwks_uri":"%s/openid/v1/jwks",`+
			`"response_types_supported":["id_token"],`+
			`"subject_types_supported":["public"],`+
			`"id_token_signing_alg_values_supported":`+
			`["RS256"]}`,
		issuerURL, issuerURL,
	)

	// 2. Upload openid-configuration.
	configBlobPath := oidcContainer +
		"/.well-known/openid-configuration"
	if err := c.UploadBlobToStaticWebsite(
		ctx, oidcStorageAccount, configBlobPath,
		[]byte(oidcConfig), "application/json",
	); err != nil {
		return fmt.Errorf(
			"uploading openid-configuration: %w", err,
		)
	}

	// 3. Upload JWKS.
	jwksBlobPath := oidcContainer + "/openid/v1/jwks"
	if err := c.UploadBlobToStaticWebsite(
		ctx, oidcStorageAccount, jwksBlobPath,
		jwksData, "application/json",
	); err != nil {
		return fmt.Errorf(
			"uploading JWKS: %w", err,
		)
	}

	return nil
}
