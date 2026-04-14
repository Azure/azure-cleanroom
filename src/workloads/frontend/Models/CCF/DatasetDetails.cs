// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models.CCF;

[RequireIdentityForAzureStore]
[RequiredSecretsAndConfigurations]
public class DatasetDetails
{
    [JsonPropertyName("name")]
    [RequiredNotNullOrWhiteSpace]
    public required string Name { get; set; }

    [JsonPropertyName("datasetSchema")]
    public required DataSchema DatasetSchema { get; set; }

    [JsonPropertyName("datasetAccessPolicy")]
    public required DataAccessPolicy DatasetAccessPolicy { get; set; }

    [JsonPropertyName("dek")]
    public DatasetEncryptionSecret? Dek { get; set; }

    [JsonPropertyName("kek")]
    public DatasetEncryptionSecret? Kek { get; set; }

    [JsonPropertyName("identity")]
    public DatasetIdentity? Identity { get; set; }

    [JsonPropertyName("store")]
    public required DatasetStore Store { get; set; }

    public static DatasetDetails FromDatasetSpecification(
        DatasetSpecification datasetSpecification)
    {
        return new DatasetDetails
        {
            Name = datasetSpecification.Name,
            DatasetSchema = datasetSpecification.DatasetSchema,
            DatasetAccessPolicy = datasetSpecification.DatasetAccessPolicy,
            Store = DatasetStore.FromDatasetAccessPoint(
                datasetSpecification.DatasetAccessPoint),
            Identity = DatasetIdentity.FromIdentity(
                datasetSpecification.DatasetAccessPoint.Identity),
            Dek = DatasetEncryptionSecret.FromDatasetDetails(
                datasetSpecification.DatasetAccessPoint.Protection.EncryptionSecrets?.Dek),
            Kek = DatasetEncryptionSecret.FromDatasetDetails(
                datasetSpecification.DatasetAccessPoint.Protection.EncryptionSecrets?.Kek),
        };
    }
}
