// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Controllers;

public class ClientManager
{
    private readonly ILogger logger;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private WorkspaceConfiguration? wsConfigOnce;
    private HttpClient? ccfAppClientOnce;
    private IConfiguration config;

    public ClientManager(
        ILogger logger,
        IConfiguration config)
    {
        this.logger = logger;
        this.config = config;
    }

    public async Task<HttpClient> GetAppClient()
    {
        return await this.InitializeAppClient();
    }

    public async Task<WorkspaceConfiguration> GetWsConfig()
    {
        return await this.InitializeWsConfig();
    }

    private async Task<HttpClient> InitializeAppClient()
    {
        await this.InitializeWsConfig();
        if (this.ccfAppClientOnce == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.ccfAppClientOnce == null)
                {
                    this.ccfAppClientOnce = this.InitializeClient();
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }

        return this.ccfAppClientOnce;
    }

    private HttpClient InitializeClient()
    {
        var client = new HttpClient();
        return client;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfig()
    {
        if (this.wsConfigOnce == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.wsConfigOnce == null)
                {
                    this.wsConfigOnce = await this.InitializeWsConfigFromEnvironment();
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }

        return this.wsConfigOnce;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFromEnvironment()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcrgovPrivKey]))
        {
            throw new ArgumentException($"{SettingName.CcrgovPrivKey} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.CcrgovPubKey]))
        {
            throw new ArgumentException($"{SettingName.CcrgovPubKey} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.MaaRequest]))
        {
            throw new ArgumentException(
                $"{SettingName.MaaRequest} setting must be specified.");
        }

        var wsConfig = new WorkspaceConfiguration();

        wsConfig.PrivateKey =
            await File.ReadAllTextAsync(this.config[SettingName.CcrgovPrivKey]!);

        wsConfig.PublicKey =
            await File.ReadAllTextAsync(this.config[SettingName.CcrgovPubKey]!);

        var content = await File.ReadAllTextAsync(this.config[SettingName.MaaRequest]!);
        wsConfig.MaaRequest = JsonSerializer.Deserialize<JsonObject>(content)!;
        return wsConfig;
    }
}
