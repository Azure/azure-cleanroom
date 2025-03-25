// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Security.KeyVault.Certificates;

namespace CoseUtils;

public class CoseSignKey
{
    public CoseSignKey(string certificate, string privateKey)
    {
        this.Certificate = certificate;
        this.PrivateKey = privateKey;
    }

    public CoseSignKey(
        KeyVaultCertificate certificate,
        TokenCredential tokenCredential)
    {
        this.Certificate = new X509Certificate2(certificate.Cer).ExportCertificatePem();
        this.KvCertificate = certificate;
        this.TokenCredential = tokenCredential;
    }

    public string Certificate { get; } = default!;

    public string PrivateKey { get; } = default!;

    public KeyVaultCertificate? KvCertificate { get; } = default!;

    public TokenCredential TokenCredential { get; } = default!;

    public static async Task<CoseSignKey> FromKeyVault(Uri signingCertId, TokenCredential creds)
    {
        var akvEndpoint = "https://" + signingCertId.Host;
        var certClient = new CertificateClient(new Uri(akvEndpoint), creds);

        // certificates/{name} or certificates/{name}/{version}
        var parts = signingCertId.AbsolutePath.Split("/", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 && parts.Length != 3)
        {
            throw new ArgumentException(
                $"Expecting signingCertId '{signingCertId}' to be of the form: " +
                "<vaultname>.vault.azure.net/certificates/{certname} or " +
                "<vaultname>.vault.azure.net/certificates/{certname}/{version}");
        }

        var certName = parts[1];
        KeyVaultCertificate certificate;
        if (parts.Length == 2)
        {
            certificate = await certClient.GetCertificateAsync(certName);
        }
        else
        {
            string version = parts[2];
            certificate = await certClient.GetCertificateVersionAsync(certName, version);
        }

        return new CoseSignKey(certificate, creds);
    }
}