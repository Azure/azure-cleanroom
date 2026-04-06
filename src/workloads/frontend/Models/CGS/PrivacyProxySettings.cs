// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Controllers;
using FrontendSvc.Models.CCF;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Models;

public class PrivacyProxySettings
{
    [JsonPropertyName("proxyType")]
    public required ProxyType ProxyType { get; set; }

    [JsonPropertyName("proxyMode")]
    public required ProxyMode ProxyMode { get; set; }

    [JsonPropertyName("privacyPolicy")]
    public Policy? PrivacyPolicy { get; set; }

    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = string.Empty;

    [JsonPropertyName("encryptionSecrets")]
    public EncryptionSecrets? EncryptionSecrets { get; set; }

    [JsonPropertyName("encryptionSecretAccessIdentity")]
    public Identity? EncryptionSecretAccessIdentity { get; set; }

    public static PrivacyProxySettings FromDatasetDetails(
        DatasetDetails datasetDetails)
    {
        return new PrivacyProxySettings
        {
            ProxyType = GetProxyType(
                datasetDetails.Store.StorageAccountType,
                datasetDetails.DatasetAccessPolicy.AccessMode),
            ProxyMode = ProxyMode.Secure,
            Configuration = GetConfiguration(
                datasetDetails.Store.EncryptionMode),
            EncryptionSecrets = EncryptionSecrets.FromDatasetDetails(
                datasetDetails),
            EncryptionSecretAccessIdentity = Identity.FromDatasetIdentity(
                datasetDetails.Identity),
        };
    }

    private static ProxyType GetProxyType(ResourceType storeType, AccessMode accessMode)
    {
        if (storeType == ResourceType.Azure_BlobStorage)
        {
            return accessMode == AccessMode.Read ?
                ProxyType.SecureVolume__ReadOnly__Azure__BlobStorage :
                ProxyType.SecureVolume__ReadWrite__Azure__BlobStorage;
        }
        else if (storeType == ResourceType.Azure_BlobStorage_DataLakeGen2)
        {
            return accessMode == AccessMode.Read ?
                ProxyType.SecureVolume__ReadOnly__Azure__BlobStorage__DataLakeGen2 :
                ProxyType.SecureVolume__ReadWrite__Azure__BlobStorage__DataLakeGen2;
        }
        else if (storeType == ResourceType.Azure_OneLake)
        {
            return accessMode == AccessMode.Read ?
                ProxyType.SecureVolume__ReadOnly__Azure__OneLake :
                ProxyType.SecureVolume__ReadWrite__Azure__OneLake;
        }
        else if (storeType == ResourceType.Aws_S3)
        {
            return accessMode == AccessMode.Read ?
                ProxyType.SecureVolume__ReadOnly__Aws__S3 :
                ProxyType.SecureVolume__ReadWrite__Aws__S3;
        }
        else
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    "UnsupportedStoreType",
                    $"Unsupported store type: {storeType}"));
        }
    }

    private static string GetConfiguration(EncryptionMode encryptionMode)
    {
        var configuration = new JsonObject
        {
            ["KeyType"] = "KEK",
            ["EncryptionMode"] = encryptionMode.ToString(),
        };
        var configurationString = configuration.ToJsonString();
        return Base64.Encode(configurationString);
    }
}
