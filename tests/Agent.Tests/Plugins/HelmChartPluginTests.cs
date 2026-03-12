using Agent.Core.Linting;
using Agent.Core.Linting.Helm;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Plugins;

public sealed class HelmChartPluginTests
{
    private static HelmChartPlugin CreatePlugin() =>
        new(new HelmChartLinter(new KubernetesManifestLinter()), new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = Samples.Root })));

    [Fact]
    public async Task Lints_a_chart_directory_from_the_workspace()
    {
        var report = await CreatePlugin().LintHelmChartAsync("helm/bad-chart");

        Assert.Equal(6, report.Findings.Count);
        Assert.All(report.Findings, f => Assert.Equal("templates/deployment.yaml", f.File));
    }

    [Fact]
    public async Task A_directory_without_a_chart_is_an_invalid_chart()
    {
        var report = await CreatePlugin().LintHelmChartAsync("docker");

        Assert.Equal("HELM000", Assert.Single(report.Findings).RuleId);
    }

    [Fact]
    public async Task Paths_outside_the_workspace_are_refused()
    {
        await Assert.ThrowsAsync<WorkspaceAccessException>(() => CreatePlugin().LintHelmChartAsync("../.."));
    }

    [Fact]
    public async Task Is_exposed_to_the_kernel()
    {
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(CreatePlugin(), HelmChartPlugin.PluginName);

        var result = await kernel.InvokeAsync(HelmChartPlugin.PluginName, "lint_helm_chart", new KernelArguments { ["path"] = "helm/good-chart" });

        Assert.True(result.GetValue<LintReport>()!.Passed);
    }
}
