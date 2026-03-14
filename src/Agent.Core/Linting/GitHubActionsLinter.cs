using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Agent.Core.Linting;

/// <summary>
/// Lints a GitHub Actions workflow for supply-chain and token-scope problems: actions and reusable workflows not
/// pinned to a commit SHA, <c>pull_request_target</c> workflows that check out code, missing or overly broad
/// <c>permissions</c>, and untrusted event fields interpolated straight into <c>run</c> scripts.
/// </summary>
public sealed partial class GitHubActionsLinter
{
    public const string Name = "github-actions";

    public LintReport Lint(string yaml)
    {
        var findings = new List<LintFinding>();
        var stream = new YamlStream();

        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (YamlException ex)
        {
            findings.Add(new LintFinding("GHA000", "invalid-yaml", Severity.Error,
                $"The workflow is not valid YAML: {ex.Message}", LineOf(ex.Start)));
            return new LintReport(Name, findings);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root || Child<YamlMappingNode>(root, "jobs") is not { } jobs)
        {
            findings.Add(new LintFinding("GHA000", "invalid-yaml", Severity.Error,
                "The document has no 'jobs' mapping, so it is not a GitHub Actions workflow.", 1));
            return new LintReport(Name, findings);
        }

        var triggers = Triggers(root);
        var pullRequestTarget = triggers.Contains("pull_request_target");
        var workflowPermissions = Child<YamlNode>(root, "permissions");
        CheckBroadPermissions(workflowPermissions, "workflow", findings);

        foreach (var (key, value) in jobs.Children)
        {
            if (key is not YamlScalarNode { Value: { } jobId } || value is not YamlMappingNode job)
            {
                continue;
            }

            var target = $"job {jobId}";
            var jobPermissions = Child<YamlNode>(job, "permissions");
            CheckBroadPermissions(jobPermissions, target, findings);
            if (workflowPermissions is null && jobPermissions is null)
            {
                findings.Add(new LintFinding("GHA003", "missing-permissions", Severity.Warning,
                    "Neither the workflow nor this job sets 'permissions', so GITHUB_TOKEN gets the repository default, which may include write access. Add 'permissions: contents: read' and grant more per job only where needed.",
                    LineOf(key.Start), target));
            }

            if (Child<YamlScalarNode>(job, "uses") is { Value: { Length: > 0 } reusable } reusableNode)
            {
                CheckPinned(reusable, reusableNode, target, findings);
            }

            var steps = Child<YamlSequenceNode>(job, "steps")?.OfType<YamlMappingNode>().ToArray() ?? [];
            for (var i = 0; i < steps.Length; i++)
            {
                CheckStep(steps[i], $"{target} step {i + 1}", pullRequestTarget, findings);
            }
        }

        return new LintReport(Name, findings);
    }

    private static void CheckStep(YamlMappingNode step, string owner, bool pullRequestTarget, List<LintFinding> findings)
    {
        var target = Scalar(step, "name") is { } name ? $"{owner} ({name})" : owner;

        if (Child<YamlScalarNode>(step, "uses") is { Value: { Length: > 0 } uses } usesNode)
        {
            CheckPinned(uses, usesNode, target, findings);
            if (pullRequestTarget && uses.StartsWith("actions/checkout@", StringComparison.OrdinalIgnoreCase))
            {
                CheckPullRequestTargetCheckout(step, usesNode, target, findings);
            }
        }

        if (Child<YamlScalarNode>(step, "run") is { Value: { } script } runNode && UntrustedExpression().Match(script) is { Success: true } match)
        {
            findings.Add(new LintFinding("GHA005", "script-injection", Severity.Error,
                $"The run script interpolates '{match.Value.Trim()}', which the author of an issue, comment or pull request controls, so they can inject shell commands. Pass it through an env: variable and quote it in the script.",
                LineOf(runNode.Start), target));
        }
    }

    private static void CheckPinned(string uses, YamlScalarNode node, string target, List<LintFinding> findings)
    {
        if (uses.StartsWith("./", StringComparison.Ordinal))
        {
            return;
        }

        if (uses.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            if (!uses.Contains("@sha256:", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new LintFinding("GHA001", "unpinned-action", Severity.Warning,
                    $"'{uses}' is a container image without a digest, so its contents can change between runs. Pin it with @sha256:<digest>.",
                    LineOf(node.Start), target));
            }

            return;
        }

        var at = uses.LastIndexOf('@');
        var reference = at < 0 ? null : uses[(at + 1)..];
        if (reference is not null && CommitSha().IsMatch(reference))
        {
            return;
        }

        var message = reference is null
            ? $"'{uses}' has no @ref. Pin it to a full commit SHA."
            : $"'{uses}' is pinned to '{reference}', a tag or branch that can be moved to different code. Pin the full 40-character commit SHA and keep the version in a comment.";
        findings.Add(new LintFinding("GHA001", "unpinned-action", Severity.Warning, message, LineOf(node.Start), target));
    }

    private static void CheckPullRequestTargetCheckout(YamlMappingNode step, YamlScalarNode usesNode, string target, List<LintFinding> findings)
    {
        var refNode = Child<YamlScalarNode>(Child<YamlMappingNode>(step, "with"), "ref");
        if (refNode?.Value is { } checkoutRef && PullRequestHead().IsMatch(checkoutRef))
        {
            findings.Add(new LintFinding("GHA002", "pull-request-target-checkout", Severity.Error,
                $"pull_request_target runs with a read/write token and repository secrets, and this step checks out the pull request's own code ({checkoutRef}). Any later step that builds or runs it hands those to the PR author. Use the pull_request trigger, or never execute the checked-out code.",
                LineOf(refNode.Start), target));
            return;
        }

        findings.Add(new LintFinding("GHA002", "pull-request-target-checkout", Severity.Warning,
            "Checkout in a pull_request_target workflow. It checks out the base branch by default, which is safe only as long as no step fetches or runs code from the pull request; consider the pull_request trigger instead.",
            LineOf(usesNode.Start), target));
    }

    private static void CheckBroadPermissions(YamlNode? permissions, string target, List<LintFinding> findings)
    {
        if (permissions is YamlScalarNode { Value: "write-all" } scalar)
        {
            findings.Add(new LintFinding("GHA004", "broad-permissions", Severity.Warning,
                "'permissions: write-all' gives GITHUB_TOKEN write access to everything. List only the scopes the jobs need.",
                LineOf(scalar.Start), target));
        }
    }

    /// <summary>Event names from <c>on</c>, which can be a string, a list or a mapping.</summary>
    private static HashSet<string> Triggers(YamlMappingNode root)
    {
        var on = Child<YamlNode>(root, "on");
        IEnumerable<string?> names = on switch
        {
            YamlScalarNode scalar => [scalar.Value],
            YamlSequenceNode sequence => sequence.OfType<YamlScalarNode>().Select(s => s.Value),
            YamlMappingNode mapping => mapping.Children.Keys.OfType<YamlScalarNode>().Select(s => s.Value),
            _ => [],
        };

        return names.OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static T? Child<T>(YamlMappingNode? node, string key)
        where T : YamlNode =>
        node is not null && node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child as T : null;

    private static string? Scalar(YamlMappingNode node, string key) =>
        Child<YamlScalarNode>(node, key)?.Value is { Length: > 0 } value ? value : null;

    private static int LineOf(Mark mark) => (int)mark.Line;

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();

    [GeneratedRegex(@"github\.event\.pull_request\.head|github\.head_ref|refs/pull/")]
    private static partial Regex PullRequestHead();

    [GeneratedRegex(@"\$\{\{\s*(?:github\.event\.(?:issue\.(?:title|body)|pull_request\.(?:title|body|head\.ref|head\.label)|comment\.body|review\.body|review_comment\.body|discussion\.(?:title|body)|head_commit\.(?:message|author\.name|author\.email)|commits\[?[^\s}]*\]?\.message|pages\[?[^\s}]*\]?\.page_name)|github\.head_ref)\s*\}\}")]
    private static partial Regex UntrustedExpression();
}
