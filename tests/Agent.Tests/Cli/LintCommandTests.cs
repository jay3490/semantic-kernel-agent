extern alias cli;

using System.Text.Json;
using LintCommand = cli::Agent.Cli.LintCommand;

namespace Agent.Tests.Cli;

public sealed class LintCommandTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("lint-cli-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await LintCommand.RunAsync(args, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string Sample(string path) => Path.Combine(Samples.Root, path);

    [Fact]
    public async Task Clean_inputs_exit_zero()
    {
        var (exitCode, stdout, stderr) = await RunAsync(Sample("docker/Dockerfile.good"), Sample("kubernetes/good-deployment.yaml"), Sample("helm/good-chart"));

        Assert.Equal(LintCommand.Passed, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("(dockerfile): passed", stdout);
        Assert.Contains("(helm): passed", stdout);
        Assert.Contains("3 target(s): 0 error(s)", stdout);
    }

    [Fact]
    public async Task Errors_exit_one_and_warnings_only_with_fail_on_warning()
    {
        var withErrors = await RunAsync(Sample("kubernetes/bad-deployment.yaml"));
        var warningsOnly = await RunAsync(Sample("docker/Dockerfile.bad"));
        var strict = await RunAsync("--fail-on", "warning", Sample("docker/Dockerfile.bad"));
        var lenient = await RunAsync("--fail-on=never", Sample("kubernetes/bad-deployment.yaml"));

        Assert.Equal(LintCommand.FindingsFound, withErrors.ExitCode);
        Assert.Equal(LintCommand.Passed, warningsOnly.ExitCode);
        Assert.Equal(LintCommand.FindingsFound, strict.ExitCode);
        Assert.Equal(LintCommand.Passed, lenient.ExitCode);
        Assert.Contains("error K8S006 privileged-container", withErrors.Stdout);
    }

    [Fact]
    public async Task Directories_are_searched_and_each_kind_is_detected()
    {
        var (_, stdout, _) = await RunAsync("--format", "json", "--fail-on", "never", Samples.Root);

        using var json = JsonDocument.Parse(stdout);
        var targets = json.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => (Path: r.GetProperty("path").GetString()!, Type: r.GetProperty("type").GetString()!))
            .ToDictionary(t => t.Path[(Samples.Root.Replace('\\', '/').Length + 1)..], t => t.Type);

        Assert.Equal("dockerfile", targets["docker/Dockerfile.bad"]);
        Assert.Equal("kubernetes", targets["kubernetes/good-deployment.yaml"]);
        Assert.Equal("helm", targets["helm/bad-chart"]);
        Assert.Equal("terraform", targets["terraform/good"]);
        Assert.Equal("github-actions", targets["github-actions/bad-workflow.yml"]);
        Assert.DoesNotContain(targets.Keys, k => k.StartsWith("helm/good-chart/", StringComparison.Ordinal) || k.StartsWith("logs/", StringComparison.Ordinal));
        Assert.True(json.RootElement.GetProperty("summary").GetProperty("errors").GetInt32() > 0);
    }

    [Fact]
    public async Task Sarif_output_lists_rules_once_and_points_into_chart_files()
    {
        var output = Path.Combine(_directory, "results.sarif");

        var (exitCode, stdout, _) = await RunAsync("-f", "sarif", "-o", output, Sample("helm/bad-chart"), Sample("docker/Dockerfile.bad"));

        Assert.Equal(LintCommand.FindingsFound, exitCode);
        Assert.Empty(stdout);
        using var sarif = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        Assert.Equal("2.1.0", sarif.RootElement.GetProperty("version").GetString());
        var run = sarif.RootElement.GetProperty("runs")[0];
        Assert.Equal("devops-agent", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());

        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Equal(rules.Distinct().Count(), rules.Count);
        Assert.Contains("HELM002", rules);

        var privileged = run.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "HELM006");
        Assert.Equal("error", privileged.GetProperty("level").GetString());
        Assert.Equal(rules.IndexOf("HELM006"), privileged.GetProperty("ruleIndex").GetInt32());
        var location = privileged.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.EndsWith("helm/bad-chart/templates/deployment.yaml", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(21, location.GetProperty("region").GetProperty("startLine").GetInt32());

        var info = run.GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "DF004");
        Assert.Equal("note", info.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Type_overrides_detection()
    {
        var file = Path.Combine(_directory, "build.txt");
        await File.WriteAllTextAsync(file, "FROM ubuntu\n");

        var detected = await RunAsync(file);
        var forced = await RunAsync("--type", "dockerfile", "--fail-on", "warning", file);

        Assert.Equal(LintCommand.UsageError, detected.ExitCode);
        Assert.Contains("pass --type", detected.Stderr);
        Assert.Equal(LintCommand.FindingsFound, forced.ExitCode);
        Assert.Contains("DF001", forced.Stdout);
    }

    [Theory]
    [InlineData(new string[0], "at least one file")]
    [InlineData(new[] { "--format", "xml", "x" }, "unknown format 'xml'")]
    [InlineData(new[] { "--fail-on", "sometimes", "x" }, "unknown --fail-on value")]
    [InlineData(new[] { "--type", "ansible", "x" }, "unknown type 'ansible'")]
    [InlineData(new[] { "--verbose", "x" }, "unknown option '--verbose'")]
    [InlineData(new[] { "--output" }, "--output needs a file name")]
    public async Task Usage_errors_exit_two(string[] args, string message)
    {
        var (exitCode, _, stderr) = await RunAsync(args);

        Assert.Equal(LintCommand.UsageError, exitCode);
        Assert.Contains(message, stderr);
    }

    [Fact]
    public async Task Missing_paths_exit_two_even_when_others_were_linted()
    {
        var (exitCode, stdout, stderr) = await RunAsync(Sample("docker/Dockerfile.good"), Path.Combine(_directory, "nope.yaml"));

        Assert.Equal(LintCommand.UsageError, exitCode);
        Assert.Contains("no such file or directory", stderr);
        Assert.Contains("Dockerfile.good (dockerfile): passed", stdout);
    }

    [Fact]
    public async Task Help_prints_usage_and_exits_zero()
    {
        var (exitCode, stdout, _) = await RunAsync("--help");

        Assert.Equal(LintCommand.Passed, exitCode);
        Assert.Contains("Usage: devops-agent lint", stdout);
    }
}
