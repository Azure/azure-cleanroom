// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using CgsUI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CgsUI.Controllers;

public class ContractsController : Controller
{
    private readonly ILogger<ContractsController> logger;
    private readonly IConfiguration configuration;

    public ContractsController(
        ILogger<ContractsController> logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        try
        {
            using var client = new HttpClient();
            var items = await client.GetFromJsonAsync<ListContractsViewModel>(
                $"{this.configuration.GetEndpoint()}/contracts");
            items!.Value = [.. items.Value.OrderBy(x => x.Id)];
            return this.View(items);
        }
        catch (HttpRequestException re)
        {
            return this.View("Error", new ErrorViewModel
            {
                Content = re.Message
            });
        }
    }

    [Route("Contracts/{id}")]
    public async Task<IActionResult> Detail(string id)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}";
        using (HttpRequestMessage request = new(HttpMethod.Get, contractUrl))
        {
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            var item = await response.Content.ReadFromJsonAsync<ContractViewModel>();
            if (item?.FinalVotes != null && item.FinalVotes.Any())
            {
                var members = (await client.GetFromJsonAsync<JsonObject>(
                    $"{this.configuration.GetEndpoint()}/members"))!;
                Dictionary<string, JsonNode?> currentMembers =
                    members["value"]!.AsArray()
                    .ToDictionary(m => m!["memberId"]!.ToString(), m => m);
                foreach (var vote in item.FinalVotes)
                {
                    string name = "not a current member";
                    if (currentMembers.TryGetValue(vote.MemberId, out var value))
                    {
                        name = value?["memberData"]?["identifier"]?.ToString() ?? "Not set";
                    }

                    vote.MemberName = name;
                }
            }

            return this.View(item);
        }
    }

    [Route("Contracts/{id}/Propose/{version}")]
    public async Task<IActionResult> Propose(string id, string version)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/propose";
        using (HttpRequestMessage request = new(HttpMethod.Post, contractUrl))
        {
            var payload = new JsonObject
            {
                ["version"] = version
            };

            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            return this.RedirectToAction(nameof(this.Detail), new { id });
        }
    }

    [Route("Contracts/{id}/Proposal/{proposalId}/VoteAccept")]
    public async Task<IActionResult> VoteAccept(string id, string proposalId)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/vote_accept";
        using (HttpRequestMessage request = new(HttpMethod.Post, contractUrl))
        {
            var payload = new JsonObject
            {
                ["proposalId"] = proposalId
            };

            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            return this.RedirectToAction(nameof(this.Detail), new { id });
        }
    }

    [Route("Contracts/{id}/Proposal/{proposalId}/VoteReject")]
    public async Task<IActionResult> VoteReject(string id, string proposalId)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/vote_reject";
        using (HttpRequestMessage request = new(HttpMethod.Post, contractUrl))
        {
            var payload = new JsonObject
            {
                ["proposalId"] = proposalId
            };

            request.Content = new StringContent(
                payload.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            return this.RedirectToAction(nameof(this.Detail), new { id });
        }
    }

    [Route("Contracts/{id}/DeploymentSpec")]
    public async Task<IActionResult> DeploymentSpecDetail(string id)
    {
        using var client = new HttpClient();
        var item = await client.GetFromJsonAsync<DeploymentSpecViewModel>(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/deploymentspec");
        return this.View(item);
    }

    [Route("Contracts/{id}/Policy")]
    public async Task<IActionResult> PolicyDetail(string id)
    {
        using var client = new HttpClient();
        var item = await client.GetFromJsonAsync<PolicyViewModel>(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/cleanroompolicy");
        return this.View(item);
    }

    [Route("Contracts/{id}/Secrets")]
    public async Task<IActionResult> SecretsDetail(string id)
    {
        using var client = new HttpClient();
        var item = (await client.GetFromJsonAsync<SecretsViewModel>(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/secrets"))!;
        item.ContractId = id;
        return this.View(item);
    }

    [Route("Contracts/{id}/Secrets/{secretId}")]
    public async Task<IActionResult> SecretIdDetail(string id, string secretId)
    {
        using var client = new HttpClient();
        var item = await client.GetFromJsonAsync<JsonObject>(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/secrets/{secretId}/cleanroompolicy");
        SecretsPolicyViewModel model = new()
        {
            Policy = item
        };
        return this.View(model);
    }

    [Route("Contracts/{id}/Events")]
    public async Task<IActionResult> EventsDetail(string id)
    {
        using var client = new HttpClient();

        // &from_seqno=1 means get all events.
        var item = await client.GetFromJsonAsync<EventsViewModel>(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/events?&from_seqno=1");
        return this.View(item);
    }

    [Route("Contracts/{id}/RuntimeOptions")]
    public async Task<IActionResult> RuntimeOptionsDetail(string id)
    {
        using var client = new HttpClient();
        var t1 = client.PostAsync(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/checkstatus/logging", null);
        var t2 = client.PostAsync(
            $"{this.configuration.GetEndpoint()}/contracts/{id}/checkstatus/telemetry", null);
        var t3 = client.PostAsync(
             $"{this.configuration.GetEndpoint()}/contracts/{id}/checkstatus/execution", null);

        var tasks = new List<Task> { t1, t2, t3 };
        await Task.WhenAll(tasks);

        return this.View(new RuntimeOptionsViewModel
        {
            Id = id,
            Logging = (await (await t1)!.Content.ReadFromJsonAsync<LoggingOptionViewModel>())!,
            Telemetry = (await (await t2)!.Content.ReadFromJsonAsync<TelemetryOptionViewModel>())!,
            Execution = (await (await t3)!.Content.ReadFromJsonAsync<ExecutionOptionViewModel>())!
        });
    }

    [Route("Contracts/{id}/EnableExecution")]
    public async Task<IActionResult> EnableExecution(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/execution/enable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("Contracts/{id}/DisableExecution")]
    public async Task<IActionResult> DisableExecution(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/execution/disable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("Contracts/{id}/EnableLogging")]
    public async Task<IActionResult> EnableLogging(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/logging/propose-enable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("Contracts/{id}/DisableLogging")]
    public async Task<IActionResult> DisableLogging(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/logging/propose-disable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("Contracts/{id}/EnableTelemetry")]
    public async Task<IActionResult> EnableTelemetry(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/telemetry/propose-enable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("Contracts/{id}/DisableTelemetry")]
    public async Task<IActionResult> DisableTelemetry(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/contracts/{id}/telemetry/propose-disable";
        return await this.PostAction(actionUrl, id);
    }

    // GET: Contracts/Create
    [Route("Contracts/Create")]
    public IActionResult Create()
    {
        return this.View();
    }

    // POST: Contracts/Create
    // To protect from overposting attacks, enable the specific properties you want to bind to.
    // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("Contracts/Create")]
    public async Task<IActionResult> Create(
        [Bind("Id,Data")] Contract contract)
    {
        if (this.ModelState.IsValid)
        {
            string contractUrl =
                $"{this.configuration.GetEndpoint()}/contracts/{contract.Id}";
            using (HttpRequestMessage request = new(HttpMethod.Put, contractUrl))
            {
                var payload = new JsonObject
                {
                    ["data"] = contract.Data
                };

                request.Content = new StringContent(
                    payload.ToJsonString(),
                    Encoding.UTF8,
                    "application/json");

                using var client = new HttpClient();
                using HttpResponseMessage response = await client.SendAsync(request);
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    return this.View("Error", new ErrorViewModel
                    {
                        Content = error
                    });
                }

                return this.RedirectToAction(nameof(this.Index));
            }
        }

        return this.View(contract);
    }

    private async Task<IActionResult> PostAction(string url, string id)
    {
        using (HttpRequestMessage request = new(HttpMethod.Post, url))
        {
            using var client = new HttpClient();
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                var error = await response.Content.ReadAsStringAsync();
                return this.View("Error", new ErrorViewModel
                {
                    Content = error
                });
            }

            return this.RedirectToAction(nameof(this.RuntimeOptionsDetail), new { id });
        }
    }
}
