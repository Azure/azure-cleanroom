// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtocolType
{
    /// <summary>
    /// Azure AD Federated.
    /// </summary>
    AzureAD_Federated,

    /// <summary>
    /// Azure AD Managed Identity.
    /// </summary>
    AzureAD_ManagedIdentity,

    /// <summary>
    /// Azure AD Secret-based.
    /// </summary>
    AzureAD_Secret,

    /// <summary>
    /// Attested OIDC (OpenID Connect).
    /// </summary>
    Attested_OIDC,

    /// <summary>
    /// Azure Key Vault Secret-based.
    /// </summary>
    AzureKeyVault_Secret,

    /// <summary>
    /// Azure Key Vault Secure Key.
    /// </summary>
    AzureKeyVault_SecureKey,

    /// <summary>
    /// Azure Key Vault Key-based.
    /// </summary>
    AzureKeyVault_Key,

    /// <summary>
    /// Azure Key Vault Certificate-based.
    /// </summary>
    AzureKeyVault_Certificate,

    /// <summary>
    /// Clean Room Service Secret-based.
    /// </summary>
    Cgs_Secret,

    /// <summary>
    /// Azure Blob Storage.
    /// </summary>
    Azure_BlobStorage,

    /// <summary>
    /// Azure Blob Storage Data lake Gen 2.
    /// </summary>
    Azure_BlobStorage_DataLakeGen2,

    /// <summary>
    /// Azure OneLake.
    /// </summary>
    Azure_OneLake,

    /// <summary>
    /// Azure Container Registry.
    /// </summary>
    AzureContainerRegistry,

    /// <summary>
    /// Amazon S3 storage.
    /// </summary>
    Aws_S3,
}
