// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using AttestationClient;
using Controllers;
using FrontendSvc.Api.V2026_03_01_Preview;
using FrontendSvc.Publisher.Factory;
using Microsoft.AspNetCore.Mvc;

namespace FrontendSvc;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => false;

    public override void OnConfigureServices(IServiceCollection services)
    {
        if (!Attestation.IsSnpCACI())
        {
            this.Logger.LogWarning(
                "Running in insecure-virtual mode. This is for dev/test environment.");
        }

        services.AddSingleton<ClientManager>();
        services.AddSingleton
            <ICollaborationPublisherFactory, CollaborationPublisherFactory>();

        // Register supported API versions.
        // Add new versions here as they are created.
        services.Configure<MvcOptions>(options =>
        {
            options.Filters.Add(new ApiVersionValidationFilter(
                [ApiVersionConstants.Version]));
        });
    }
}
