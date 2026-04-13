// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Models.CCF;

[RequireAWSSecretForAWSS3]
public class DatasetStore
{
    [JsonPropertyName("containerName")]
    [RequiredNotNullOrWhiteSpace]
    public required string ContainerName { get; set; }

    [JsonPropertyName("storageAccountUrl")]
    [RequiredNotNullOrWhiteSpace]
    public required string StorageAccountUrl { get; set; }

    [JsonPropertyName("storageAccountType")]
    public required ResourceType StorageAccountType { get; set; }

    [JsonPropertyName("encryptionMode")]
    public required EncryptionMode EncryptionMode { get; set; }

    [JsonPropertyName("awsCgsSecretId")]
    public string AWSCgsSecretId { get; set; } = string.Empty;

    public static DatasetStore FromDatasetAccessPoint(
        AccessPoint datasetAccessPoint)
    {
        var awsCgsSecretId = GetAwsSecretId(
            datasetAccessPoint.Store.Provider.Configuration) ?? string.Empty;

        return new DatasetStore
        {
            ContainerName = datasetAccessPoint.Store.Id,
            StorageAccountUrl = datasetAccessPoint.Store.Provider.Url,
            StorageAccountType = datasetAccessPoint.Store.Type,
            EncryptionMode = GetEncryptionMode(datasetAccessPoint.Protection.Configuration),
            AWSCgsSecretId = awsCgsSecretId,
        };
    }

    private static EncryptionMode GetEncryptionMode(string protectionConfiguration)
    {
        var decodedProtectionConfigurationJsonString = Base64.Decode(protectionConfiguration);
        var protectionConfigurationJson = JsonSerializer.Deserialize<JsonObject>(
            decodedProtectionConfigurationJsonString);

        return Enum.Parse<EncryptionMode>(
            protectionConfigurationJson!["EncryptionMode"]!.ToString());
    }

    private static string? GetAwsSecretId(string providerConfiguration)
    {
        if (string.IsNullOrWhiteSpace(providerConfiguration))
        {
            return default;
        }

        var decodedJsonStringConfiguration = Base64.Decode(providerConfiguration);
        var jsonConfiguration = JsonSerializer.Deserialize<JsonObject>(
            decodedJsonStringConfiguration);

        return jsonConfiguration!["secretId"]!.ToString();
    }
}