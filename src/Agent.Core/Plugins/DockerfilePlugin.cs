using System.ComponentModel;
using Agent.Core.Linting;
using Agent.Core.Workspace;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel functions that lint Dockerfiles.</summary>
public sealed class DockerfilePlugin(DockerfileLinter linter, WorkspaceFiles files)
{
    public const string PluginName = "dockerfile";

    [KernelFunction("lint_dockerfile")]
    [Description("Lints a Dockerfile for unpinned base images, running as root, ADD used instead of COPY, a missing HEALTHCHECK and apt caches left in the image. Returns a structured report of findings.")]
    public LintReport LintDockerfile(
        [Description("The full Dockerfile text.")] string dockerfile) =>
        linter.Lint(dockerfile);

    [KernelFunction("lint_dockerfile_file")]
    [Description("Reads a Dockerfile from the workspace and lints it. Returns a structured report of findings.")]
    public async Task<LintReport> LintDockerfileFileAsync(
        [Description("Path of the Dockerfile relative to the workspace root, for example samples/docker/Dockerfile.bad.")] string path,
        CancellationToken cancellationToken = default) =>
        linter.Lint(await files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
}
