// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

    protected HttpClient IdpClient { get; set; } = default!;

    protected List<HttpClient> CgsClients { get; set; } = default!;

    // Keep an easy reference to member0 as its used often.
    protected HttpClient CgsClient_Member0 { get; set; } = default!;

    protected HttpClient GovSidecarClient { get; set; } = default!;

    protected string GovSidecarHostData { get; } =
        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";

    protected HttpClient GovSidecar2Client { get; set; } = default!;

    protected string GovSidecar2HostData { get; set; } =
        "7ec5120f0f497e22b18e59ed702ed82e2732562245c9a944f54cd41db4f491af";

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

        this.IdpClient = new HttpClient
        {
            BaseAddress = new Uri(this.Configuration["idpEndpoint"]!)
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

        this.GovSidecar2Client = new HttpClient
        {
            BaseAddress = new Uri(this.Configuration["govSidecar2Endpoint"]!)
        };

        // Set the governance API path prefix for the ccr-governance sidecar as each
        // test case needs to use a unique contract Id in the endpoint address so we cannot
        // specify the path prefix as part of the ccr-governance startup configuration.
        this.GovSidecarClient.DefaultRequestHeaders.Add(
            "x-ms-ccr-governance-api-path-prefix",
            $"app/contracts/{this.ContractId}");

        this.GovSidecar2Client.DefaultRequestHeaders.Add(
            "x-ms-ccr-governance-api-path-prefix",
            $"app/contracts/{this.ContractId}");
    }

    protected async Task ProposeContractAndAcceptCleanRoomPolicy(string contractId, string hostData)
    {
        await this.ProposeAndAcceptContract(contractId);
        await this.ProposeAndAcceptCleanRoomPolicy(contractId, hostData);
    }

    protected async Task ProposeContractAndAcceptAllowAllCleanRoomPolicy(string contractId)
    {
        string allowAllValue = "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";
        await this.ProposeContractAndAcceptCleanRoomPolicy(contractId, allowAllValue);
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

    protected async Task<string> ProposeUserDocument(string contractId, string documentId)
    {
        string userdocumentUrl = $"userdocuments/{documentId}";
        var contractContent = new JsonObject
        {
            ["contractId"] = contractId,
            ["data"] = "hello world"
        };

        // Add a userdocument to start with.
        using (HttpRequestMessage request = new(HttpMethod.Put, userdocumentUrl))
        {
            request.Content = new StringContent(
                contractContent.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await this.CgsClient_Member0.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var userdocument =
            (await this.CgsClient_Member0.GetFromJsonAsync<JsonObject>(userdocumentUrl))!;
        var version = userdocument[VersionKey]!.ToString();

        // Create a proposal for the above contract.
        string proposalId;
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/propose"))
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

    protected async Task ProposeAndAcceptUserDocument(string contractId, string documentId)
    {
        string proposalId = await this.ProposeUserDocument(contractId, documentId);

        // All members vote on the above proposal by accepting it.
        await this.AllMembersAcceptUserDocument(documentId, proposalId);
    }

    protected async Task ProposeAndAcceptUserIdentity(
        string userId,
        string identifier,
        string tenantId)
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
                        ["name"] = "set_user_identity",
                        ["args"] = new JsonObject
                        {
                            ["id"] = userId,
                            ["accountType"] = "microsoft",
                            ["data"] = new JsonObject
                            {
                                ["tenantId"] = tenantId,
                                ["identifier"] = identifier
                            }
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

    protected Task<string> ProposeAllowAllCleanRoomPolicy(
    string contractId,
    int asMember = Members.Member0)
    {
        string allowAllValue = "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";
        return this.ProposeCleanRoomPolicy(contractId, allowAllValue, asMember);
    }

    protected async Task<string> ProposeCleanRoomPolicy(
        string contractId,
        string hostData,
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
                    ["x-ms-sevsnpvm-hostdata"] = hostData
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
        string allowAllValue = "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";
        string proposalId = await this.ProposeCleanRoomPolicy(contractId, allowAllValue);

        // All members vote on the above proposal by accepting it.
        await this.AllMembersAcceptProposal(proposalId);
    }

    protected async Task ProposeAndAcceptCleanRoomPolicy(string contractId, string hostData)
    {
        string proposalId = await this.ProposeCleanRoomPolicy(contractId, hostData);

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
            new(HttpMethod.Post, $"memberdocuments/{documentId}/vote_accept"))
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

    protected virtual async Task AllMembersAcceptUserDocument(
        string documentId,
        string proposalId)
    {
        foreach (var memberClient in this.CgsClients)
        {
            await this.MemberAcceptUserDocument(memberClient, documentId, proposalId);
        }
    }

    protected async Task MemberAcceptUserDocument(
        HttpClient client,
        string documentId,
        string proposalId)
    {
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"userdocuments/{documentId}/vote_accept"))
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

    protected bool IsCodespacesEnv()
    {
        return this.Configuration["CODESPACES"] == "true";
    }

    protected async Task<string> GetMemberId(HttpClient client)
    {
        var info = await client.GetFromJsonAsync<JsonObject>("/show");
        return info!["memberId"]!.ToString();
    }

    protected async Task<List<(string id, HttpClient userClient)>> CreateAndAcceptUsers(
        int numUsers)
    {
        List<(string id, HttpClient userClient)> users = new();
        for (int i = 0; i < numUsers; i++)
        {
            string userId = Guid.NewGuid().ToString().Substring(0, 8);
            string identifier = $"user{i}";
            string tenantId = Guid.NewGuid().ToString();
            using var response = await this.IdpClient.PostAsync(
                $"oauth/token?oid={userId}&tid={tenantId}",
                content: null);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var token = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
                {
                    return true;
                }
            };

            var userClient = new HttpClient(handler)
            {
                BaseAddress = new Uri(this.Configuration["ccfEndpoint"]!),
            };
            userClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token["accessToken"]!.ToString());

            users.Add((userId, userClient));
            await this.ProposeAndAcceptUserIdentity(userId, identifier, tenantId);
        }

        return users;
    }

    protected JsonObject ConvertClaimsToArrayFormat(JsonObject claims)
    {
        var result = new JsonObject();
        foreach (var kvp in claims)
        {
            JsonArray newArray;
            if (kvp.Value is JsonArray existingArray)
            {
                newArray = JsonNode.Parse(existingArray.ToJsonString())!.AsArray();
            }
            else
            {
                newArray = new JsonArray(JsonNode.Parse(kvp.Value!.ToJsonString()));
            }

            result[kvp.Key] = newArray;
        }

        return result;
    }
}