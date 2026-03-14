using Agent.Core.Linting;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Plugins;

public sealed class GitHubActionsPluginTests
{
    private static GitHubActionsPlugin CreatePlugin() =>
        new(new GitHubActionsLinter(), new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = Samples.Root })));

    [Fact]
    public async Task Lints_a_workflow_file_from_the_workspace()
    {
        var report = await CreatePlugin().LintWorkflowFileAsync("github-actions/bad-workflow.yml");

        Assert.Equal(2, report.Errors);
    }

    [Fact]
    public async Task Is_exposed_to_the_kernel_with_both_functions()
    {
        var kernel = new Kernel();
        var plugin = kernel.Plugins.AddFromObject(CreatePlugin(), GitHubActionsPlugin.PluginName);

        Assert.Equal(["lint_workflow", "lint_workflow_file"], plugin.Select(f => f.Name).Order());
        var result = await kernel.InvokeAsync(GitHubActionsPlugin.PluginName, "lint_workflow",
            new KernelArguments { ["yaml"] = Samples.Read("github-actions/good-workflow.yml") });

        Assert.True(result.GetValue<LintReport>()!.Passed);
    }
}
