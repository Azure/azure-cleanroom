// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

public class TestBase
{
    public const string ProposalIdKey = "proposalId";
    public const string VersionKey = "version";
    public const string StateKey = "state";
    public const string MsTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";

    protected IConfiguration Configuration { get; set; } = default!;

    protected ILogger Logger { get; set; } = default!;

    protected HttpClient CcfClient { get; set; } = default!;

    protected List<HttpClient> CgsClients { get; set; } = default!;

    // Keep an easy reference to member0 as its used often.
    protected HttpClient CgsClient_Member0 { get; set; } = default!;

    protected HttpClient GovSidecarClient { get; set; } = default!;

    protected string ContractId { get; set; } = default!;

    /// <summary>
    /// Initialize tests.
    /// </summary>
    [TestInitialize]
    public virtual void Initialize()
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

        this.CcfClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["ccfEndpoint"]!)
        };

        this.CgsClients = new List<HttpClient>();
        this.CgsClient_Member0 = new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["cgsClientEndpoint_member0"]!)
        };
        this.CgsClients.Add(this.CgsClient_Member0);

        this.CgsClients.Add(new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["cgsClientEndpoint_member1"]!)
        });

        this.CgsClients.Add(new HttpClient(handler)
        {
            BaseAddress = new Uri(this.Configuration["cgsClientEndpoint_member2"]!)
        });

        this.GovSidecarClient = new HttpClient
        {
            BaseAddress = new Uri(this.Configuration["govSidecarEndpoint"]!)
        };

        // Set the governance API path prefix for the ccr-governance sidecar as each
        // test case needs to use a unique contract Id in the endpoint address so we cannot
        // specify the path prefix as part of the ccr-governance startup configuration.
        this.GovSidecarClient.DefaultRequestHeaders.Add(
            "x-ms-ccr-governance-api-path-prefix",
            $"app/contracts/{this.ContractId}");
    }

    protected async Task ProposeContractAndAcceptAllowAllCleanRoomPolicy(string contractId)
    {
        await this.ProposeAndAcceptContract(contractId);
        await this.ProposeAndAcceptAllowAllCleanRoomPolicy(contractId);
    }

    protected async Task<string> ProposeContract(string contractId)
    {
        string contractUrl = $"contracts/{contractId}";
        var contractContent = new JsonObject
        {
            ["data"] = "hello world"
        };

        // Add a contract to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var contract = (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(contractUrl))!;
        var version = contract[VersionKey]!.ToString();

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, $"contracts/{contractId}/propose"))
        {
            var proposalContent = new JsonObject
            {
                [VersionKey] = version
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
            return proposalId;
        }
    }

    protected async Task ProposeAndAcceptContract(string contractId)
    {
        string proposalId = await this.ProposeContract(contractId);

        // All members vote on the above proposal by accepting it.
        await this.AllMembersAcceptContract(contractId, proposalId);
    }

    protected async Task<string> ProposeDeploymentSpec(
        string contractId,
        JsonObject spec,
        int asMember = Members.Member0)
    {
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/deploymentspec/propose"))
        {
            request.Content = new StringContent(
                spec.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClients[asMember].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        return proposalId;
    }

    protected async Task ProposeAndAcceptDeploymentSpec(string contractId, JsonObject spec)
    {
        string proposalId = await this.ProposeDeploymentSpec(contractId, spec);

        // All members vote on the above proposal by accepting it.
        await this.AllMembersAcceptProposal(proposalId);
    }

    protected async Task<string> ProposeAllowAllCleanRoomPolicy(
        string contractId,
        int asMember = Members.Member0)
    {
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/cleanroompolicy/propose"))
        {
            var proposalContent = new JsonObject
            {
                ["type"] = "add",
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

            using HttpResponseMessage response = await this.CgsClients[asMember].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        return proposalId;
    }

    protected async Task ProposeAndAcceptAllowAllCleanRoomPolicy(string contractId)
    {
        string proposalId = await this.ProposeAllowAllCleanRoomPolicy(contractId);

        // All members vote on the above proposal by accepting it.
        await this.AllMembersAcceptProposal(proposalId);
    }

    protected async Task ProposeAndAcceptEnableOidcIssuer()
    {
        string proposalId;
        using (HttpRequestMessage request = new(HttpMethod.Post, "proposals/create"))
        {
            var proposalContent = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "enable_oidc_issuer",
                        ["args"] = new JsonObject
                        {
                            ["kid"] = Guid.NewGuid().ToString("N")
                        }
                    }
                }
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            proposalId = responseBody[ProposalIdKey]!.ToString();
        }

        // All members vote on the above proposal by accepting it.
        await this.AllMembersAcceptProposal(proposalId);
    }

    protected async Task<string> GenerateOidcIssuerSigningKey()
    {
        using (HttpRequestMessage request = new(HttpMethod.Post, "oidc/generateSigningKey"))
        {
            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.IsTrue(!string.IsNullOrEmpty(responseBody["reqid"]!.ToString()));
            string kid = responseBody["kid"]!.ToString();
            Assert.IsTrue(!string.IsNullOrEmpty(kid));
            return kid;
        }
    }

    protected virtual async Task AllMembersAcceptProposal(string proposalId)
    {
        foreach (var memberClient in this.CgsClients)
        {
            await this.MemberAcceptProposal(memberClient, proposalId);
        }
    }

    protected async Task MemberAcceptProposal(HttpClient client, string proposalId)
    {
        using (HttpRequestMessage request =
        new(HttpMethod.Post, $"proposals/{proposalId}/ballots/vote_accept"))
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    protected virtual async Task AllMembersAcceptContract(string contractId, string proposalId)
    {
        foreach (var memberClient in this.CgsClients)
        {
            await this.MemberAcceptContract(memberClient, contractId, proposalId);
        }
    }

    protected async Task MemberAcceptContract(
        HttpClient client,
        string contractId,
        string proposalId)
    {
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"contracts/{contractId}/vote_accept"))
        {
            var proposalContent = new JsonObject
            {
                [ProposalIdKey] = proposalId
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    protected async Task AllMembersAcceptDocument(string documentId, string proposalId)
    {
        foreach (var memberClient in this.CgsClients)
        {
            await this.MemberAcceptDocument(memberClient, documentId, proposalId);
        }
    }

    protected async Task MemberAcceptDocument(
        HttpClient client,
        string documentId,
        string proposalId)
    {
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"documents/{documentId}/vote_accept"))
        {
            var proposalContent = new JsonObject
            {
                [ProposalIdKey] = proposalId
            };

            request.Content = new StringContent(
                proposalContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    protected async Task MemberSetIssuerUrl(int index, string issuerUrl)
    {
        using (HttpRequestMessage request = new(HttpMethod.Post, "oidc/setIssuerUrl"))
        {
            var content = new JsonObject
            {
                ["url"] = issuerUrl
            };
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await this.CgsClients[index].SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    protected bool IsGitHubActionsEnv()
    {
        return this.Configuration["GITHUB_ACTIONS"] == "true";
    }
}