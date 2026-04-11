using System.Text;
using Agent.Api.OpenApi;
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
    public static RouteGroupBuilder MapLintEndpoints(this IEndpointRouteBuilder app)
    {
        var lint = app.MapGroup("/lint").WithTags("Lint");

        lint.MapPost("/k8s", (HttpRequest request, KubernetesManifestPlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
                LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintManifest, cancellationToken))
            .Document("LintKubernetes", "Lint Kubernetes manifests", "Kubernetes YAML (one or more documents)",
                "apiVersion: v1\nkind: Pod\nmetadata:\n  name: debug\nspec:\n  containers:\n    - name: shell\n      image: busybox\n");

        lint.MapPost("/dockerfile", (HttpRequest request, DockerfilePlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
                LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintDockerfile, cancellationToken))
            .Document("LintDockerfile", "Lint a Dockerfile", "Dockerfile text",
                "FROM node:latest\nADD . /app\nRUN npm ci\nCMD [\"node\", \"server.js\"]\n");

        lint.MapPost("/terraform", (HttpRequest request, TerraformPlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
                LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintTerraform, cancellationToken))
            .Document("LintTerraform", "Run static checks on Terraform", "The contents of one .tf file",
                "resource \"aws_security_group_rule\" \"ssh\" {\n  type        = \"ingress\"\n  from_port   = 22\n  to_port     = 22\n  protocol    = \"tcp\"\n  cidr_blocks = [\"0.0.0.0/0\"]\n}\n");

        lint.MapPost("/github-actions", (HttpRequest request, GitHubActionsPlugin plugin, IOptions<WorkspaceOptions> limits, CancellationToken cancellationToken) =>
                LintBodyAsync(request, limits.Value.MaxFileBytes, plugin.LintWorkflow, cancellationToken))
            .Document("LintGitHubActions", "Lint a GitHub Actions workflow", "Workflow YAML",
                "on: pull_request_target\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v4\n");

        return lint;
    }

    private static RouteHandlerBuilder Document(this RouteHandlerBuilder builder, string name, string summary, string bodyDescription, string example) =>
        builder
            .WithName(name)
            .WithSummary(summary)
            .WithDescription("No model is involved. Send the raw file as the body; the response is a LintReport with findings ordered by severity.")
            .Produces<LintReport>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AcceptsText(bodyDescription, example);

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
