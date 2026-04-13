using Agent.Core.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agent.Api.Telemetry;

/// <summary>OpenTelemetry settings. Bound from the <c>Telemetry</c> configuration section.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    public string ServiceName { get; set; } = "devops-agent-api";

    /// <summary>
    /// Turns on Semantic Kernel's own GenAI spans and metrics for model calls (prompt and completion text are
    /// never recorded). Kernel function spans are always on.
    /// </summary>
    public bool SemanticKernelModelDiagnostics { get; set; } = true;

    /// <summary>Path of the Prometheus scrape endpoint; empty turns it off.</summary>
    public string PrometheusEndpoint { get; set; } = "/metrics";
}

public static class TelemetrySetup
{
    /// <summary>Activity sources and meters Semantic Kernel emits: kernel functions, connectors and model diagnostics.</summary>
    public const string SemanticKernelSources = "Microsoft.SemanticKernel*";

    private const string SemanticKernelDiagnosticsSwitch = "Microsoft.SemanticKernel.Experimental.GenAI.EnableOTelDiagnostics";

    /// <summary>
    /// Traces: incoming requests, outgoing HTTP calls (including the model endpoint), the agent's turn, model and
    /// tool spans, and Semantic Kernel's spans. Metrics: the same, plus the agent's tool, model and token counters.
    /// Traces and metrics go to OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; metrics are also served for
    /// Prometheus.
    /// </summary>
    public static IServiceCollection AddAgentTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new TelemetryOptions();
        services.Configure<TelemetryOptions>(configuration.GetSection(TelemetryOptions.SectionName));

        if (options.SemanticKernelModelDiagnostics)
        {
            AppContext.SetSwitch(SemanticKernelDiagnosticsSwitch, true);
        }

        var otlp = !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AgentTelemetry.SourceName, SemanticKernelSources)
                    .AddAspNetCoreInstrumentation(aspNet => aspNet.Filter = context => !IsInfrastructure(context.Request.Path, options))
                    .AddHttpClientInstrumentation();
                if (otlp)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AgentTelemetry.SourceName, SemanticKernelSources)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(options.PrometheusEndpoint))
                {
                    metrics.AddPrometheusExporter();
                }

                if (otlp)
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }

    /// <summary>Maps the Prometheus scrape endpoint. It is not behind the API key: restrict it at the network level.</summary>
    public static WebApplication MapAgentMetrics(this WebApplication app)
    {
        var path = app.Configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()?.PrometheusEndpoint ?? new TelemetryOptions().PrometheusEndpoint;
        if (!string.IsNullOrWhiteSpace(path))
        {
            app.MapPrometheusScrapingEndpoint(path);
        }

        return app;
    }

    private static bool IsInfrastructure(PathString path, TelemetryOptions options) =>
        path.StartsWithSegments("/health") ||
        path.StartsWithSegments("/swagger") ||
        (!string.IsNullOrWhiteSpace(options.PrometheusEndpoint) && path.StartsWithSegments(options.PrometheusEndpoint));
}
