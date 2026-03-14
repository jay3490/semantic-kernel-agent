using Agent.Core.Linting;

namespace Agent.Tests.Linting;

public sealed class GitHubActionsLinterTests
{
    private const string Sha = "11bd71901bbe5b1630ceea73d27597364c9af683";

    private readonly GitHubActionsLinter _linter = new();

    private static string Workflow(string on, string steps, string permissions = "permissions:\n  contents: read\n") =>
        $"on: {on}\n{permissions}jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n{steps}";

    [Fact]
    public void Good_sample_passes_with_no_findings()
    {
        var report = _linter.Lint(Samples.Read("github-actions/good-workflow.yml"));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Bad_sample_reports_every_rule()
    {
        var report = _linter.Lint(Samples.Read("github-actions/bad-workflow.yml"));

        Assert.Equal(["GHA001", "GHA001", "GHA001", "GHA001", "GHA002", "GHA003", "GHA003", "GHA004", "GHA005"],
            report.Findings.Select(f => f.RuleId).Order());
        Assert.Equal((2, 7), (report.Errors, report.Warnings));
    }

    [Fact]
    public void Findings_point_at_the_job_step_and_line()
    {
        var report = _linter.Lint(Samples.Read("github-actions/bad-workflow.yml"));

        var unpinned = report.Findings.Where(f => f.RuleId == "GHA001").ToDictionary(f => f.Line!.Value, f => f.Target);
        Assert.Equal("job preview step 1", unpinned[12]);
        Assert.Equal("job preview step 2", unpinned[15]);
        Assert.Equal("job label step 1", unpinned[26]);
        Assert.Equal("job reuse", unpinned[31]);

        var checkout = Assert.Single(report.Findings, f => f.RuleId == "GHA002");
        Assert.Equal((Severity.Error, 14), (checkout.Severity, checkout.Line!.Value));
        var injection = Assert.Single(report.Findings, f => f.RuleId == "GHA005");
        Assert.Equal(("job preview step 4 (Comment)", 19), (injection.Target, injection.Line!.Value));
        Assert.Contains("github.event.pull_request.title", injection.Message);
        Assert.Equal(["job preview", "job reuse"], report.Findings.Where(f => f.RuleId == "GHA003").Select(f => f.Target).Order());
    }

    [Theory]
    [InlineData("actions/checkout@" + Sha, false)]
    [InlineData("actions/checkout@v4", true)]
    [InlineData("actions/checkout@v4.2.2", true)]
    [InlineData("actions/checkout", true)]
    [InlineData("github/codeql-action/init@" + Sha, false)]
    [InlineData("./local/action", false)]
    [InlineData("docker://alpine:3.20", true)]
    [InlineData("docker://alpine@sha256:0000000000000000000000000000000000000000000000000000000000000000", false)]
    public void Actions_must_be_pinned_to_a_commit_sha(string uses, bool flagged)
    {
        var report = _linter.Lint(Workflow("push", $"      - uses: {uses}\n"));

        Assert.Equal(flagged, report.Findings.Any(f => f.RuleId == "GHA001"));
    }

    [Fact]
    public void Base_branch_checkout_under_pull_request_target_is_a_warning()
    {
        var report = _linter.Lint(Workflow("[pull_request_target]", $"      - uses: actions/checkout@{Sha}\n"));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(("GHA002", Severity.Warning), (finding.RuleId, finding.Severity));
    }

    [Fact]
    public void Checkout_under_other_triggers_is_fine()
    {
        var report = _linter.Lint(Workflow("pull_request", $"      - uses: actions/checkout@{Sha}\n        with:\n          ref: ${{{{ github.head_ref }}}}\n"));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Workflow_level_permissions_cover_every_job_and_write_all_is_flagged()
    {
        var covered = _linter.Lint(Workflow("push", "      - run: make\n"));
        var missing = _linter.Lint(Workflow("push", "      - run: make\n", permissions: ""));
        var broad = _linter.Lint(Workflow("push", "      - run: make\n", permissions: "permissions: write-all\n"));

        Assert.Empty(covered.Findings);
        Assert.Equal(("GHA003", "job build"), (Assert.Single(missing.Findings).RuleId, Assert.Single(missing.Findings).Target));
        Assert.Equal(("GHA004", "workflow"), (Assert.Single(broad.Findings).RuleId, Assert.Single(broad.Findings).Target));
    }

    [Theory]
    [InlineData("echo ${{ github.event.issue.title }}", true)]
    [InlineData("echo ${{ github.event.comment.body }}", true)]
    [InlineData("git checkout ${{ github.head_ref }}", true)]
    [InlineData("echo ${{ github.event.pull_request.number }}", false)]
    [InlineData("echo \"$TITLE\"", false)]
    public void Untrusted_event_fields_in_run_scripts_are_injection(string script, bool flagged)
    {
        var report = _linter.Lint(Workflow("issues", $"      - run: {script}\n"));

        Assert.Equal(flagged, report.Findings.Any(f => f.RuleId == "GHA005"));
    }

    [Theory]
    [InlineData("jobs: [\n")]
    [InlineData("name: not a workflow\n")]
    public void Invalid_documents_are_reported(string yaml)
    {
        var finding = Assert.Single(_linter.Lint(yaml).Findings);

        Assert.Equal(("GHA000", Severity.Error), (finding.RuleId, finding.Severity));
    }
}
