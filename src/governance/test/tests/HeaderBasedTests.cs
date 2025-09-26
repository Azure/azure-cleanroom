// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Test.ContractTests;

namespace Test;

[TestClass]
public class HeaderBasedTests : TestBase
{
    private const string CcfEndpointHeaderKey = "x-ms-ccf-endpoint";
    private const string ServiceCertHeaderKey = "x-ms-service-cert";

    private string ccfEndpoint = string.Empty;

    /// <summary>
    /// Initialize tests.
    /// </summary>
    [TestInitialize]
    public override void Initialize()
    {
        string? testConfigurationFile = Environment.GetEnvironmentVariable(
            "TEST_CONFIGURATION_FILE");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables();

        if (!string.IsNullOrEmpty(testConfigurationFile))
        {
            configBuilder.AddJsonFile(testConfigurationFile);
        }

        this.Configuration = configBuilder.Build();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        this.Logger = loggerFactory.CreateLogger<EventTests>();

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                return true;
            }
        };

        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        this.Logger.LogInformation($"contractId: {contractId}");
        this.ContractId = contractId;

        this.ccfEndpoint = this.Configuration["headerTesting:ccfEndpoint"]!;
        if (this.IsGitHubActionsEnv() || this.IsCodespacesEnv())
        {
            // 172.17.0.1:
            // https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
            this.ccfEndpoint = "https://172.17.0.1:9001";
        }

        this.CcfClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.ccfEndpoint)
        };

        this.CgsClients = new List<HttpClient>();
        this.CgsClient_Member0 = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["headerTesting:cgsClientEndpoint_member0"]!)
        };
        this.CgsClients.Add(this.CgsClient_Member0);
    }

    [TestMethod]
    public async Task CreateContractWithHeader()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";
        string checkStatusUrl = contractUrl + "/checkstatus/execution";
        string serviceCertPemBase64 = await this.GetServiceCertificateAsync();

        // Contract should not be found as we have not added it yet.
        using (HttpRequestMessage request = new(HttpMethod.Get, contractUrl))
        {
            request.Headers.Add(CcfEndpointHeaderKey, this.ccfEndpoint);
            request.Headers.Add(ServiceCertHeaderKey, serviceCertPemBase64);

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.IsNotNull(response);
            this.Logger.LogError(
                await response.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
            var error = (await response.Content.ReadFromJsonAsync<ODataError>())!.Error;
            Assert.AreEqual("ContractNotFound", error.Code);
            Assert.AreEqual(
                "A contract with the specified id was not found.",
                error.Message);
        }

        using (HttpRequestMessage request = new(HttpMethod.Post, checkStatusUrl))
        {
            request.Headers.Add(CcfEndpointHeaderKey, this.ccfEndpoint);
            request.Headers.Add(ServiceCertHeaderKey, serviceCertPemBase64);

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var statusResponse =
                (await response.Content.ReadFromJsonAsync<StatusWithReasonResponse>())!;
            Assert.AreEqual("disabled", statusResponse.Status);
            Assert.AreEqual("ContractNotAccepted", statusResponse.Reason.Code);
            Assert.AreEqual(
                "Contract does not exist or has not been accepted.",
                statusResponse.Reason.Message);
        }

        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        string txnId;
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Headers.Add(CcfEndpointHeaderKey, this.ccfEndpoint);
            request.Headers.Add(ServiceCertHeaderKey, serviceCertPemBase64);

            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-ms-ccf-transaction-id", out var values));
            Assert.IsNotNull(values);
            txnId = values.First().ToString()!;
        }

        using (HttpRequestMessage request = new(HttpMethod.Get, contractUrl))
        {
            request.Headers.Add(CcfEndpointHeaderKey, this.ccfEndpoint);
            request.Headers.Add(ServiceCertHeaderKey, serviceCertPemBase64);

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            var contract = await response.Content.ReadFromJsonAsync<JsonObject>();
            Assert.AreEqual(nameof(ContractState.Draft), contract![StateKey]!.ToString());
            Assert.AreEqual(contractContent["data"]!.ToString(), contract["data"]!.ToString());
            var version = contract[VersionKey]!.ToString();
            Assert.AreEqual(version, txnId, "Version value should have matched the transactionId.");
        }
    }

    [TestMethod]
    public async Task CreateContractWithoutHeader()
    {
        string contractId = Guid.NewGuid().ToString().Substring(0, 8);
        string contractUrl = $"contracts/{contractId}";

        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);

            // TODO (anrdesai): Add support for a better error here.
            Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    private async Task<string> GetServiceCertificateAsync()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/node/network");
        using HttpResponseMessage response = await this.CcfClient.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var responseObj = await response.Content.ReadFromJsonAsync<JsonObject>();
        var serviceCertPem = responseObj!["service_certificate"]!.ToString();
        byte[] serviceCertBytes = Encoding.UTF8.GetBytes(serviceCertPem);
        return Convert.ToBase64String(serviceCertBytes);
    }
}
