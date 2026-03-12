using Agent.Core.Linting;
using Agent.Core.Linting.Helm;

namespace Agent.Tests.Linting.Helm;

public sealed class HelmChartLinterTests
{
    private const string ChartYaml = "apiVersion: v2\nname: demo\nversion: 0.1.0\nappVersion: \"1.0\"\n";

    private readonly HelmChartLinter _linter = new(new KubernetesManifestLinter());

    internal static Task<HelmChartFiles> LoadSampleAsync(string name)
    {
        var directory = Path.Combine(Samples.Root, "helm", name);
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace(Path.DirectorySeparatorChar, '/'));
        return HelmChartFiles.LoadAsync(files, (path, ct) => File.ReadAllTextAsync(Path.Combine(directory, path), ct));
    }

    [Fact]
    public async Task Good_chart_passes_with_no_findings()
    {
        var report = _linter.Lint(await LoadSampleAsync("good-chart"));

        Assert.Empty(report.Findings);
        Assert.Equal("helm", report.Linter);
    }

    [Fact]
    public async Task Bad_chart_reports_every_rule_against_the_template_line()
    {
        var report = _linter.Lint(await LoadSampleAsync("bad-chart"));

        Assert.Equal(["HELM002", "HELM002", "HELM003", "HELM004", "HELM005", "HELM006"], report.Findings.Select(f => f.RuleId).Order());
        Assert.All(report.Findings, f => Assert.Equal("templates/deployment.yaml", f.File));
        Assert.Equal(1, report.Errors);

        var latest = report.Findings.Where(f => f.RuleId == "HELM002").ToDictionary(f => f.Target!, f => f.Line);
        Assert.Equal(17, latest["Deployment/release-name-web container=web"]);
        Assert.Equal(25, latest["Deployment/release-name-web container=worker"]);
        Assert.Equal(21, Assert.Single(report.Findings, f => f.RuleId == "HELM006").Line);
        Assert.Equal(16, Assert.Single(report.Findings, f => f.RuleId == "HELM003").Line);
    }

    [Fact]
    public async Task Findings_name_the_values_key_that_needs_fixing()
    {
        var report = _linter.Lint(await LoadSampleAsync("bad-chart"));

        var limits = Assert.Single(report.Findings, f => f.RuleId == "HELM003");
        Assert.Contains(".Values.resources (values.yaml line 8)", limits.Message);
        var webImage = Assert.Single(report.Findings, f => f is { RuleId: "HELM002", Target: "Deployment/release-name-web container=web" });
        Assert.Contains(".Values.image.tag (values.yaml line 3)", webImage.Message);
        var workerImage = Assert.Single(report.Findings, f => f is { RuleId: "HELM002", Target: "Deployment/release-name-web container=worker" });
        Assert.DoesNotContain("image.tag", workerImage.Message);
        Assert.Contains(".Values.securityContext", Assert.Single(report.Findings, f => f.RuleId == "HELM006").Message);
    }

    [Fact]
    public void Missing_or_incomplete_chart_yaml_is_an_error()
    {
        var missing = _linter.Lint(new HelmChartFiles(null, null, []));
        var incomplete = _linter.Lint(new HelmChartFiles("apiVersion: v2\nname: demo\n", null, []));

        Assert.Equal("HELM000", Assert.Single(missing.Findings).RuleId);
        var finding = Assert.Single(incomplete.Findings);
        Assert.Contains("version", finding.Message);
        Assert.Equal("Chart.yaml", finding.File);
    }

    [Fact]
    public void Invalid_values_yaml_is_an_error_with_its_file()
    {
        var report = _linter.Lint(new HelmChartFiles(ChartYaml, "image:\n  tag: [unclosed\n", []));

        var finding = Assert.Single(report.Findings);
        Assert.Equal(("HELM000", Severity.Error, "values.yaml"), (finding.RuleId, finding.Severity, finding.File));
    }

    [Fact]
    public void Templates_that_cannot_be_rendered_or_parsed_are_reported_as_info()
    {
        var report = _linter.Lint(new HelmChartFiles(ChartYaml, null,
        [
            new HelmTemplateFile("templates/broken.yaml", "kind: Pod\n{{ if .Values.x }}\n"),
            new HelmTemplateFile("templates/odd.yaml", "kind: Pod\nmetadata: [\n"),
        ]));

        Assert.Equal(["HELM001", "HELM001"], report.Findings.Select(f => f.RuleId));
        Assert.All(report.Findings, f => Assert.Equal(Severity.Info, f.Severity));
        Assert.True(report.Passed);
    }

    [Fact]
    public void Disabled_optional_templates_are_not_linted()
    {
        const string template = "{{- if .Values.debug.enabled }}\napiVersion: v1\nkind: Pod\nmetadata:\n  name: debug\nspec:\n  containers:\n    - name: shell\n      image: busybox\n{{- end }}\n";

        var off = _linter.Lint(new HelmChartFiles(ChartYaml, "debug:\n  enabled: false\n", [new HelmTemplateFile("templates/debug.yaml", template)]));
        var on = _linter.Lint(new HelmChartFiles(ChartYaml, "debug:\n  enabled: true\n", [new HelmTemplateFile("templates/debug.yaml", template)]));

        Assert.Empty(off.Findings);
        Assert.Contains(on.Findings, f => f.RuleId == "HELM002");
    }

    [Fact]
    public async Task Loader_skips_partials_notes_and_files_outside_templates()
    {
        var chart = await LoadSampleAsync("good-chart");

        Assert.Equal(["templates/deployment.yaml", "templates/hpa.yaml", "templates/service.yaml"], chart.Templates.Select(t => t.Path));
        Assert.NotNull(chart.ChartYaml);
        Assert.NotNull(chart.ValuesYaml);
    }
}
