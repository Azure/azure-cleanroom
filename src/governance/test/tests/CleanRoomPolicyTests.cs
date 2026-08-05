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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class CleanRoomPolicyTests : TestBase
{
    [TestMethod]
    public async Task ProposeCleanRoomPolicyInvalidScenarios()
    {
        // Proposing a policy for a non-existent contract should fail.
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/cleanroompolicy/propose"))
        {
            var proposalContent = new JsonObject
            {
                ["type"] = "add",
                ["policyType"] = "snp-caci",
                ["contractId"] = contractId,
                ["claims"] = new JsonObject
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] =
                        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"
                }
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ProposalFailedToValidate", error.Code);
            Assert.Contains(
                "Proposal failed to validate: set_clean_room_policy at position 0 failed " +
                "validation: Error: Clean Room Policy can only be proposed once contract " +
                $"'{contractId}' has been accepted.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task SetDelegatePolicyRequiresUvmEndorsements()
    {
        string contractId = this.ContractId;
        string setDelegatePolicyUrl =
            $"app/contracts/{contractId}/cleanroompolicy/delegates/events/writer";

        string?[] invalidUvmEndorsements = [null, string.Empty, " "];
        foreach (string? invalidUvmEndorsement in invalidUvmEndorsements)
        {
            JsonObject attestation = await GetSnpCaciAttestationAsync();
            if (invalidUvmEndorsement == null)
            {
                attestation.Remove("uvm_endorsements");
            }
            else
            {
                attestation["uvm_endorsements"] = invalidUvmEndorsement;
            }

            using HttpRequestMessage request = new(HttpMethod.Put, setDelegatePolicyUrl);
            request.Content = new StringContent(
                new JsonObject
                {
                    ["attestation"] = attestation,
                    ["encrypt"] = new JsonObject
                    {
                        ["publicKey"] = "doesnotmatter"
                    },
                    ["sign"] = new JsonObject
                    {
                        ["publicKey"] = "doesnotmatter",
                        ["signature"] = "doesnotmatter"
                    },
                    ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "'uvm_endorsements' must be supplied for snp-caci attestation.",
                error.Message);
        }
    }

    [TestMethod]
    public async Task SetDelegatePolicyRejectsSigningKeyMismatch()
    {
        string contractId = this.ContractId;
        string setDelegatePolicyUrl =
            $"app/contracts/{contractId}/cleanroompolicy/delegates/events/writer";

        // Setup: propose and accept a contract and an allow-all clean room policy so that the
        // attestation verification stage will succeed and control can reach the signing-key
        // check that we want to exercise.
        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // Read the encryption public key whose SHA-256 matches the report_data value in the
        // sample attestation. Normalize line-endings so the check passes when the test is run
        // on Windows too (existing tests in EventTests.cs do the same).
        string encryptPublicKeyPem =
            (await File.ReadAllTextAsync("data/encryption/pub_key.pem"))!
                .Replace("\r\n", "\n");
        string encryptPublicKeyBase64 =
            Convert.ToBase64String(Encoding.UTF8.GetBytes(encryptPublicKeyPem));

        // The request contains a valid attestation and an encryption public key whose hash
        // matches the report_data, so attestation verification succeeds. The supplied signing
        // key is a completely unrelated key that was never bound to any attestation, so the
        // endpoint must reject with SigningKeyMismatch.
        using HttpRequestMessage request = new(HttpMethod.Put, setDelegatePolicyUrl);
        var attackerSigningPublicKey =
            CreateX509Certificate2("attacker").PublicKey.ExportSubjectPublicKeyInfo();
        var attackerSigningPublicKeyPem =
            PemEncoding.Write("PUBLIC KEY", attackerSigningPublicKey);

        request.Content = new StringContent(
            new JsonObject
            {
                ["attestation"] = await GetSnpCaciAttestationAsync(),
                ["encrypt"] = new JsonObject
                {
                    ["publicKey"] = encryptPublicKeyBase64
                },
                ["sign"] = new JsonObject
                {
                    ["publicKey"] = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(attackerSigningPublicKeyPem)),
                    ["signature"] = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes("does-not-matter"))
                },
                ["data"] = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes("{\"type\":\"add\",\"policyType\":\"snp-caci\"}"))
            }.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
        Assert.AreEqual("SigningKeyMismatch", error.Code);
        Assert.AreEqual(
            "Signing key must be the same as the encryption key in the report data.",
            error.Message);

        static X509Certificate2 CreateX509Certificate2(string certName)
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
    }
}