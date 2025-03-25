// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Controllers;

public static class ApiMain
{
    public static void Main(string[] args, Func<WebApplicationBuilder, ApiStartup> startupFunc)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Suggest switching from using Configure methods to WebApplicationBuilder.Configuration
#pragma warning disable ASP0013
        builder.WebHost.ConfigureAppConfiguration((context, configBuilder) =>
        {
            configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            configBuilder.AddJsonFile(
                $"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
                optional: true,
                reloadOnChange: true);

            configBuilder.AddEnvironmentVariables();
            configBuilder.AddCommandLine(args);
        });

        // Suggest switching from using Configure methods to WebApplicationBuilder.Configuration
#pragma warning restore ASP0013

        // Migrating away from using Startup class as per link below.
        // https://andrewlock.net/exploring-dotnet-6-part-12-upgrading-a-dotnet-5-startup-based-app-to-dotnet-6/#option-2-re-use-your-startup-class
        var startup = startupFunc.Invoke(builder);
        startup.ConfigureServices(builder.Services);

        var app = builder.Build();

        startup.Configure(app, app.Environment);

        app.Run();
    }
}
