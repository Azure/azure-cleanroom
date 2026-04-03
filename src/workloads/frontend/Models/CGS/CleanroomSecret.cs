// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class CleanroomSecret
{
    [JsonPropertyName("secretType")]
    public required SecretType SecretType { get; set; }

    [JsonPropertyName("backingResource")]
    public required Resource BackingResource { get; set; }

    public static CleanroomSecret FromDatasetEncryptionSecret(
        DatasetEncryptionSecret datasetEncryptionSecret,
        ProtocolType issuerProtocol)
    {
        return new CleanroomSecret
        {
            SecretType = SecretType.Key,
            BackingResource = Resource.FromDatasetEncryptionSecret(
                datasetEncryptionSecret,
                issuerProtocol),
        };
    }
}
