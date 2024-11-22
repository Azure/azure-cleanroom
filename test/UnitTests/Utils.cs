// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Microsoft.Extensions.Logging;
using Microsoft.Rest.TransientFaultHandling;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests;

internal static class Utils
{
    internal static async Task SecureKeyReleaseAsync(
        string skrPort,
        SecureKeyReleasePayload payload,
        ILogger logger)
    {
        using HttpClient client = new();
        client.BaseAddress = new Uri($"http://localhost:{skrPort}");

        var request = new HttpRequestMessage(HttpMethod.Post, "/key/release");

        logger.LogInformation($"Testing secure key release with payload:\n" +
            $"{JsonSerializer.Serialize(payload)}");

        request.Content = JsonContent.Create(payload);

        HttpResponseMessage response = await client.SendAsync(request);

        logger.LogInformation(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    internal static async Task ValidateStatusCodeAsync(
        this HttpResponseMessage response,
        ILogger logger)
    {
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();

            logger.LogError(
                $"{response.RequestMessage!.Method} request for resource: " +
                $"{response.RequestMessage.RequestUri} " +
                $"failed with statusCode {response.StatusCode}, " +
                $"reasonPhrase: {response.ReasonPhrase} and content: {content}.");

            throw new HttpRequestWithStatusException(content)
            {
                StatusCode = response.StatusCode
            };
        }
    }

    internal static async Task<int> FetchACIContainerExitCode(
        string containerName,
        string aciContainerGroupId,
        TimeSpan timeout,
        ILogger logger)
    {
        string expectedContainerState = "Terminated";
        var options = new JsonSerializerOptions { WriteIndented = true };

        ResourceIdentifier resourceId = new(aciContainerGroupId);
        ArmClient client = new(new DefaultAzureCredential());
        ContainerGroupResource containerGroup = client.GetContainerGroupResource(resourceId);

        int? exitCode = null;

        Stopwatch stopWatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                containerGroup = await containerGroup.GetAsync();
            }
            catch (RequestFailedException rfe)
            when (rfe.ErrorCode == "AuthorizationFailed" && stopWatch.Elapsed < timeout)
            {
                // Can happen at times if permissions on the RG have not percolated yet. Retry.
                logger.LogWarning(
                    $"Not able to access container group '{resourceId}' due to " +
                    $"{rfe.ErrorCode}, {rfe.Message}. waiting in case its due to delay in " +
                    $"permissions getting applied.");
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            var container =
                containerGroup.Data.Containers.Single(c => c.Name == containerName);
            string currentState = container.InstanceView.CurrentState.State;
            exitCode = container.InstanceView.CurrentState.ExitCode;
            if (currentState != expectedContainerState)
            {
                if (stopWatch.Elapsed > timeout)
                {
                    throw new Exception(
                        $"Did not find expected container '{containerName}' in ACI " +
                        $"container group in expected state: {expectedContainerState}. " +
                        $"InstanceView: " +
                        $"{JsonSerializer.Serialize(container.InstanceView, options)}");
                }

                logger.LogInformation(
                    $"Waiting for container '{containerName}' to appear in " +
                    $"{expectedContainerState} state." +
                    $"Current state: {currentState}");
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            logger.LogInformation(
                $"Found for container '{containerName}' in expected state. " +
                $"InstanceView: {JsonSerializer.Serialize(container.InstanceView, options)}");
            break;
        }

        stopWatch.Stop();

        Assert.IsNotNull(exitCode, "Exit code returned null for a terminated container");

        return exitCode.Value;
    }
}