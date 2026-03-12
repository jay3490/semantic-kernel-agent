using Agent.Core.Linting;
using Agent.Core.Linting.Terraform;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Plugins;

public sealed class TerraformPluginTests
{
    private static TerraformPlugin CreatePlugin() =>
        new(new TerraformLinter(), new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = Samples.Root })));

    [Fact]
    public async Task Lints_every_tf_file_of_a_module_directory_together()
    {
        var report = await CreatePlugin().LintTerraformPathAsync("terraform/good");

        // versions.tf declares the provider used in main.tf, so neither file alone would pass.
        Assert.True(report.Passed);
        Assert.All(report.Findings, f => Assert.Equal("main.tf", f.File));
    }

    [Fact]
    public async Task Lints_a_single_file()
    {
        var report = await CreatePlugin().LintTerraformPathAsync("terraform/good/main.tf");

        Assert.False(report.Passed);
        Assert.Contains(report.Findings, f => f is { RuleId: "TF001", Target: "provider aws" });
    }

    [Fact]
    public async Task A_directory_without_tf_files_is_refused()
    {
        var error = await Assert.ThrowsAsync<WorkspaceAccessException>(() => CreatePlugin().LintTerraformPathAsync("docker"));

        Assert.Contains("no .tf files", error.Message);
    }

    [Fact]
    public async Task Is_exposed_to_the_kernel_with_both_functions()
    {
        var kernel = new Kernel();
        var plugin = kernel.Plugins.AddFromObject(CreatePlugin(), TerraformPlugin.PluginName);

        Assert.Equal(["lint_terraform", "lint_terraform_path"], plugin.Select(f => f.Name).Order());
        var result = await kernel.InvokeAsync(TerraformPlugin.PluginName, "lint_terraform",
            new KernelArguments { ["hcl"] = Samples.Read("terraform/bad/main.tf") });

        Assert.Equal(3, result.GetValue<LintReport>()!.Errors);
    }
}
