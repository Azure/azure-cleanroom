// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel, Workers = 0)]

namespace UnitTests;

/// <summary>
/// The base class for unit tests.
/// </summary>
[TestClass]
public abstract class UnitTestBase
{
    /// <summary>
    /// Gets the configuration.
    /// </summary>
    protected IConfiguration Configuration { get; private set; } = default!;

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    protected ILogger Logger { get; private set; } = default!;

    /// <summary>
    /// Initialize tests.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        string? testConfigurationFile = Environment.GetEnvironmentVariable(
            "TEST_CONFIGURATION_FILE");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("testconfiguration.json")
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

        this.Logger = loggerFactory.CreateLogger<UnitTestBase>();
    }

    protected async Task SecureKeyReleaseAsync(SecureKeyReleasePayload payload)
    {
        using HttpClient client = new();
        client.BaseAddress = new Uri($"http://localhost:" +
            $"{this.Configuration[TestSettingName.SkrPort]}");

        var request = new HttpRequestMessage(HttpMethod.Post, "/key/release");

        this.Logger.LogInformation($"Testing secure key release with payload:\n" +
            $"{JsonSerializer.Serialize(payload)}");

        request.Content = JsonContent.Create(payload);

        HttpResponseMessage response = await client.SendAsync(request);

        this.Logger.LogInformation(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
