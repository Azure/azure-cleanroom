// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class SecretTests : TestBase
{
    [TestMethod]
    public async Task InsertAndGetSecret()
    {
        string contractId = this.ContractId;
        string secretName = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"secretName: {secretName}");
        string secretNameUrl = $"contracts/{contractId}/secrets/{secretName}";

        // Add a secret as member0.
        string secretId;
        using (HttpRequestMessage request = new(HttpMethod.Put, secretNameUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = "somesecret"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(secretId));
        }

        // Getting secret before a clean room policy is set should fail.
        string secretIdUrl = $"secrets/{secretId}";
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted above get secret
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }
    }

    [TestMethod]
    public async Task InsertAndGetSecretWithMultipleHostData()
    {
        string contractId = this.ContractId;
        string secretName = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"secretName: {secretName}");
        string secretNameUrl = $"contracts/{contractId}/secrets/{secretName}";

        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // Add a secret as member0.
        string secretId;
        using (HttpRequestMessage request = new(HttpMethod.Put, secretNameUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = "somesecret"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(secretId));
        }

        // Setup secret level policy (hostData2) with duplicates to check if the CGS will de-dup it.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy"))
        {
            var addPolicy = new JsonObject
            {
                ["type"] = "add",
                ["claims"] = new JsonObject
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] = new JsonArray(
                        this.GovSidecarHostData,
                        this.GovSidecarHostData,
                        this.GovSidecar2HostData)
                }
            };
            request.Content = new StringContent(
                addPolicy.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            // Check the get response by the user matches with the policy that was set.
            using HttpRequestMessage request2 =
                new(HttpMethod.Get, $"contracts/{contractId}/secrets/{secretId}/cleanroompolicy");
            using HttpResponseMessage response2 = await this.CgsClient_Member0.SendAsync(request2);
            Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
            var responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            JsonObject expectedClaims =
                this.ConvertClaimsToArrayFormat(addPolicy["claims"]!.AsObject());
            this.Logger.LogInformation($"ExpectedClaims: {expectedClaims.ToJsonString()}");
            this.Logger.LogInformation(
                $"ActualClaims: {responseBody["claims"]?.ToJsonString()}");
            var hostDataArray = responseBody["claims"]?["x-ms-sevsnpvm-hostdata"]?.AsArray();
            Assert.IsTrue(
                hostDataArray != null && hostDataArray.Count == 2,
                $"Expected length of hostDataArray to be 1 but got {hostDataArray?.Count}");
            Assert.IsTrue(
                this.JsonNodesEqualIgnoreDuplicates(expectedClaims, responseBody["claims"]),
                "The claims in the response do not match the expected claims.");
        }
    }

    [TestMethod]
    public async Task InsertAndGetSecretPlaceholderContract()
    {
        var govSidecarClient = new HttpClient
        {
            BaseAddress = new Uri(this.Configuration["govSidecarEndpoint"]!)
        };

        // docker-compose setup for ccr-governance is configured with "app/contracts/placeholder"
        // for the api path prefix.
        string contractId = "placeholder";
        string secretName = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"secretName: {secretName}");
        string secretNameUrl = $"contracts/{contractId}/secrets/{secretName}";

        // Add a secret as member0.
        string secretId;
        string secretValue = Guid.NewGuid().ToString().Substring(0, 8);
        using (HttpRequestMessage request = new(HttpMethod.Put, secretNameUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = secretValue
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(secretId));
        }

        // Re-running this test will find an existing clean room policy for the "default" contract
        // so handle that.
        bool cleanRoomPolicyExists;
        using (HttpRequestMessage request =
            new(HttpMethod.Get, $"contracts/{contractId}/cleanroompolicy"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            cleanRoomPolicyExists = responseBody["policy"]!.ToJsonString() != "{}";
        }

        string secretIdUrl = $"secrets/{secretId}";
        if (!cleanRoomPolicyExists)
        {
            // Getting secret before a clean room policy is set should fail.
            using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
            {
                using HttpResponseMessage response = await govSidecarClient.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
                Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
                Assert.AreEqual(
                    "The clean room policy is missing. Please propose a new clean room policy.",
                    error.Message);
            }

            await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);
        }

        // As the contract and clean room policy was proposed and accepted above get secret
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await govSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string value = responseBody["value"]!.ToString();
            Assert.AreEqual(secretValue, value);
        }
    }

    [TestMethod]
    public async Task InsertAndGetSecretBadInputs()
    {
        string contractId = this.ContractId;
        string secretName = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"secretName: {secretName}");

        // Getting a secret directly using the CCF client without presenting any
        // attestation report should fail.
        string dummySecretUrl = $"app/contracts/{contractId}/secrets/doesnotmatter";
        using (HttpRequestMessage request = new(HttpMethod.Post, dummySecretUrl))
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
        using (HttpRequestMessage request = new(HttpMethod.Post, dummySecretUrl))
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

        // Try fetching a secret with invalid attestation.
        using (HttpRequestMessage request = new(HttpMethod.Post, dummySecretUrl))
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

        using (HttpRequestMessage request = new(HttpMethod.Post, dummySecretUrl))
        {
            // Payload contains valid attestation report but no clean room policy has been proposed
            // yet so get secret should fail.
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

        using (HttpRequestMessage request = new(HttpMethod.Post, dummySecretUrl))
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

        // Add a secret as member0.
        // Add a very large secret that cannot get wrapped.
        int maxSecretLength = 25600;
        string longSecret = new('*', maxSecretLength + 1);
        using (HttpRequestMessage request =
            new(HttpMethod.Put, $"contracts/{contractId}/secrets/longSecret"))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = longSecret
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ValueTooLarge", error.Code);
            Assert.AreEqual(
                $"Length of the value should not exceed {maxSecretLength} characters. " +
                $"Input is {maxSecretLength + 1} characters.",
                error.Message);
        }

        // maxSecretLength should work.
        string secretId;
        longSecret = new('*', maxSecretLength);
        using (HttpRequestMessage request =
            new(HttpMethod.Put, $"contracts/{contractId}/secrets/longSecret"))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = longSecret
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(secretId));
        }

        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}"))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual(longSecret, secretValue);
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
    public async Task InsertAndGetUserSecretWithSecretPolicy()
    {
        string contractId = this.ContractId;
        string secretName = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"secretName: {secretName}");
        string secretNameUrl = $"contracts/{contractId}/secrets/{secretName}";

        // Create a user that would create the secret.
        List<(string id, HttpClient userClient)> users = await this.CreateAndAcceptUsers(1);

        // Add a secret as the user.
        string secretId;
        using (HttpRequestMessage request = new(HttpMethod.Put, secretNameUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = "somesecret"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await users[0].userClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(secretId));
        }

        // Querying for the cleanroom policy for a non-existent secret should fail.
        using (HttpRequestMessage request =
            new(HttpMethod.Get, $"contracts/{contractId}/secrets/doesnotexist/cleanroompolicy"))
        {
            using HttpResponseMessage response = await users[0].userClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("SecretNotFound", error.Code);
            Assert.AreEqual(
                "A secret with the specified id 'doesnotexist' was not found.",
                error.Message);
        }

        // Getting secret before a clean room policy is set should fail.
        string secretIdUrl = $"secrets/{secretId}";
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted above get secret
        // should now succeed.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }

        // Setup a secret level policy (hostData2) using ccr-governance running with the
        // contract level clean room policy (hostData1). Then fetch the secret using hostData2.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy"))
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

            // Check the get response by the user matches with the policy that was set.
            using HttpRequestMessage request2 =
                new(HttpMethod.Get, $"contracts/{contractId}/secrets/{secretId}/cleanroompolicy");
            using HttpResponseMessage response2 = await users[0].userClient.SendAsync(request2);
            Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
            var responseBody = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            JsonObject expectedClaims =
                this.ConvertClaimsToArrayFormat(addPolicy["claims"]!.AsObject());
            Assert.IsTrue(
                JsonNode.DeepEquals(expectedClaims, responseBody["claims"]),
                $"Expected: {expectedClaims.ToJsonString()}, " +
                $"actual: {responseBody["claims"]?.ToJsonString()}");
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }

        // Getting the secret via gov sidecar using contract level policy (ie hostData1) should
        // fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
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

        // Removing policy should allow getting the secret via contract level policy (ie hostData1).
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy"))
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
            using HttpRequestMessage request2 =
                new(HttpMethod.Get, $"contracts/{contractId}/secrets/{secretId}/cleanroompolicy");
            using HttpResponseMessage response2 = await users[0].userClient.SendAsync(request2);
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

        // Getting the secret via gov sidecar using hostData2 should fail but start working with
        // hostData1.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
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

        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }
    }

    [TestMethod]
    public async Task InsertAndGetCleanRoomSecretWithSecretPolicy()
    {
        string contractId = this.ContractId;
        string secretName = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"secretName: {secretName}");
        string secretNameUrl = $"secrets/{secretName}";

        // Getting secret before a clean room policy is set should fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, $"secrets/doesnotmatter"))
        {
            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("VerifySnpAttestationFailed", error.Code);
            Assert.AreEqual(
                "The clean room policy is missing. Please propose a new clean room policy.",
                error.Message);
        }

        await this.ProposeContractAndAcceptAllowAllCleanRoomPolicy(contractId);

        // As the contract and clean room policy was proposed and accepted should be able to
        // set a cleanroom secert now.
        string secretId;
        using (HttpRequestMessage request = new(HttpMethod.Put, secretNameUrl))
        {
            request.Content = new StringContent(
                new JsonObject
                {
                    ["value"] = "somesecret"
                }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(secretId));
        }

        // Get secret should now succeed as sidecar 1 as we have not set the secret level policy
        // yet.
        string secretIdUrl = $"secrets/{secretId}";
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }

        // Setup a secret level policy (hostData2) using ccr-governance running with the
        // contract level clean room policy (hostData1). Then fetch the secret using hostData2.
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy"))
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

            // Check the get response by a member matches with the policy that was set.
            using HttpRequestMessage request2 =
                new(HttpMethod.Get, $"contracts/{contractId}/secrets/{secretId}/cleanroompolicy");
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

        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecar2Client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }

        // Getting the secret via gov sidecar using contract level policy (ie hostData1) should
        // fail.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
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

        // Removing policy should allow getting the secret via contract level policy (ie hostData1).
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy"))
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

            // After removing the policy above check the get response by a member matches with the
            // contract level policy now.
            using HttpRequestMessage request2 =
                new(HttpMethod.Get, $"contracts/{contractId}/secrets/{secretId}/cleanroompolicy");
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

        // Getting the secret via gov sidecar using hostData2 should fail but start working with
        // hostData1.
        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
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

        using (HttpRequestMessage request = new(HttpMethod.Post, secretIdUrl))
        {
            using HttpResponseMessage response = await this.GovSidecarClient.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            string secretValue = responseBody["value"]!.ToString();
            Assert.AreEqual("somesecret", secretValue);
        }
    }

    private bool JsonNodesEqualIgnoreDuplicates(JsonNode? node1, JsonNode? node2)
    {
        if (node1 == null || node2 == null)
        {
            return node1 == node2;
        }

        if (node1.GetType() != node2.GetType())
        {
            return false;
        }

        switch (node1)
        {
            case JsonObject obj1 when node2 is JsonObject obj2:
                if (obj1.Count != obj2.Count)
                {
                    return false;
                }

                foreach (var kvp in obj1)
                {
                    if (!obj2.TryGetPropertyValue(kvp.Key, out var otherVal))
                    {
                        return false;
                    }

                    if (!this.JsonNodesEqualIgnoreDuplicates(kvp.Value, otherVal))
                    {
                        return false;
                    }
                }

                return true;

            case JsonArray arr1 when node2 is JsonArray arr2:
                // Compare arrays as sets of unique elements
                var set1 = arr1.Select(x => x?.ToString()).ToHashSet();
                var set2 = arr2.Select(x => x?.ToString()).ToHashSet();
                return set1.SetEquals(set2);

            default:
                return node1.ToJsonString() == node2.ToJsonString();
        }
    }
}