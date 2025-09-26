// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Identity.Configuration;

/// <summary>
/// The credential type.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialType
{
    /// <summary>
    /// Secret based authentication.
    /// </summary>
    Secret,

    /// <summary>
    /// Certificate based authentication.
    /// </summary>
    Certificate,

    /// <summary>
    /// A federated credential.
    /// </summary>
    FederatedCredential,
}

/// <summary>
/// Class that represents the Identity configuration.
/// </summary>
public class IdentityConfiguration
{
    /// <summary>
    /// Gets or sets a list of identities associated with the cleanroom.
    /// </summary>
    public Identities Identities { get; set; } = default!;
}

/// <summary>
/// Class that represents the identities associated with the cleanroom.
/// </summary>
public class Identities
{
    /// <summary>
    /// Gets or sets the list of managed identities.
    /// </summary>
    public List<ManagedIdentity> ManagedIdentities { get; set; } = default!;

    /// <summary>
    /// Gets or sets the list of application identities.
    /// </summary>
    public List<ApplicationIdentity> ApplicationIdentities { get; set; } = default!;
}

/// <summary>
/// Class that represents a managed identity.
/// </summary>
public class ManagedIdentity
{
    /// <summary>
    /// Gets or sets the client Id.
    /// </summary>
    public string ClientId { get; set; } = default!;
}

/// <summary>
/// Class that represents the application identity.
/// </summary>
public class ApplicationIdentity
{
    /// <summary>
    /// Gets or sets the client Id.
    /// </summary>
    public string ClientId { get; set; } = default!;

    /// <summary>
    /// Gets or sets the credential details.
    /// </summary>
    public Credential Credential { get; set; } = default!;
}

/// <summary>
/// Class that represents credential details.
/// </summary>
public class Credential
{
    /// <summary>
    /// Gets or sets the credential type.
    /// </summary>
    public CredentialType CredentialType { get; set; }

    /// <summary>
    /// Gets or sets the secret configuration.
    /// </summary>
    public SecretConfiguration SecretConfiguration { get; set; } = default!;

    /// <summary>
    /// Gets or sets the federated credential configuration.
    /// </summary>
    // TODO (anrdesai): Remove the provider specific configurations and switch to a generic
    // ConfigurationJson string. The interpretation of this string will be provider specific.
    public FederationConfiguration FederationConfiguration { get; set; } =
        default!;
}

public class SecretConfiguration
{
    /// <summary>
    /// Gets or sets the secret name.
    /// </summary>
    public string SecretName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the secret store details.
    /// </summary>
    public SecretStore SecretStore { get; set; } = default!;
}

/// <summary>
/// Class that represents secret stores.
/// </summary>
public class SecretStore
{
    /// <summary>
    /// Gets or sets the name of the secret store.
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// Gets or sets the type of the secret store.
    /// </summary>
    public string Type { get; set; } = default!;

    /// <summary>
    /// Gets or sets the endpoint for the secret store.
    /// </summary>
    public string Endpoint { get; set; } = default!;

    /// <summary>
    /// Gets or sets the Key Vault configuration.
    /// </summary>
    public KeyVaultConfiguration KeyVaultConfiguration { get; set; } = default!;
}

/// <summary>
/// Configuration for federated credentials.
/// </summary>
public class FederationConfiguration
{
    /// <summary>
    /// Gets or sets the federated token endpoint.
    /// </summary>
    public string IdTokenEndpoint { get; set; } = default!;

    /// <summary>
    /// Gets or sets the subject for a federated credential.
    /// </summary>
    public string Subject { get; set; } = default!;

    /// <summary>
    /// Gets or sets the audience for the federated credential.
    /// </summary>
    public string Audience { get; set; } = default!;

    /// <summary>
    /// Gets or sets the issuer for the federated credential.
    /// </summary>
    public string? Issuer { get; set; }
}

/// <summary>
/// Class to hold Key Vault specific fields for a Key Vault secret store.
/// </summary>
public class KeyVaultConfiguration
{
    /// <summary>
    /// Gets or sets the managed identity client ID.
    /// </summary>
    public string ManagedIdentityClientId { get; set; } = default!;
}