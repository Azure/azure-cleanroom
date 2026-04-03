// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Controllers;
using FrontendSvc.Models.CCF;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Models;

public class ServiceEndpoint
{
    [JsonPropertyName("protocol")]
    public required ProtocolType Protocol { get; set; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = string.Empty;

    public static ServiceEndpoint FromDatasetStore(DatasetStore datasetStore)
    {
        return new ServiceEndpoint
        {
            Configuration = GetServiceEndpointConfiguration(datasetStore.AWSCgsSecretId),
            Protocol = GetServiceEndpointProtocol(datasetStore.StorageAccountType),
            Url = datasetStore.StorageAccountUrl,
        };
    }

    private static ProtocolType GetServiceEndpointProtocol(ResourceType storeType)
    {
        if (storeType == ResourceType.Azure_BlobStorage)
        {
            return ProtocolType.Azure_BlobStorage;
        }
        else if (storeType == ResourceType.Azure_BlobStorage_DataLakeGen2)
        {
            return ProtocolType.Azure_BlobStorage_DataLakeGen2;
        }
        else if (storeType == ResourceType.Azure_OneLake)
        {
            return ProtocolType.Azure_OneLake;
        }
        else if (storeType == ResourceType.Aws_S3)
        {
            return ProtocolType.Aws_S3;
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

    private static string GetServiceEndpointConfiguration(string awsCgsSecretId)
    {
        if (!string.IsNullOrWhiteSpace(awsCgsSecretId))
        {
            var configurationJson = new JsonObject()
            {
                ["secretId"] = awsCgsSecretId,
            }.ToJsonString();

            return Base64.Encode(configurationJson);
        }

        return awsCgsSecretId;
    }
}
