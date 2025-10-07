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
                    $"Issuer url has not been supplied nor configured for tenant {MsTenantId}. " +
                    $"Either pass in the 'iss' value or propose " +
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

        // Try a custom iss value and not what is set for the member above.
        string customIssuerUrl = "https://some.endpoint.com";
        query += $"&iss={customIssuerUrl}";
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
            Assert.AreEqual(customIssuerUrl, claims["iss"]!.ToString());
        }
    }

    [TestMethod]
    public async Task GetIdpTokenBadInputs()
    {
        string contractId = this.ContractId;

        // Getting a token directly using the CCF client without presenting any
        // attestation report should fail.
        string query = $"?&sub=foo&tid=foo&aud=foo&nbf=foo&exp=foo&iat=foo&jti=foo";
        string tokenUrl = $"app/contracts/{contractId}/oauth/token" + query;
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

    [TestMethod]
    public async Task GetIdpTokenWithSubjectPolicy()
    {
        string contractId = this.ContractId;

        await this.ProposeContractAndAcceptCleanRoomPolicy(contractId, this.GovSidecarHostData);
        await this.ProposeAndAcceptEnableOidcIssuer();
        string kid = await this.GenerateOidcIssuerSigningKey();
        string issuerUrl = "https://foo.bar";
        await this.MemberSetIssuerUrl(Members.Member1, issuerUrl);
        string sub = "cleanroom-azure-analytics";
        string policyUrl =
            $"contracts/{contractId}/oauth/federation/subjects/{sub}/cleanroompolicy";

        // Setup a subject level policy (hostData2) using ccr-governance running with the
        // contract level clean room policy (hostData1). Then request token using hostData2.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"oauth/federation/subjects/{sub}/cleanroompolicy"))
        {
            var addPolicy = new JsonObject
            {
                ["type"] = "add",
                ["claims"] = new JsonObject
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] = this.GovSidecar2HostData
                }
            };
            request.Content = new StringContent(
                addPolicy.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            // Check the get response matches with the policy that was set.
            using HttpRequestMessage request2 = new(HttpMethod.Get, policyUrl);
            using HttpResponseMessage response2 = await this.CgsClient_Member0.SendAsync(request2);
            Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
            var responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            JsonObject expectedClaims =
                this.ConvertClaimsToArrayFormat(addPolicy["claims"]!.AsObject());
            Assert.IsTrue(
                JsonNode.DeepEquals(expectedClaims, responseBody["claims"]),
                $"Expected: {expectedClaims.ToJsonString()}, " +
                $"actual: {responseBody["claims"]?.ToJsonString()}");
        }

        string query = $"?&sub={sub}&tenantId={MsTenantId}&aud=api://AzureADTokenExchange";

        // Get the client assertion (jwt) using hostData2 client and validate its structure.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"oauth/token{query}"))
        {
            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
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

        // Getting the client assertion (jwt) via gov sidecar using contract level
        // policy (ie hostData1) should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"oauth/token{query}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                $"Attestation claim x-ms-sevsnpvm-hostdata, value {this.GovSidecarHostData} " +
                $"does not match policy values: {this.GovSidecar2HostData}",
                error.Message);
        }

        // Removing policy should allow getting the client assertion (jwt) contract
        // level policy (ie hostData1).
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"oauth/federation/subjects/{sub}/cleanroompolicy"))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["type"] = "remove",
                    ["claims"] = new JsonObject
                    {
                        ["x-ms-sevsnpvm-is-debuggable"] = false,
                        ["x-ms-sevsnpvm-hostdata"] = this.GovSidecar2HostData
                    }
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            // After removing the policy above check the get response by the user matches with the
            // contract level policy now.
            using HttpRequestMessage request2 = new(HttpMethod.Get, policyUrl);
            using HttpResponseMessage response2 = await this.CgsClient_Member0.SendAsync(request2);
            Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
            var responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            JsonObject expectedClaims = this.ConvertClaimsToArrayFormat(new()
            {
                ["x-ms-sevsnpvm-is-debuggable"] = false,
                ["x-ms-sevsnpvm-hostdata"] = this.GovSidecarHostData
            });
            Assert.IsTrue(
                JsonNode.DeepEquals(expectedClaims, responseBody["claims"]),
                $"Expected: {expectedClaims.ToJsonString()}, " +
                $"actual: {responseBody["claims"]?.ToJsonString()}");
        }

        // Getting the client assertion (jwt) via gov sidecar using custom policy (ie hostData2)
        // should now fail as its access was removed.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"oauth/token{query}"))
        {
            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                $"Attestation claim x-ms-sevsnpvm-hostdata, value {this.GovSidecar2HostData} " +
                $"does not match policy values: {this.GovSidecarHostData}",
                error.Message);
        }

        // Since no subject level policy is now set, access via hostData1 should be possible.
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
}