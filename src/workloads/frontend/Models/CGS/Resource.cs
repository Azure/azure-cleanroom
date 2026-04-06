// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Models;

public class Resource
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required ResourceType Type { get; set; }

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("provider")]
    public required ServiceEndpoint Provider { get; set; }

    public static Resource FromDatasetStore(
        DatasetStore datasetStore)
    {
        return new Resource
        {
            Id = datasetStore.ContainerName,
            Name = datasetStore.ContainerName,
            Type = datasetStore.StorageAccountType,
            Provider = ServiceEndpoint.FromDatasetStore(datasetStore),
        };
    }

    public static Resource FromDatasetEncryptionSecret(
        DatasetEncryptionSecret datasetEncryptionSecret,
        ProtocolType protocol)
    {
        return new Resource
        {
            Id = datasetEncryptionSecret.SecretId,
            Name = datasetEncryptionSecret.SecretId,
            Type = ResourceType.AzureKeyVault,
            Provider = new ServiceEndpoint
            {
                Protocol = protocol,
                Url = datasetEncryptionSecret.KeyVaultUrl,
                Configuration = GetEncryptionKeyProviderConfiguration(
                    datasetEncryptionSecret.MaaUrl!),
            },
        };
    }

    private static string GetEncryptionKeyProviderConfiguration(string maaUrl)
    {
        if (string.IsNullOrWhiteSpace(maaUrl))
        {
            return string.Empty;
        }

        var configurationJson = new JsonObject
        {
            ["authority"] = maaUrl,
        }.ToJsonString();

        return Base64.Encode(configurationJson);
    }
}
