// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AttestationClient;
using Controllers;

namespace CcfConsortiumMgr;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override void OnConfigureServices(IServiceCollection services)
    {
        var env = JsonSerializer.Serialize(
            Environment.GetEnvironmentVariables(),
            new JsonSerializerOptions { WriteIndented = true });
        this.Logger.LogInformation($"Environment Variables: {env}.");

        if (!Attestation.IsSevSnp())
        {
            this.Logger.LogWarning(
                "Running in insecure-virtual mode. This is for dev/test environment.");
        }

        var svc = new CcfConsortiumManager(this.Logger);
        services.AddSingleton(svc);
    }
}
