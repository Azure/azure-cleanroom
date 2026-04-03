// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using Controllers;

namespace CcfProviderClient;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => false;

    public override void OnConfigureServices(IServiceCollection services)
    {
        var env = JsonSerializer.Serialize(
            Environment.GetEnvironmentVariables(),
            new JsonSerializerOptions { WriteIndented = true });
        this.Logger.LogInformation($"Environment Variables: {env}");

        services.AddControllers(options =>
        {
            options.InputFormatters.Add(new ByteArrayInputFormatter());
        });
        services.AddSingleton<ClientManager>();
    }
}