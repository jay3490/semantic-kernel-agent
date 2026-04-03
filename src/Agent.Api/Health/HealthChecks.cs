using System.Text.Json;
using Agent.Core.Runbooks;
using Agent.Core.Workspace;
using Agent.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Api.Health;

/// <summary>The workspace root the tools read from must exist.</summary>
public sealed class WorkspaceHealthCheck(WorkspaceFiles files) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Directory.Exists(files.Root)
            ? HealthCheckResult.Healthy($"Workspace root {files.Root} exists.")
            : HealthCheckResult.Unhealthy($"Workspace root {files.Root} does not exist."));
}

/// <summary>Degraded, not unhealthy, without a chat model: the lint endpoints still work. The model itself is not called.</summary>
public sealed class ChatModelHealthCheck(Kernel kernel) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(kernel.Services.GetService<IChatCompletionService>() is null
            ? HealthCheckResult.Degraded("No chat model is configured; chat endpoints return 503.")
            : HealthCheckResult.Healthy("A chat model is configured."));
}

/// <summary>Degraded when the runbook folder is missing: everything else works, but runbook search fails.</summary>
public sealed class RunbookHealthCheck(WorkspaceFiles files, IOptions<RunbookOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var directory = options.Value.Directory;
        try
        {
            return Task.FromResult(files.DirectoryExists(directory)
                ? HealthCheckResult.Healthy($"Runbook folder '{directory}' exists.")
                : HealthCheckResult.Degraded($"Runbook folder '{directory}' does not exist; runbook search will fail."));
        }
        catch (WorkspaceAccessException ex)
        {
            return Task.FromResult(HealthCheckResult.Degraded(ex.Message));
        }
    }
}

public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddAgentHealthChecks(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHealthChecks()
            .AddCheck<WorkspaceHealthCheck>("workspace", tags: [ReadyTag])
            .AddCheck<ChatModelHealthCheck>("chat-model", tags: [ReadyTag])
            .AddCheck<RunbookHealthCheck>("runbooks", tags: [ReadyTag])
            .AddSessionStoreCheck(configuration, ReadyTag);
        return services;
    }

    /// <summary>
    /// <c>/health/live</c> runs no checks (the process answers), for liveness probes; <c>/health/ready</c> and
    /// <c>/health</c> run every check, for readiness probes and the container HEALTHCHECK. Degraded is still 200.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync }).WithTags("Health");
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag), ResponseWriter = WriteAsync }).WithTags("Health");
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteAsync }).WithTags("Health");
        return app;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description ?? e.Value.Exception?.Message,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted);
    }
}
