// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class SubmitRecoveryShareInput
{
    public string InfraType { get; set; } = default!;

    // PEM encoded private key string.
    public string? EncryptionPrivateKey { get; set; } = default!;

    // Key Vault URI of the private key.
    public string? EncryptionKeyId { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}