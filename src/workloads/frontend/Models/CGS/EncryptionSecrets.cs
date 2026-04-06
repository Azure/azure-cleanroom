// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class EncryptionSecrets
{
    [JsonPropertyName("dek")]
    public required EncryptionSecret Dek { get; set; }

    [JsonPropertyName("kek")]
    public EncryptionSecret? Kek { get; set; }

    public static EncryptionSecrets? FromDatasetDetails(
        DatasetDetails datasetDetails)
    {
        if (datasetDetails.Store.EncryptionMode == EncryptionMode.SSE ||
            datasetDetails.Dek == null)
        {
            return null;
        }

        return new EncryptionSecrets
        {
            Dek = EncryptionSecret.FromDatasetEncryptionSecret(
                datasetDetails.Dek,
                ProtocolType.AzureKeyVault_Secret),
            Kek = datasetDetails.Kek != null ?
                EncryptionSecret.FromDatasetEncryptionSecret(
                    datasetDetails.Kek,
                    ProtocolType.AzureKeyVault_SecureKey) :
                null,
        };
    }
}