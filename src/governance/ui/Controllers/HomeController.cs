// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CgsUI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CgsUI.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> logger;
    private readonly IConfiguration configuration;

    public HomeController(ILogger<HomeController> logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        bool connected;
        bool configured;
        bool updatesAvailable = false;

        using var client = new HttpClient();
        try
        {
            using var response = await client.GetAsync($"{this.configuration.GetEndpoint()}/show");
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                connected = true;
                configured = true;
                var item = (await response.Content.ReadFromJsonAsync<SettingsViewModel>())!;
                Common.Name = item.Identifier ?? "unknown";
                var uri = new Uri(this.configuration.GetEndpoint());
                Common.ConnectedTo = $"{Common.Name}@{uri.Host}:{uri.Port}";
                Common.MemberId = item.MemberId;

                var updates = (await client.GetFromJsonAsync<UpdatesViewModel>(
                    $"{this.configuration.GetEndpoint()}/checkupdates"))!;
                updatesAvailable = updates.Proposals?.Count != 0;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                connected = true;
                configured = false;
                Common.Name = "na";
                Common.ConnectedTo = "cgsclient-not-configured";
            }
            else
            {
                connected = false;
                configured = false;
                Common.Name = "na";
                Common.ConnectedTo = "cgsclient-not-reachable";
            }

            return this.View(new HomeViewModel
            {
                Connected = connected,
                Configured = configured,
                UpdatesAvailable = updatesAvailable
            });
        }
        catch
        {
            connected = false;
            configured = false;
            Common.Name = "na";
            Common.ConnectedTo = "cgsclient-not-reachable";
            return this.View(new HomeViewModel
            {
                Connected = connected,
                Configured = configured,
                UpdatesAvailable = updatesAvailable
            });
        }
    }

    public IActionResult Privacy()
    {
        return this.View();
    }
}
