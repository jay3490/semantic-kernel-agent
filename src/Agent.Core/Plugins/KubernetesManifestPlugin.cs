using System.ComponentModel;
using Agent.Core.Linting;
using Agent.Core.Workspace;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel functions that lint Kubernetes manifests.</summary>
public sealed class KubernetesManifestPlugin(KubernetesManifestLinter linter, WorkspaceFiles files)
{
    public const string PluginName = "kubernetes";

    [KernelFunction("lint_manifest")]
    [Description("Lints Kubernetes YAML for missing resource limits, missing liveness/readiness probes, 'latest' or untagged images, privileged containers and missing namespaces. Returns a structured report of findings.")]
    public LintReport LintManifest(
        [Description("The manifest text. May contain several YAML documents separated by ---.")] string yaml) =>
        linter.Lint(yaml);

    [KernelFunction("lint_manifest_file")]
    [Description("Reads a Kubernetes manifest from the workspace and lints it. Returns a structured report of findings.")]
    public async Task<LintReport> LintManifestFileAsync(
        [Description("Path of the YAML file relative to the workspace root, for example samples/kubernetes/bad-deployment.yaml.")] string path,
        CancellationToken cancellationToken = default) =>
        linter.Lint(await files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
}
