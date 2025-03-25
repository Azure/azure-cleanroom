// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class EventsController : ClientControllerBase
{
    private const int MaxAttempts = 3;

    public EventsController(
        ILogger<EventsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/contracts/{contractId}/events")]
    [Produces("application/json")]
    public async Task<IActionResult> GetEvents(
        [FromRoute] string contractId,
        [FromQuery] string? id,
        [FromQuery] string? scope,
        [FromQuery] string? from_seqno,
        [FromQuery] string? to_seqno,
        [FromQuery] string? max_seqno_per_page)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        string? query = this.Request.QueryString.Value;
        JsonObject? events = null;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            using HttpResponseMessage response =
                await appClient.GetAsync($"app/contracts/{contractId}/events{query}");
            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1));
                continue;
            }

            await response.ValidateStatusCodeAsync(this.Logger);
            this.Response.CopyHeaders(response.Headers);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                events = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                var nextLink = events["nextLink"]?.ToString();
                if (!string.IsNullOrEmpty(nextLink))
                {
                    if (!nextLink.StartsWith($"/app/contracts/{contractId}/events"))
                    {
                        throw new Exception($"Unexpected nextLink prefix of {nextLink}");
                    }

                    // Remove "/app" prefix from the link so that the URL matches the path
                    // for this method.
                    events["nextLink"] = nextLink.Remove(0, "/app".Length);
                }
            }

            break;
        }

        return events != null ? this.Ok(events) : this.Accepted();
    }
}
