// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AttestationClient;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using CcfCommon;

namespace Controllers;

public class AkvPolicyStore : IPolicyStore
{
    private const string PolicySignerKeyType = "policy-signer-key";
    private const string PolicySignerNameFormat = "psk-ccf-policy-signer-{0}";
    private const string SignedPolicySecretNameFormat = "sps-ccf-network-policy-{0}";
    private readonly ILogger logger;
    private readonly string akvEndpoint;
    private readonly IKeyStore keyStore;
    private readonly NetworkJoinPolicy initialPolicy;

    public AkvPolicyStore(
        ILogger logger,
        string akvEndpoint,
        string encodedInitialPolicy,
        IKeyStore keyStore)
    {
        if (string.IsNullOrEmpty(akvEndpoint))
        {
            throw new ArgumentNullException(nameof(akvEndpoint));
        }

        if (string.IsNullOrEmpty(encodedInitialPolicy))
        {
            throw new ArgumentNullException(nameof(encodedInitialPolicy));
        }

        if (!akvEndpoint.StartsWith("http"))
        {
            akvEndpoint = "https://" + akvEndpoint;
        }

        var initialPolicyJson = Encoding.UTF8.GetString(
            Convert.FromBase64String(encodedInitialPolicy));
        var initialPolicy =
            JsonSerializer.Deserialize<NetworkJoinPolicy>(initialPolicyJson)!;
        this.ValidateJoinPolicy(initialPolicy);

        this.initialPolicy = initialPolicy;
        this.logger = logger;
        this.akvEndpoint = akvEndpoint;
        this.keyStore = keyStore;
    }

    public async Task SetNetworkJoinPolicy(NetworkJoinPolicy joinPolicy)
    {
        this.logger.LogInformation($"NetworkJoinPolicy: {JsonSerializer.Serialize(joinPolicy)}");
        this.ValidateJoinPolicy(joinPolicy);

        var signer = await this.GetOrCreatePolicySigner();
        var signingKey = await this.GetPolicySigningKey();

        var joinPolicyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joinPolicy));
        var signature = ECDsaSigning.SignData(joinPolicyBytes, signingKey);
        using var cert = X509Certificate2.CreateFromPem(signer.Certificate);
        var signerId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();

        var creds = new DefaultAzureCredential();
        await this.SetSignedPolicyDocument(creds, new NetworkSecuritySignedPolicy
        {
            Policy = Convert.ToBase64String(joinPolicyBytes),
            Signatures = new()
            {
                {
                    signerId, Convert.ToBase64String(signature)
                }
            }
        });
    }

    public async Task<NetworkJoinPolicy> GetNetworkJoinPolicy()
    {
        NetworkJoinPolicy? joinPolicy;
        var creds = new DefaultAzureCredential();
        NetworkSecuritySignedPolicy? signedPolicy = await this.GetPolicyDocument(creds);
        if (!string.IsNullOrEmpty(signedPolicy?.Policy))
        {
            var policyBytes = Convert.FromBase64String(signedPolicy.Policy);
            var policyJson = Encoding.UTF8.GetString(policyBytes);
            joinPolicy = JsonSerializer.Deserialize<NetworkJoinPolicy>(policyJson)!;
            this.ValidateJoinPolicy(joinPolicy);
        }
        else
        {
            joinPolicy = this.initialPolicy;
        }

        return joinPolicy;
    }

    public async Task<NetworkSecurityPolicy> GetSecurityPolicy()
    {
        var creds = new DefaultAzureCredential();
        var signedPolicy = await this.GetPolicyDocument(creds);
        return new NetworkSecurityPolicy
        {
            SignedPolicy = signedPolicy ?? new NetworkSecuritySignedPolicy(),
            Signers = signedPolicy != null ? [await this.GetPolicySigner()] : []
        };
    }

    private async Task<NetworkSecuritySignedPolicy?> GetPolicyDocument(TokenCredential creds)
    {
        var secretClient = new SecretClient(new Uri(this.akvEndpoint), creds);
        try
        {
            var secretName = await this.SignedPolicySecretName();
            var secret = await secretClient.GetSecretAsync(secretName);
            var value = secret.Value.Value;
            var signedPolicy = JsonSerializer.Deserialize<NetworkSecuritySignedPolicy>(value)!;

            // Ensure value from key vault is signed by the expected signers as this could have
            // been altered in KV externally.
            await this.ValidateSignedPolicy(signedPolicy);
            return signedPolicy;
        }
        catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task SetSignedPolicyDocument(
        TokenCredential creds,
        NetworkSecuritySignedPolicy signedPolicy)
    {
        var secretName = await this.SignedPolicySecretName();
        var secretClient = new SecretClient(new Uri(this.akvEndpoint), creds);
        var value = JsonSerializer.Serialize(signedPolicy);
        await secretClient.SetSecretAsync(new KeyVaultSecret(secretName, value));
    }

    private async Task ValidateSignedPolicy(NetworkSecuritySignedPolicy signedPolicy)
    {
        if (signedPolicy == null)
        {
            throw new ApiException(
                code: "BadInput",
                message: "signedPolicy input is missing.");
        }

        var expectedSignatures = 1;
        if (signedPolicy.Signatures.Count != expectedSignatures)
        {
            throw new ApiException(
                code: "InvalidSignaturesCount",
                message: $"{expectedSignatures} signature(s) are " +
                $"required but policy contains {signedPolicy.Signatures.Count} signature(s).");
        }

        byte[] policyBytes;
        string? policyJson = null;
        try
        {
            policyBytes = Convert.FromBase64String(signedPolicy.Policy);
            policyJson = Encoding.UTF8.GetString(policyBytes);
            var joinPolicy = JsonSerializer.Deserialize<NetworkJoinPolicy>(policyJson)!;
            this.ValidateJoinPolicy(joinPolicy);
        }
        catch (Exception e)
        {
            throw new ApiException(
                code: "InvalidNetworkSecurityPolicyContent",
                message: $"Content: {policyJson}, Message: {e.Message}");
        }

        var signer = await this.GetPolicySigner();
        using var cert = X509Certificate2.CreateFromPem(signer.Certificate);
        string signerId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();
        string certName = cert.Subject;
        if (!signedPolicy.Signatures.TryGetValue(
            signerId,
            out var signature))
        {
            throw new ApiException(
                code: "MissingSigner",
                message: $"Signer {signerId} for cert '{certName}' is not present " +
                $"in signatures.");
        }

        var signatureBytes = Convert.FromBase64String(signature);
        if (!ECDsaSigning.VerifyDataUsingCert(
            policyBytes,
            signatureBytes,
            cert))
        {
            throw new ApiException(
                code: "BadSignature",
                message: $"Signature for signer {signerId} over the policy is " +
                $"invalid.");
        }
    }

    private void ValidateJoinPolicy(NetworkJoinPolicy? policy)
    {
        if (policy == null)
        {
            throw new ApiException(
                code: "PolicyMissing",
                message: "CCF NetworkJoinPolicy must be supplied.");
        }

        if (policy.Snp == null)
        {
            throw new ApiException(
                code: "SnpKeyMissing",
                message: "snp key is missing");
        }

        if (policy.Snp.HostData == null || policy.Snp.HostData.Count == 0)
        {
            throw new ApiException(
                code: "HostDataKeyMissing",
                message: "snp.hostData value is missing");
        }

        if (policy.Snp.HostData.Any(x => x.Length != 64))
        {
            throw new ApiException(
                code: "InvalidHostData",
                message: "hostData hex string must have 64 characters.");
        }
    }

    private async Task<NetworkSecurityPolicySigner> GetPolicySigner()
    {
        var signingKeyInfo = await this.keyStore.GetSigningKey(
            await this.PolicySignerKeyName());
        if (signingKeyInfo == null)
        {
            throw new ApiException("PolicySignerNotFound", "No policy signer exists.");
        }

        return new NetworkSecurityPolicySigner
        {
            Certificate = signingKeyInfo.SigningCert
        };
    }

    private async Task<NetworkSecurityPolicySigner> GetOrCreatePolicySigner()
    {
        var signingKeyInfo = await this.keyStore.GenerateSigningKey(
            await this.PolicySignerKeyName(),
            PolicySignerKeyType,
            tags: new());
        return new NetworkSecurityPolicySigner
        {
            Certificate = signingKeyInfo.SigningCert
        };
    }

    private async Task<string> GetPolicySigningKey()
    {
        var signingKeyInfo = await this.keyStore.ReleaseSigningKey(
            await this.PolicySignerKeyName());
        return signingKeyInfo.SigningKey;
    }

    private async Task<string> PolicySignerKeyName()
    {
        var hostData = await Attestation.GetHostData();
        return string.Format(PolicySignerNameFormat, hostData);
    }

    private async Task<string> SignedPolicySecretName()
    {
        var hostData = await Attestation.GetHostData();
        return string.Format(SignedPolicySecretNameFormat, hostData);
    }
}
