// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package contracts

import "fmt"

const (
	OIDCAudience = "api://AzureADTokenExchange"
)

func GovernanceAPIPathPrefix(contractID, subject string) string {
	id := contractID
	if id == "" {
		id = subject
	}
	if id == "" {
		return ""
	}
	return fmt.Sprintf("app/contracts/%s", id)
}

type FederatedCredentialConfiguration struct {
	IDTokenEndpoint         string `json:"idTokenEndpoint"`
	Subject                 string `json:"subject"`
	Audience                string `json:"audience"`
	GovernanceAPIPathPrefix string `json:"governanceApiPathPrefix,omitempty"`
}

type RegisterIdentityRequest struct {
	ClientID string                           `json:"clientId"`
	Credential RegisterIdentityCredentialSpec `json:"credential"`
}

type RegisterIdentityCredentialSpec struct {
	CredentialType          string                           `json:"credentialType"`
	FederationConfiguration FederatedCredentialConfiguration `json:"federationConfiguration"`
}

type UnwrapDEKRequest struct {
	ClientID    string   `json:"clientId"`
	TenantID    string   `json:"tenantId"`
	KID         string   `json:"kid"`
	AKVEndpoint string   `json:"akvEndpoint"`
	KEK         KEKSpec  `json:"kek"`
}

// KEKSpec identifies the Key Encryption Key used to wrap a DEK.
type KEKSpec struct {
	KID         string `json:"kid"`
	AKVEndpoint string `json:"akvEndpoint"`
	MAAEndpoint string `json:"maaEndpoint"`
}

type UnwrapDEKResponse struct {
	Value string `json:"value"`
}
