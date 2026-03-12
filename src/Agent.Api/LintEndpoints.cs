using System.Text;
using Agent.Core.Linting;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;

namespace Agent.Api;

/// <summary>
/// Lint endpoints call the plugins directly; no model is involved. The request body is the raw file
/// (for example <c>curl --data-binary @Dockerfile</c>) and the response is the structured <see cref="LintReport"/>.
/// </summary>
public static class LintEndpoints
{
    public static IEndpointRouteBuilder MapLintEndpoints(this IEndpointRouteBuilder app)
    {
        var lint = app.MapGroup("/lint").WithTags("Lint");

        lint.MapPost("/k8s", (HttpRequest request, KubernetesManifestPlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
            LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintManifest, cancellationToken));

        lint.MapPost("/dockerfile", (HttpRequest request, DockerfilePlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
            LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintDockerfile, cancellationToken));

        lint.MapPost("/terraform", (HttpRequest request, TerraformPlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
            LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintTerraform, cancellationToken));

        return app;
    }

    private static async Task<IResult> LintBodyAsync(HttpRequest request, long maxBytes, Func<string, LintReport> lint, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return TooLarge(maxBytes);
        }

        var body = await ReadLimitedAsync(request.Body, maxBytes, cancellationToken);
        if (body is null)
        {
            return TooLarge(maxBytes);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Results.Problem("Send the file contents as the request body.", statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(lint(body));
    }

    /// <summary>Reads the body as UTF-8, or returns null as soon as it exceeds <paramref name="maxBytes"/>.</summary>
    private static async Task<string?> ReadLimitedAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static IResult TooLarge(long maxBytes) =>
        Results.Problem($"The body is larger than the {maxBytes}-byte limit.", statusCode: StatusCodes.Status413PayloadTooLarge);
}
