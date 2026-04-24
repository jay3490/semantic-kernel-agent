using Agent.Core.Linting.Reporting;

namespace Agent.Tests.Deployment;

/// <summary>The repository's own deployment files must pass the project's linters.</summary>
public sealed class DeploymentFilesTests
{
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SemanticKernelAgent.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test assembly.");
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("deploy/kubernetes/base/deployment.yaml")]
    [InlineData("deploy/kubernetes/base/service.yaml")]
    [InlineData("deploy/kubernetes/base/hpa.yaml")]
    public async Task Passes_its_own_linter_without_warnings(string relativePath)
    {
        var runner = new LintRunner();
        var errors = new List<string>();
        var targets = runner.Resolve([Path.Combine(RepositoryRoot(), relativePath)], kind: null, errors);

        var result = Assert.Single(await runner.RunAsync(targets, errors));

        Assert.Empty(errors);
        Assert.True(result.Report.Passed, string.Join(Environment.NewLine, result.Report.Findings.Select(f => $"{f.RuleId} {f.Message}")));
    }

    [Fact]
    public void Kustomization_lists_every_manifest_in_the_base()
    {
        var baseDirectory = Path.Combine(RepositoryRoot(), "deploy", "kubernetes", "base");
        var kustomization = File.ReadAllText(Path.Combine(baseDirectory, "kustomization.yaml"));

        var manifests = Directory.EnumerateFiles(baseDirectory, "*.yaml")
            .Select(Path.GetFileName)
            .Where(name => name != "kustomization.yaml");

        Assert.All(manifests, name => Assert.Contains($"- {name}", kustomization));
    }
}
