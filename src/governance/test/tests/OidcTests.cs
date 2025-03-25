// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class OidcTests : TestBase
{
    [TestMethod]
    public async Task GetIdpToken()
    {
        string contractId = this.ContractId;
        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);
        await this.ProposeAndAcceptEnableOidcIssuer();

        string sub = contractId;
        string query = $"?&sub={sub}&tenantId={MsTenantId}&aud=api://AzureADTokenExchange";
        if (this.IsGitHubActionsEnv())
        {
            // Attempting to get a token before signing key was generated should fail.
            using (HttpRequestMessage request = new(HttpMethod.Post, $"oauth/token{query}"))
            {
                using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
                Assert.AreEqual("SigningKeyNotAvailable", error.Code);
                Assert.AreEqual(
                    "Propose enable_oidc_issuer and generate signing key before attempting to " +
                    "fetch it.",
                    error.Message);
            }
        }

        string kid = await this.GenerateOidcIssuerSigningKey();

        if (this.IsGitHubActionsEnv())
        {
            // Attempting to get a token without setting issuerUrl should fail.
            using (HttpRequestMessage request = new(HttpMethod.Post, $"oauth/token{query}"))
            {
                using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
                Assert.AreEqual("IssuerUrlNotSet", error.Code);
                Assert.AreEqual(
                    $"Issuer url has not been configured for tenant {MsTenantId}. Propose " +
                    $"set_oidc_issuer_url or set the issuer at the tenant level.",
                    error.Message);
            }
        }

        string issuerUrl = "https://foo.bar";
        await this.MemberSetIssuerUrl(Members.Member1, issuerUrl);

        // Get the client assertion (jwt) and validate its structure.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"oauth/token{query}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

            string clientAssertion = responseBody["value"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(clientAssertion));
            var parts = clientAssertion.Split('.');
            Assert.AreEqual(3, parts.Length);

            var header = JsonSerializer.Deserialize<JsonObject>(Base64UrlEncoder.Decode(parts[0]))!;
            var expectedAlgHeader = "PS256";
            Assert.AreEqual(expectedAlgHeader, header["alg"]!.ToString());
            Assert.AreEqual("JWT", header["typ"]!.ToString());
            Assert.AreEqual(kid, header["kid"]!.ToString());

            var claims = JsonSerializer.Deserialize<JsonObject>(Base64UrlEncoder.Decode(parts[1]))!;
            Assert.AreEqual("api://AzureADTokenExchange", claims["aud"]!.ToString());
            Assert.AreEqual(sub, claims["sub"]!.ToString());
            Assert.AreEqual(issuerUrl, claims["iss"]!.ToString());
        }
    }

    [TestMethod]
    public async Task GetIdpTokenBadInputs()
    {
        string contractId = this.ContractId;

        // Getting a token directly using the CCF client without presenting any
        // attestation report should fail.
        string tokenUrl = $"app/contracts/{contractId}/oauth/token";
        using (HttpRequestMessage request = new(HttpMethod.Post, tokenUrl))
        {
            request.Content = new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("AttestationMissing", error.Code);
            Assert.AreEqual(
                "Attestation payload must be supplied.",
                error.Message);
        }

        // Not sending encryption data should get caught.
        using (HttpRequestMessage request = new(HttpMethod.Post, tokenUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    // Payload without "encrypt" key.
                    ["attestation"] = "doesnotmatter"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("EncryptionMissing", error.Code);
            Assert.AreEqual(
                "Encrypt payload must be supplied.",
                error.Message);
        }

        // Try fetching a token with invalid attestation.
        using (HttpRequestMessage request = new(HttpMethod.Post, tokenUrl))
        {
            // Payload contains invalid attestation report.
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "invalidinput",
                    ["encrypt"] = "doesnotmatter"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, tokenUrl))
        {
            // Payload contains valid attestation report but no clean room policy has been proposed
            // yet so get token should fail.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync("data/attestation-report.json"))!;
            var publicKey = CreateX509Certificate2("foo").PublicKey.ExportSubjectPublicKeyInfo();
            var publicKeyPem = PemEncoding.Write("PUBLIC KEY", publicKey);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = new JsonObject
                    {
                        ["evidence"] = attestationReport["attestation"]!.ToString(),
                        ["endorsements"] = attestationReport["platformCertificates"]!.ToString(),
                        ["uvm_endorsements"] = attestationReport["uvmEndorsements"]!.ToString(),
                    },
                    ["encrypt"] = new JsonObject
                    {
                        ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKeyPem))
                    }
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        // Propose and accept the contract and cleanroom policy before testing the next set
        // of scenarios.
        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        using (HttpRequestMessage request = new(HttpMethod.Post, tokenUrl))
        {
            // Payload contains valid attestation report but public key does not match reportdata.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync("data/attestation-report.json"))!;
            var publicKey = CreateX509Certificate2("foo").PublicKey.ExportSubjectPublicKeyInfo();
            var publicKeyPem = PemEncoding.Write("PUBLIC KEY", publicKey);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = new JsonObject
                    {
                        ["evidence"] = attestationReport["attestation"]!.ToString(),
                        ["endorsements"] = attestationReport["platformCertificates"]!.ToString(),
                        ["uvm_endorsements"] = attestationReport["uvmEndorsements"]!.ToString(),
                    },
                    ["encrypt"] = new JsonObject
                    {
                        ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKeyPem))
                    }
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ReportDataMismatch", error.Code);
            Assert.AreEqual(
                "Attestation report_data value did not match calculated value.",
                error.Message);
        }

        static X509Certificate2 CreateX509Certificate2(string certName)
        {
            var rsa = RSA.Create();
            var req = new CertificateRequest(
                $"cn={certName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
            return new X509Certificate2(
                cert.Export(X509ContentType.Pfx, string.Empty), string.Empty);
        }
    }
}