using Agent.Core.Linting;

namespace Agent.Tests.Linting;

public sealed class KubernetesManifestLinterTests
{
    private readonly KubernetesManifestLinter _linter = new();

    [Fact]
    public void Good_sample_passes_with_no_findings()
    {
        var report = _linter.Lint(Samples.Read("kubernetes/good-deployment.yaml"));

        Assert.Empty(report.Findings);
        Assert.True(report.Passed);
    }

    [Fact]
    public void Bad_sample_reports_every_rule()
    {
        var report = _linter.Lint(Samples.Read("kubernetes/bad-deployment.yaml"));

        Assert.False(report.Passed);
        Assert.Equal(["K8S001", "K8S002", "K8S003", "K8S004", "K8S005", "K8S006"],
            report.Findings.Select(f => f.RuleId).Distinct().Order());
        Assert.Equal(1, report.Errors);
        Assert.Equal(11, report.Warnings);
        Assert.Equal(1, report.Infos);
    }

    [Fact]
    public void Findings_point_at_the_offending_container_and_line()
    {
        var report = _linter.Lint(Samples.Read("kubernetes/bad-deployment.yaml"));

        var privileged = Assert.Single(report.Findings, f => f.RuleId == "K8S006");
        Assert.Equal("Deployment/web container=log-shipper", privileged.Target);
        Assert.Equal(26, privileged.Line);
        Assert.Equal(report.Findings[0], privileged);

        var latest = report.Findings.Where(f => f.RuleId == "K8S005").ToDictionary(f => f.Target!, f => f.Line);
        Assert.Equal(17, latest["Deployment/web container=nginx"]);
        Assert.Equal(21, latest["Deployment/web container=log-shipper"]);
        Assert.Equal(36, latest["Pod/debug container=shell"]);
    }

    [Fact]
    public void Missing_limits_message_names_only_the_missing_resource()
    {
        var report = _linter.Lint(Samples.Read("kubernetes/bad-deployment.yaml"));

        var shipper = Assert.Single(report.Findings, f => f is { RuleId: "K8S002", Target: "Deployment/web container=log-shipper" });
        Assert.Contains("no cpu limit", shipper.Message);
        var nginx = Assert.Single(report.Findings, f => f is { RuleId: "K8S002", Target: "Deployment/web container=nginx" });
        Assert.Contains("no cpu or memory limit", nginx.Message);
    }

    [Theory]
    [InlineData("nginx", false)]
    [InlineData("nginx:latest", false)]
    [InlineData("nginx:LATEST", false)]
    [InlineData("localhost:5000/team/app", false)]
    [InlineData("localhost:5000/team/app:1.2.3", true)]
    [InlineData("nginx@sha256:0123abcd", true)]
    [InlineData("nginx:latest@sha256:0123abcd", true)]
    [InlineData("nginx:1.27-alpine", true)]
    public void Image_pinning_is_judged_by_tag_and_digest(string image, bool pinned)
    {
        var yaml = $$"""
            apiVersion: batch/v1
            kind: Job
            metadata: { name: once, namespace: ops }
            spec:
              template:
                spec:
                  restartPolicy: Never
                  containers:
                    - name: main
                      image: {{image}}
                      resources: { limits: { cpu: 100m, memory: 64Mi } }
            """;

        var report = _linter.Lint(yaml);

        Assert.Equal(!pinned, report.Findings.Any(f => f.RuleId == "K8S005"));
    }

    [Fact]
    public void Batch_workloads_and_run_once_pods_are_not_asked_for_probes()
    {
        const string yaml = """
            apiVersion: v1
            kind: Pod
            metadata: { name: migrate, namespace: ops }
            spec:
              restartPolicy: Never
              containers:
                - name: main
                  image: app:1.0
                  resources: { limits: { cpu: 100m, memory: 64Mi } }
            """;

        Assert.Empty(_linter.Lint(yaml).Findings);
    }

    [Fact]
    public void Cluster_scoped_objects_do_not_need_a_namespace()
    {
        const string yaml = """
            apiVersion: v1
            kind: Namespace
            metadata: { name: shop }
            ---
            apiVersion: rbac.authorization.k8s.io/v1
            kind: ClusterRole
            metadata: { name: reader }
            rules: []
            """;

        Assert.Empty(_linter.Lint(yaml).Findings);
    }

    [Fact]
    public void Items_of_a_List_are_linted()
    {
        const string yaml = """
            apiVersion: v1
            kind: List
            items:
              - apiVersion: v1
                kind: ConfigMap
                metadata: { name: settings }
            """;

        var finding = Assert.Single(_linter.Lint(yaml).Findings);
        Assert.Equal("K8S001", finding.RuleId);
        Assert.Equal("ConfigMap/settings", finding.Target);
    }

    [Fact]
    public void Invalid_yaml_is_reported_as_a_finding()
    {
        var report = _linter.Lint("kind: Pod\nmetadata:\n  name: [unclosed\n");

        var finding = Assert.Single(report.Findings);
        Assert.Equal("K8S000", finding.RuleId);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.False(report.Passed);
    }
}
