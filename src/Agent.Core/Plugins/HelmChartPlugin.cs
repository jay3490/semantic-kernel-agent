using System.ComponentModel;
using Agent.Core.Linting;
using Agent.Core.Linting.Helm;
using Agent.Core.Workspace;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel functions that lint Helm charts.</summary>
public sealed class HelmChartPlugin(HelmChartLinter linter, WorkspaceFiles files)
{
    public const string PluginName = "helm";

    [KernelFunction("lint_helm_chart")]
    [Description("Lints a Helm chart directory: validates Chart.yaml and values.yaml, renders the templates with the default values and checks the workloads for 'latest' or untagged images, missing resource limits, missing liveness/readiness probes and privileged containers. Findings name the template file and line, and the values.yaml key to change. Returns a structured report.")]
    public async Task<LintReport> LintHelmChartAsync(
        [Description("Path of the chart directory (the one containing Chart.yaml) relative to the workspace root, for example samples/helm/bad-chart.")] string path,
        CancellationToken cancellationToken = default)
    {
        var prefix = files.DirectoryPrefix(path);
        var chartFiles = files.ListFiles(path);

        var chart = await HelmChartFiles.LoadAsync(
            chartFiles.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)).Select(f => f[prefix.Length..]),
            (relative, ct) => files.ReadAllTextAsync(prefix + relative, ct),
            cancellationToken).ConfigureAwait(false);

        return linter.Lint(chart);
    }
}
