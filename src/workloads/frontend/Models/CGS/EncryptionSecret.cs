// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class EncryptionSecret
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("secret")]
    public required CleanroomSecret Secret { get; set; }

    public static EncryptionSecret FromDatasetEncryptionSecret(
        DatasetEncryptionSecret datasetEncryptionSecret,
        ProtocolType issuerProtocol)
    {
        return new EncryptionSecret
        {
            Name = datasetEncryptionSecret.SecretId,
            Secret = CleanroomSecret.FromDatasetEncryptionSecret(
                datasetEncryptionSecret,
                issuerProtocol),
        };
    }
}
