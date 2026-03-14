using System.ComponentModel;
using Agent.Core.Linting;
using Agent.Core.Workspace;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel functions that lint GitHub Actions workflows.</summary>
public sealed class GitHubActionsPlugin(GitHubActionsLinter linter, WorkspaceFiles files)
{
    public const string PluginName = "github_actions";

    [KernelFunction("lint_workflow")]
    [Description("Lints GitHub Actions workflow YAML for actions not pinned to a commit SHA, pull_request_target workflows that check out code, missing or write-all permissions, and untrusted event fields interpolated into run scripts. Returns a structured report.")]
    public LintReport LintWorkflow(
        [Description("The workflow YAML text.")] string yaml) =>
        linter.Lint(yaml);

    [KernelFunction("lint_workflow_file")]
    [Description("Reads a GitHub Actions workflow file from the workspace and lints it. Returns a structured report.")]
    public async Task<LintReport> LintWorkflowFileAsync(
        [Description("Path of the workflow file relative to the workspace root, for example samples/github-actions/bad-workflow.yml.")] string path,
        CancellationToken cancellationToken = default) =>
        linter.Lint(await files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
}
