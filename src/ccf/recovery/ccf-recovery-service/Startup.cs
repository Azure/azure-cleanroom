// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using System.Text.Json;
using AttestationClient;
using CcfCommon;
using Controllers;

namespace CcfProviderClient;

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
        this.Logger.LogInformation($"Environment Variables: {env}");

        if (!Attestation.IsSevSnp())
        {
            this.Logger.LogWarning(
                "Running in insecure-virtual mode. This is for dev/test environment.");
        }

        var keyStore = this.BuildKeyStore();
        var policyStore = this.BuildPolicyStore(keyStore);
        var memberStore = this.BuildMemberStore(keyStore);
        var svc = new CcfRecoveryService(
            this.Logger,
            this.Configuration[SettingName.ServiceCertLocation],
            memberStore);
        services.AddSingleton(svc);
        services.AddSingleton(policyStore);
    }

    private IMemberStore BuildMemberStore(IKeyStore keyStore)
    {
        return new MemberStore(keyStore);
    }

    private IPolicyStore BuildPolicyStore(IKeyStore keyStore)
    {
        var policyJsonBase64 = this.Configuration[SettingName.CcfNetworkInitialJoinPolicy];
        if (!string.IsNullOrEmpty(policyJsonBase64))
        {
            this.Logger.LogInformation("Using AKV policy store for ccf network join policy.");
            return new AkvPolicyStore(
                this.Logger,
                this.Configuration[SettingName.AkvEndpoint]!,
                policyJsonBase64,
                keyStore);
        }

        bool.TryParse(
            this.Configuration[SettingName.CcfNetworkAllowAllJoinPolicy],
            out var useAllowAllPolicy);
        if (useAllowAllPolicy)
        {
            this.Logger.LogInformation($"Using allow-all ccf network join policy.");
            return new AllowAllPolicyStore(this.Logger);
        }

        var message = $"Either {SettingName.CcfNetworkInitialJoinPolicy} or " +
            $"{SettingName.CcfNetworkAllowAllJoinPolicy} environment variable must be set.";
        this.Logger.LogError(message);
        throw new Exception(message);
    }

    private IKeyStore BuildKeyStore()
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