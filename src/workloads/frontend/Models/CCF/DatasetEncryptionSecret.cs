// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Models.CCF;

public class DatasetEncryptionSecret
{
    [JsonPropertyName("secretId")]
    public required string SecretId { get; set; }

    [JsonPropertyName("keyVaultUrl")]
    public required string KeyVaultUrl { get; set; }

    [JsonPropertyName("maaUrl")]
    public string? MaaUrl { get; set; }

    public static DatasetEncryptionSecret? FromDatasetDetails(
            EncryptionSecret? encryptionSecret)
    {
        if (encryptionSecret == null)
        {
            return default;
        }

        return new DatasetEncryptionSecret
        {
            SecretId = encryptionSecret.Name,
            KeyVaultUrl = encryptionSecret.Secret.BackingResource.Provider.Url,
            MaaUrl = GetMaaUrl(encryptionSecret.Secret.BackingResource.Provider.Configuration),
        };
    }

    private static string? GetMaaUrl(string? providerConfiguration)
    {
        if (string.IsNullOrWhiteSpace(providerConfiguration))
        {
            return default;
        }

        var decodedJsonStringConfiguration = Base64.Decode(providerConfiguration);
        var jsonConfiguration = JsonSerializer.Deserialize<JsonObject>(
            decodedJsonStringConfiguration);
        return jsonConfiguration!["authority"]?.ToString();
    }
}