// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Controllers;

/// <summary>
/// The startup class.
/// </summary>
public abstract class ApiStartup
{
    private readonly ILoggerFactory loggerFactory;

    protected ApiStartup(
        IConfiguration config,
        string name,
        Action<ILoggingBuilder>? configure = null)
    {
        this.ServiceName = name;
        this.loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddConfiguration(config.GetSection("Logging"));
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = false;
                options.TimestampFormat = "yyyy-MM-ddThh:mm:ssZ ";
                options.UseUtcTimestamp = true;
                options.SingleLine = true;
            });

            if (this.EnableOpenTelemetry)
            {
                builder.AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(this.CreateResourceBuilder());
                    options.AddOtlpExporter();
                });
            }

            configure?.Invoke(builder);
        });
        this.Logger = this.loggerFactory.CreateLogger(name);
        this.Configuration = config;
    }

    public ILogger Logger { get; }

    public IConfiguration Configuration { get; }

    public string ServiceName { get; }

    public virtual string? OTelServiceName { get; } = null;

    public abstract bool EnableOpenTelemetry { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.Filters.Add<GlobalActionFilter>();
            options.Filters.Add<ApiExceptionFilter>();
            options.Filters.Add<HttpRequestWithStatusExceptionFilter>();
            options.ModelMetadataDetailsProviders.Add(
                new SystemTextJsonValidationMetadataProvider());
        });
        services.AddSwaggerGen();
        services.AddSingleton(this.loggerFactory);
        services.AddSingleton(this.Logger);

        if (this.EnableOpenTelemetry)
        {
            services.AddOpenTelemetry()
                .WithTracing(tracing =>
                {
                    tracing
                        .SetResourceBuilder(this.CreateResourceBuilder())
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddProcessor(new BaggageSpanProcessor())
                        .AddOtlpExporter();
                })
                .WithMetrics(metrics =>
                {
                    metrics
                        .SetResourceBuilder(this.CreateResourceBuilder())
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddMeter(this.OTelServiceName ?? this.ServiceName)
                        .AddOtlpExporter();
                });
        }

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

        this.OnConfigure(app, env);
    }

    public virtual void OnConfigure(WebApplication app, IWebHostEnvironment env)
    {
    }

    private ResourceBuilder CreateResourceBuilder()
    {
        var serviceName = this.OTelServiceName ?? this.ServiceName;
        var builder = ResourceBuilder.CreateDefault()
            .AddService(serviceName);

        var deploymentName = Environment.GetEnvironmentVariable("DEPLOYMENT_NAME");
        if (!string.IsNullOrEmpty(deploymentName))
        {
            builder.AddAttributes(new Dictionary<string, object>
            {
                ["k8s.deployment.name"] = deploymentName
            });
        }

        return builder;
    }
}