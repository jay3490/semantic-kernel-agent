using System.ComponentModel;
using Agent.Core.Linting;
using Agent.Core.Linting.Terraform;
using Agent.Core.Workspace;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel functions that run static checks on Terraform configuration.</summary>
public sealed class TerraformPlugin(TerraformLinter linter, WorkspaceFiles files)
{
    public const string PluginName = "terraform";

    [KernelFunction("lint_terraform")]
    [Description("Runs static checks on Terraform (HCL) text: providers and modules without version pins, security group or firewall ingress open to 0.0.0.0/0, resources with public IPs or public endpoints, and taggable resources without tags. Returns a structured report.")]
    public LintReport LintTerraform(
        [Description("The contents of one .tf file.")] string hcl) =>
        linter.Lint(hcl);

    [KernelFunction("lint_terraform_path")]
    [Description("Reads a .tf file, or every .tf file directly inside a module directory, from the workspace and runs the same checks over them together. Findings name the file. Returns a structured report.")]
    public async Task<LintReport> LintTerraformPathAsync(
        [Description("Path of a .tf file or a module directory relative to the workspace root, for example samples/terraform/bad.")] string path,
        CancellationToken cancellationToken = default)
    {
        if (!files.DirectoryExists(path))
        {
            return linter.Lint([new TerraformFile(path, await files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))]);
        }

        // A module is the .tf files of one directory; subdirectories are separate modules.
        var prefix = files.DirectoryPrefix(path);
        var modulePaths = files.ListFiles(path, ".tf").Where(f => !f[prefix.Length..].Contains('/'));

        var module = new List<TerraformFile>();
        foreach (var file in modulePaths)
        {
            module.Add(new TerraformFile(file[prefix.Length..], await files.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)));
        }

        if (module.Count == 0)
        {
            throw new WorkspaceAccessException($"'{path}' contains no .tf files.");
        }

        return linter.Lint(module);
    }
}
