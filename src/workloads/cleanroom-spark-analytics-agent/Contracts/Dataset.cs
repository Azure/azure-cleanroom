// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

#pragma warning disable SA1602 // Enumeration items should be documented
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessPointType
{
    Volume_ReadWrite,
    Volume_ReadOnly
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyType
{
    SecureVolume__ReadOnly__Azure__OneLake,
    SecureVolume__ReadOnly__Azure__BlobStorage,
    SecureVolume__ReadOnly__Aws__S3,
    SecureVolume__ReadWrite__Azure__OneLake,
    SecureVolume__ReadWrite__Azure__BlobStorage,
    SecureVolume__ReadWrite__Aws__S3,
    API,
    SecureAPI
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyMode
{
    Secure,
    Open
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecretType
{
    Secret,
    Certificate,
    Key
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceType
{
    Azure_BlobStorage,
    Azure_OneLake,
    AzureKeyVault,
    Aws_S3,
    Cgs,
    AzureContainerRegistry
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProtocolType
{
    AzureAD_Federated,
    AzureAD_ManagedIdentity,
    AzureAD_Secret,
    Attested_OIDC,
    AzureKeyVault_Secret,
    AzureKeyVault_SecureKey,
    AzureKeyVault_Key,
    AzureKeyVault_Certificate,
    Cgs_Secret,
    Azure_BlobStorage,
    Azure_OneLake,
    AzureContainerRegistry,
    Aws_S3
}
#pragma warning restore SA1602 // Enumeration items should be documented

#pragma warning disable MEN002 // Line is too long

public record Dataset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("viewName")] string ViewName,
    [property: JsonPropertyName("ownerId")] string OwnerId,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("schema")] Dictionary<string, SchemaField> Schema,
    [property: JsonPropertyName("accessPoint")] AccessPoint AccessPoint);

public record SchemaField(
    [property: JsonPropertyName("type")] string Type);

public record AccessPoint(
    [property: JsonPropertyName("type")] AccessPointType Type,
    [property: JsonPropertyName("protection")] PrivacyProxySettings Protection,
    [property: JsonPropertyName("store")] Resource Store,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("identity")] Identity Identity);

public record PrivacyProxySettings(
    [property: JsonPropertyName("proxyType")] ProxyType ProxyType,
    [property: JsonPropertyName("configuration")] string Configuration,
    [property: JsonPropertyName("privacyPolicy")] object? PrivacyPolicy,
    [property: JsonPropertyName("proxyMode")] ProxyMode ProxyMode,
    [property: JsonPropertyName("encryptionSecretAccessIdentity")] Identity? EncryptionSecretAccessIdentity,
    [property: JsonPropertyName("encryptionSecrets")] EncryptionSecrets EncryptionSecrets);

public record EncryptionSecrets(
    [property: JsonPropertyName("kek")] SecretWrapper? Kek,
    [property: JsonPropertyName("dek")] SecretWrapper Dek);

public record SecretWrapper(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("secret")] Secret Secret);

public record Secret(
    [property: JsonPropertyName("secretType")] SecretType SecretType,
    [property: JsonPropertyName("backingResource")] Resource BackingResource);

public record Resource(
    [property: JsonPropertyName("provider")] ServiceEndpoint Provider,
    [property: JsonPropertyName("type")] ResourceType Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("id")] string Id);

public record ServiceEndpoint(
    [property: JsonPropertyName("configuration")] string Configuration,
    [property: JsonPropertyName("protocol")] ProtocolType Protocol,
    [property: JsonPropertyName("url")] string Url);

public record Identity(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tokenIssuer")] TokenIssuer TokenIssuer);

public record TokenIssuer(
    [property: JsonPropertyName("federatedIdentity")] FederatedIdentity? FederatedIdentity,
    [property: JsonPropertyName("issuer")] Issuer Issuer,
    [property: JsonPropertyName("issuerType")] string IssuerType);

public record FederatedIdentity(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("tokenIssuer")] TokenIssuer TokenIssuer);

public record Issuer(
    [property: JsonPropertyName("configuration")] string Configuration,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("url")] string? Url);

#pragma warning restore MEN002 // Line is too long
