using Agent.Core.Linting;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Plugins;

public sealed class KubernetesManifestPluginTests
{
    private static KubernetesManifestPlugin CreatePlugin() =>
        new(new KubernetesManifestLinter(), new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = Samples.Root })));

    [Fact]
    public async Task Lints_a_file_from_the_workspace()
    {
        var report = await CreatePlugin().LintManifestFileAsync("kubernetes/bad-deployment.yaml");

        Assert.Contains(report.Findings, f => f.RuleId == "K8S006");
    }

    [Fact]
    public async Task Is_exposed_to_the_kernel_with_both_functions()
    {
        var kernel = new Kernel();
        var plugin = kernel.Plugins.AddFromObject(CreatePlugin(), KubernetesManifestPlugin.PluginName);

        Assert.Equal(["lint_manifest", "lint_manifest_file"], plugin.Select(f => f.Name).Order());

        var result = await kernel.InvokeAsync(KubernetesManifestPlugin.PluginName, "lint_manifest",
            new KernelArguments { ["yaml"] = Samples.Read("kubernetes/good-deployment.yaml") });

        Assert.True(result.GetValue<LintReport>()!.Passed);
    }
}
