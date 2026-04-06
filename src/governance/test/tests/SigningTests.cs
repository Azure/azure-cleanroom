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
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class SigningTests : TestBase
{
    [TestMethod]
    public async Task SignPayload()
    {
        string contractId = this.ContractId;
        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);
        await this.ProposeAndAcceptEnableSigning();

        if (this.IsGitHubActionsEnv())
        {
            // Attempting to sign before signing key was generated should fail.
            using (HttpRequestMessage request = new(HttpMethod.Post, "signing/sign"))
            {
                request.Content = new StringContent(
                    new JsonObject
                    {
                        ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("foo"))
                    }.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);

                Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
                Assert.AreEqual("SigningKeyNotAvailable", error.Code);
                Assert.AreEqual(
                    "Propose enable_signing and generate signing key before attempting " +
                    "to sign.",
                    error.Message);
            }
        }

        string kid = await this.GenerateSigningKey();
        Assert.IsFalse(string.IsNullOrEmpty(kid));

        // Verify signing info shows enabled and the kid.
        await this.VerifySigningInfo();

        // Get the public key for verification.
        string publicKeyPem = await this.GetSigningPublicKey();
        Assert.IsFalse(string.IsNullOrEmpty(publicKeyPem));

        // Sign a payload via the sidecar and verify the signature.
        string testPayload = "test payload to sign";
        string payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(testPayload));

        using (HttpRequestMessage request = new(HttpMethod.Post, "signing/sign"))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["payload"] = payloadBase64
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

            string signatureBase64 = responseBody["value"]!.ToString();
            Assert.IsFalse(string.IsNullOrEmpty(signatureBase64));

            // Verify the signature using the public key.
            byte[] signature = Convert.FromBase64String(signatureBase64);
            byte[] payloadBytes = Encoding.UTF8.GetBytes(testPayload);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            bool isValid = rsa.VerifyData(
                payloadBytes,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            Assert.IsTrue(isValid, "Signature verification failed.");
        }
    }

    [TestMethod]
    public async Task SignPayloadBadInputs()
    {
        string contractId = this.ContractId;
        string signUrl = $"app/contracts/{contractId}/signing/sign";

        // Signing without presenting any attestation report should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, signUrl))
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
        using (HttpRequestMessage request = new(HttpMethod.Post, signUrl))
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

        // Not sending payload should get caught.
        using (HttpRequestMessage request = new(HttpMethod.Post, signUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "doesnotmatter",
                    ["encrypt"] = "doesnotmatter"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("PayloadMissing", error.Code);
            Assert.AreEqual(
                "Payload to sign must be supplied.",
                error.Message);
        }

        // Try signing with invalid attestation.
        using (HttpRequestMessage request = new(HttpMethod.Post, signUrl))
        {
            // Payload contains invalid attestation report.
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = "invalidinput",
                    ["encrypt"] = "doesnotmatter",
                    ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test"))
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, signUrl))
        {
            // Payload contains valid attestation report but no clean room policy has been proposed
            // yet so sign should fail.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync(
                    "data/encryption/attestation.json"))!["report"]!["snpCACI"]!;
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
                    },
                    ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test"))
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

        using (HttpRequestMessage request = new(HttpMethod.Post, signUrl))
        {
            // Payload contains valid attestation report but public key does not match reportdata.
            var attestationReport = JsonSerializer.Deserialize<JsonObject>(
                await File.ReadAllTextAsync(
                    "data/encryption/attestation.json"))!["report"]!["snpCACI"]!;
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
                    },
                    ["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test"))
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
    }

    [TestMethod]
    public async Task RotateSigningKey()
    {
        string contractId = this.ContractId;
        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);
        await this.ProposeAndAcceptEnableSigning();

        // Generate the first signing key.
        string kid1 = await this.GenerateSigningKey();

        // Verify signing info shows enabled and the kid.
        await this.VerifySigningInfo();

        // Propose and accept key rotation.
        await this.ProposeAndAcceptRotateSigningKey();

        // Generate a new signing key.
        string kid2 = await this.GenerateSigningKey();
        Assert.AreNotEqual(kid1, kid2, "New kid should be different from the old one.");

        // Verify signing info shows the new kid.
        await this.VerifySigningInfo();
    }

    private static X509Certificate2 CreateX509Certificate2(string certName)
    {
        var rsa = RSA.Create();
        var req = new CertificateRequest(
            $"cn={certName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
        return cert;
    }

    private async Task VerifySigningInfo()
    {
        using (HttpRequestMessage request = new(HttpMethod.Post, "signing/info"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.IsTrue(responseBody["enabled"]!.GetValue<bool>());
        }
    }
}
