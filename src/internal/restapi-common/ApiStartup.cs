// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Controllers;

/// <summary>
/// The startup class.
/// </summary>
public class ApiStartup
{
    private readonly ILoggerFactory loggerFactory;

    public ApiStartup(IConfiguration config, string name)
    {
        this.loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "yyyy-MM-ddThh:mm:ssZ ";
                options.UseUtcTimestamp = true;
            });
        });
        this.Logger = this.loggerFactory.CreateLogger(name);
        this.Configuration = config;
    }

    public ILogger Logger { get; }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.Filters.Add<ApiExceptionFilter>();
            options.Filters.Add<HttpRequestWithStatusExceptionFilter>();
        });
        services.AddSwaggerGen();
        services.AddSingleton(this.Logger);
        this.OnConfigureServices(services);
    }

    public virtual void OnConfigureServices(IServiceCollection services)
    {
    }

#pragma warning disable VSSpell001 // Spell Check
    public void Configure(WebApplication app, IWebHostEnvironment env)
#pragma warning restore VSSpell001 // Spell Check
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseSwagger();
        app.UseSwaggerUI();

        app.UseAuthorization();

        app.MapControllers();
    }
}