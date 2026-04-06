// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.RecoveryService.Models;

public class RecoveryServiceMember
{
    [JsonPropertyName("signingCert")]
    public string? SigningCert { get; set; }

    [JsonPropertyName("encryptionPublicKey")]
    public string? EncryptionPublicKey { get; set; }

    [JsonPropertyName("recoveryService")]
    public RecoveryServiceEnvironmentInfo? RecoveryServiceEnvInfo { get; set; }

    public class RecoveryServiceEnvironmentInfo
    {
        [JsonPropertyName("hostData")]
        public string? HostData { get; set; }
    }
}
