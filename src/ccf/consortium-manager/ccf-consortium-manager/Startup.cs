// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AttestationClient;
using CcfCommon;
using CcfConsortiumMgr.Auth;
using CcfConsortiumMgr.Clients;
using CcfConsortiumMgr.Workloads;
using Controllers;
using Microsoft.AspNetCore.Authentication;

namespace CcfConsortiumMgr;

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
        this.Logger.LogInformation($"Environment Variables: {env}.");

        if (!Attestation.IsSnpCACI())
        {
            this.Logger.LogWarning(
                "Running in insecure-virtual mode. This is for dev/test environment.");
        }

        services.AddAuthentication(options =>
        {
            options.DefaultScheme = AuthConstants.BearerScheme;
        })
        .AddScheme<AuthenticationSchemeOptions, JwtAuthenticationHandler>(
            AuthConstants.BearerScheme,
            options => { });

        var memberStore = this.BuildMemberStore();
        var clientManager = this.BuildClientManager();
        var workloadFactory = this.BuildWorkloadFactory();
        var svc = new CcfConsortiumManager(
            this.Logger,
            this.Configuration,
            memberStore,
            clientManager,
            workloadFactory);
        services.AddSingleton(svc);

        var authConfigHandler = this.BuildAuthConfigHandler();
        services.AddSingleton(authConfigHandler);
    }

    private IMemberStore BuildMemberStore()
    {
        var keyStore = BuildKeyStore();
        return new MemberStore(keyStore);

        IKeyStore BuildKeyStore()
        {
            if (string.IsNullOrEmpty(this.Configuration[SettingName.AkvEndpoint]))
            {
                var message = $"{SettingName.AkvEndpoint} must be set.";
                this.Logger.LogError(message);
                throw new Exception(message);
            }

            return new AkvKeyStore(
                this.Logger,
                this.Configuration[SettingName.SkrEndpoint]!,
                this.Configuration[SettingName.AkvEndpoint]!,
                this.Configuration[SettingName.MaaEndpoint]!);
        }
    }

    private ClientManager BuildClientManager()
    {
        return new ClientManager(this.Logger);
    }

    private IWorkloadFactory BuildWorkloadFactory()
    {
        return new WorkloadFactory(this.Logger, this.Configuration);
    }

    private AuthConfigHandler BuildAuthConfigHandler()
    {
        return new AuthConfigHandler(this.Logger, this.Configuration);
    }
}
