// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CgsUI.Models;
using Microsoft.AspNetCore.Mvc;
using static CgsUI.Controllers.UsersController;

namespace CgsUI.Controllers;

public class UserDocumentsController : Controller
{
    private readonly ILogger<UserDocumentsController> logger;
    private readonly IConfiguration configuration;

    public UserDocumentsController(
        ILogger<UserDocumentsController> logger,
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
            var items = await client.GetFromJsonAsync<ListUserDocumentsViewModel>(
                $"{this.configuration.GetEndpoint()}/userdocuments");
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

    [Route("UserDocuments/{id}")]
    public async Task<IActionResult> Detail(string id)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}";
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

            var item = await response.Content.ReadFromJsonAsync<UserDocumentViewModel>();
            if (item?.Labels != null)
            {
                item.LabelsView = item.Labels.Select(
                    kvp => new UserDocumentViewModel.LabelsViewModel
                    {
                        Name = kvp.Key,
                        Value = kvp.Value!.ToString()!
                    }).ToList();
            }

            var users = (await client.GetFromJsonAsync<ListUsers>(
                $"{this.configuration.GetEndpoint()}/users"))!;
            var members = await client.GetFromJsonAsync<MembersController.ListMembers>(
                $"{this.configuration.GetEndpoint()}/members");
            return this.View(new UserDocumentViewWithMembersUsersModel
            {
                Document = item!,
                Members = members!,
                Users = users
            });
        }
    }

    [Route("UserDocuments/{id}/Propose/{version}")]
    public async Task<IActionResult> Propose(string id, string version)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/propose";
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

    [Route("UserDocuments/{id}/Proposal/{proposalId}/VoteAccept")]
    public async Task<IActionResult> VoteAccept(string id, string proposalId)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/vote_accept";
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

    [Route("UserDocuments/{id}/Proposal/{proposalId}/VoteReject")]
    public async Task<IActionResult> VoteReject(string id, string proposalId)
    {
        string contractUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/vote_reject";
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

    [Route("UserDocuments/{id}/RuntimeOptions")]
    public async Task<IActionResult> RuntimeOptionsDetail(string id)
    {
        using var client = new HttpClient();
        var t2 = client.PostAsync(
             $"{this.configuration.GetEndpoint()}/userdocuments/{id}/checkstatus/telemetry", null);
        var t3 = client.PostAsync(
             $"{this.configuration.GetEndpoint()}/userdocuments/{id}/checkstatus/execution", null);

        var tasks = new List<Task> { t2, t3 };
        await Task.WhenAll(tasks);

        return this.View(new RuntimeOptionsViewModel
        {
            Id = id,
            Execution = (await (await t3)!.Content.ReadFromJsonAsync<ExecutionOptionViewModel>())!,
            Telemetry = (await (await t2)!.Content.ReadFromJsonAsync<TelemetryOptionViewModel>())!
        });
    }

    [Route("UserDocuments/{id}/EnableExecution")]
    public async Task<IActionResult> EnableExecution(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/" +
            $"runtimeoptions/execution/enable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("UserDocuments/{id}/DisableExecution")]
    public async Task<IActionResult> DisableExecution(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/" +
            $"runtimeoptions/execution/disable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("UserDocuments/{id}/EnableTelemetry")]
    public async Task<IActionResult> EnableTelemetry(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/" +
            $"runtimeoptions/telemetry/enable";
        return await this.PostAction(actionUrl, id);
    }

    [Route("UserDocuments/{id}/DisableTelemetry")]
    public async Task<IActionResult> DisableTelemetry(string id)
    {
        string actionUrl =
            $"{this.configuration.GetEndpoint()}/userdocuments/{id}/" +
            $"runtimeoptions/telemetry/disable";
        return await this.PostAction(actionUrl, id);
    }

    // GET: UserDocuments/Create
    [Route("UserDocuments/Create")]
    public async Task<IActionResult> Create()
    {
        try
        {
            using var client = new HttpClient();
            var contracts = await client.GetFromJsonAsync<ListContractsViewModel>(
                $"{this.configuration.GetEndpoint()}/contracts");

            var viewModel = new CreateUserDocumentViewModel
            {
                AvailableContracts = contracts?.Value.Select(c => c.Id).OrderBy(x => x).ToList()
                ?? new List<string>()
            };

            return this.View(viewModel);
        }
        catch (HttpRequestException re)
        {
            return this.View("Error", new ErrorViewModel
            {
                Content = re.Message
            });
        }
    }

    // POST: UserDocuments/Create
    // To protect from overposting attacks, enable the specific properties you want to bind to.
    // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("UserDocuments/Create")]
    public async Task<IActionResult> Create(
        CreateUserDocumentViewModel viewModel)
    {
        // Validate label key/value pairs
        if (viewModel.Labels != null && viewModel.Labels.Any())
        {
            foreach (var label in viewModel.Labels)
            {
                if (string.IsNullOrWhiteSpace(label.Key) && !string.IsNullOrWhiteSpace(label.Value))
                {
                    this.ModelState.AddModelError(
                        "Labels",
                        "Label key cannot be empty if value is provided.");
                    break;
                }
            }
        }

        if (this.ModelState.IsValid)
        {
            string documentUrl =
                $"{this.configuration.GetEndpoint()}/userdocuments/{viewModel.Document.Id}";
            using (HttpRequestMessage request = new(HttpMethod.Put, documentUrl))
            {
                var payload = new JsonObject
                {
                    ["contractId"] = viewModel.Document.ContractId,
                    ["data"] = viewModel.Document.Data
                };

                // Convert labels key/value pairs to JSON object
                if (viewModel.Labels != null && viewModel.Labels.Any())
                {
                    var labelsObject = new JsonObject();
                    foreach (var label in viewModel.Labels)
                    {
                        if (!string.IsNullOrWhiteSpace(label.Key))
                        {
                            labelsObject[label.Key] = label.Value;
                        }
                    }

                    if (labelsObject.Count > 0)
                    {
                        payload["labels"] = labelsObject;
                    }
                }

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

        // Reload contracts for the dropdown on validation failure
        try
        {
            using var client = new HttpClient();
            var contracts = await client.GetFromJsonAsync<ListContractsViewModel>(
                $"{this.configuration.GetEndpoint()}/contracts");
            viewModel.AvailableContracts =
                contracts?.Value.Select(c => c.Id).OrderBy(x => x).ToList() ?? new List<string>();
        }
        catch
        {
            // If we can't load contracts, just return with empty list
        }

        return this.View(viewModel);
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
