// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
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
        this.Configuration = config;
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
                    options.IncludeScopes = true;
                    options.ParseStateValues = true;

                    // Render the log message so the exported `body` contains the
                    // formatted text (e.g. "Request finished HTTP/1.1 GET ...")
                    // instead of the raw template ("Request finished {Protocol}
                    // {Method} ..."). Structured params are still emitted as
                    // attributes because ParseStateValues stays true.
                    options.IncludeFormattedMessage = true;

                    // Enabling IncludeFormattedMessage makes the OTLP exporter
                    // emit an extra "{OriginalFormat}" attribute (the message
                    // template). The braces in that key are invalid
                    // Kusto/Geneva column identifiers and cause the record to be
                    // silently dropped during Geneva -> Kusto (GDC) ingestion.
                    // Drop it here since the rendered text is already in the body.
                    options.AddProcessor(new DropOriginalFormatAttributeProcessor());

                    options.SetResourceBuilder(this.CreateResourceBuilder());
                    options.AddOtlpExporter();
                });
            }

            configure?.Invoke(builder);
        });
        this.Logger = this.loggerFactory.CreateLogger(name);
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
            app.UseSwagger();
            app.UseSwaggerUI();
        }

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
        var attributes = new List<KeyValuePair<string, object>>();

        // Per-process dimensions for Geneva Dgrep columns.
        var service = Environment.GetEnvironmentVariable("SERVICE_NAME");
        if (!string.IsNullOrEmpty(service))
        {
            attributes.Add(new("Service", service));
        }

        var cluster = Environment.GetEnvironmentVariable("CR_CLUSTER");
        if (!string.IsNullOrEmpty(cluster))
        {
            attributes.Add(new("Cluster", cluster));
        }

        var container = Environment.GetEnvironmentVariable("CONTAINER_NAME");
        if (!string.IsNullOrEmpty(container))
        {
            attributes.Add(new("Container", container));
            attributes.Add(new("k8s.deployment.name", container));
        }

        var buildVersion = Environment.GetEnvironmentVariable("BUILD_VERSION");
        if (!string.IsNullOrEmpty(buildVersion))
        {
            attributes.Add(new("Build", buildVersion));
        }

        var podName = Environment.GetEnvironmentVariable("POD_NAME");
        if (!string.IsNullOrEmpty(podName))
        {
            attributes.Add(new("k8s.pod.name", podName));
        }

        return ResourceBuilder.CreateDefault()
            .AddService(serviceName)
            .AddAttributes(attributes);
    }
}

/// <summary>
/// Removes the "{OriginalFormat}" attribute from exported log records.
/// </summary>
/// <remarks>
/// When <c>IncludeFormattedMessage</c> is enabled the OTLP exporter emits an
/// extra attribute keyed "{OriginalFormat}" (the message template). The braces
/// in that key are not valid Kusto/Geneva column identifiers, which causes the
/// whole record to be silently dropped during Geneva -&gt; Kusto (GDC)
/// ingestion. The template is redundant because the rendered text is already
/// carried in the log body.
/// </remarks>
internal sealed class DropOriginalFormatAttributeProcessor : BaseProcessor<LogRecord>
{
    private const string OriginalFormatKey = "{OriginalFormat}";

    public override void OnEnd(LogRecord data)
    {
        if (data.Attributes is { } attributes
            && attributes.Any(a => a.Key == OriginalFormatKey))
        {
            data.Attributes = attributes.Where(a => a.Key != OriginalFormatKey).ToList();
        }
    }
}