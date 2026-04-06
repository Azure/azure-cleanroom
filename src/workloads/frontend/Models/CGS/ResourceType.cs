// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceType
{
    /// <summary>
    /// Azure blob storage.
    /// </summary>
    [JsonStringEnumMemberName("Azure_BlobStorage")]
    Azure_BlobStorage,

    /// <summary>
    /// Azure Blob Storage Data Lake Gen 2.
    /// </summary>
    [JsonStringEnumMemberName("Azure_BlobStorage_DataLakeGen2")]
    Azure_BlobStorage_DataLakeGen2,

    /// <summary>
    /// Azure one lake.
    /// </summary>
    [JsonStringEnumMemberName("Azure_OneLake")]
    Azure_OneLake,

    /// <summary>
    /// Azure keyvault.
    /// </summary>
    [JsonStringEnumMemberName("AzureKeyVault")]
    AzureKeyVault,

    /// <summary>
    /// AWS S3.
    /// </summary>
    [JsonStringEnumMemberName("Aws_S3")]
    Aws_S3,

    /// <summary>
    /// CGS.
    /// </summary>
    [JsonStringEnumMemberName("Cgs")]
    Cgs,

    /// <summary>
    /// Azure container registry.
    /// </summary>
    [JsonStringEnumMemberName("AzureContainerRegistry")]
    AzureContainerRegistry,
}
