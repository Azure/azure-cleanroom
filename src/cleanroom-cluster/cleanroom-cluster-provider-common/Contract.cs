// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class Contract
{
    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

public class ContractData
{
    [JsonPropertyName("ccrgovEndpoint")]
    public string CcrgovEndpoint { get; set; } = default!;

    [JsonPropertyName("ccrgovApiPathPrefix")]
    public string CcrgovApiPathPrefix { get; set; } = default!;

    [JsonPropertyName("ccrgovServiceCert")]
    public string CcrgovServiceCert { get; set; } = default!;

    [JsonPropertyName("ccrgovServiceCertDiscovery")]
    public ServiceCertDiscoveryInput? CcrgovServiceCertDiscovery { get; set; }

    [JsonPropertyName("ccfNetworkRecoveryMembers")]
    public JsonArray? CcfNetworkRecoveryMembers { get; set; }

    public void Validate()
    {
        if (!string.IsNullOrEmpty(this.CcrgovEndpoint))
        {
            if (string.IsNullOrEmpty(this.CcrgovApiPathPrefix))
            {
                throw new ArgumentException("BadInput: ccrgovApiPathPrefix must be specified in " +
                    "the contract.");
            }

            if (string.IsNullOrEmpty(this.CcrgovServiceCert) &&
                this.CcrgovServiceCertDiscovery == null)
            {
                throw new ArgumentException(
                    "BadInput: Either ccrgovServiceCert or ccrgovServiceCertDiscovery must be " +
                    "specified in the contract.");
            }

            if (this.CcrgovServiceCertDiscovery != null)
            {
                if (!string.IsNullOrEmpty(this.CcrgovServiceCert))
                {
                    throw new ArgumentException("BadInput: ccrgovServiceCert cannot be specified " +
                        "along with certificate discovery inputs in the contract.");
                }

                if (string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.SnpHostData))
                {
                    throw new ArgumentException("BadInput: snpHostData must be specified in " +
                        "the contract.");
                }

                if (!this.CcrgovServiceCertDiscovery.SkipDigestCheck &&
                    string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.ConstitutionDigest))
                {
                    throw new ArgumentException("BadInput: constitutionDigest cannot be " +
                        "specified in the contract.");
                }

                if (!this.CcrgovServiceCertDiscovery.SkipDigestCheck &&
                    string.IsNullOrEmpty(this.CcrgovServiceCertDiscovery.JsappBundleDigest))
                {
                    throw new ArgumentException("BadInput: jsappBundleDigest cannot be specified " +
                        "in the contract.");
                }
            }
        }
    }
}