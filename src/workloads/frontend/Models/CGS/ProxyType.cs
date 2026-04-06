// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

/// <summary>
/// Defines the type of privacy proxy used for data access and operations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyType
{
    /// <summary>
    /// Read-only secure volume proxy for Azure OneLake storage.
    /// Provides secure access to data stored in Microsoft OneLake with read-only permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadOnly__Azure__OneLake")]
    SecureVolume__ReadOnly__Azure__OneLake,

    /// <summary>
    /// Read-only secure volume proxy for Azure Blob Storage.
    /// Provides secure access to data stored in Azure Blob Storage with read-only permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadOnly__Azure__BlobStorage")]
    SecureVolume__ReadOnly__Azure__BlobStorage,

    /// <summary>
    /// Read-only secure volume proxy for Azure Blob Storage Data Lake Gen2.
    /// Provides secure access to data stored in ADLS Gen2 with read-only permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadOnly__Azure__BlobStorage__DataLakeGen2")]
    SecureVolume__ReadOnly__Azure__BlobStorage__DataLakeGen2,

    /// <summary>
    /// Read-only secure volume proxy for AWS S3.
    /// Provides secure access to data stored in Amazon S3 with read-only permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadOnly__Aws__S3")]
    SecureVolume__ReadOnly__Aws__S3,

    /// <summary>
    /// Read-write secure volume proxy for Azure OneLake storage.
    /// Provides secure access to data stored in Microsoft OneLake with read and write permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadWrite__Azure__OneLake")]
    SecureVolume__ReadWrite__Azure__OneLake,

    /// <summary>
    /// Read-write secure volume proxy for Azure Blob Storage.
    /// Provides secure access to data stored in Azure Blob Storage with read and write permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadWrite__Azure__BlobStorage")]
    SecureVolume__ReadWrite__Azure__BlobStorage,

    /// <summary>
    /// Read-write secure volume proxy for Azure Blob Storage Data Lake Gen2.
    /// Provides secure access to data stored in ADLS Gen2 with read and write permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadWrite__Azure__BlobStorage__DataLakeGen2")]
    SecureVolume__ReadWrite__Azure__BlobStorage__DataLakeGen2,

    /// <summary>
    /// Read-write secure volume proxy for AWS S3.
    /// Provides secure access to data stored in Amazon S3 with read and write permissions.
    /// </summary>
    [JsonStringEnumMemberName("SecureVolume__ReadWrite__Aws__S3")]
    SecureVolume__ReadWrite__Aws__S3,

    /// <summary>
    /// Standard API proxy for secure API access.
    /// Provides proxy functionality for regular API endpoints with security controls.
    /// </summary>
    [JsonStringEnumMemberName("API")]
    API,

    /// <summary>
    /// Secure API proxy with enhanced security features.
    /// Provides proxy functionality for API endpoints with additional security and privacy controls.
    /// </summary>
    [JsonStringEnumMemberName("SecureAPI")]
    SecureAPI
}
