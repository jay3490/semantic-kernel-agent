using Agent.Core.Linting;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;

namespace Agent.Tests.Plugins;

public sealed class DockerfilePluginTests
{
    private static DockerfilePlugin CreatePlugin() =>
        new(new DockerfileLinter(), new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = Samples.Root })));

    [Fact]
    public async Task Lints_a_file_from_the_workspace()
    {
        var report = await CreatePlugin().LintDockerfileFileAsync("docker/Dockerfile.bad");

        Assert.Equal(6, report.Findings.Count);
    }

    [Fact]
    public void Lints_inline_text()
    {
        var report = CreatePlugin().LintDockerfile("FROM alpine\n");

        Assert.Contains(report.Findings, f => f.RuleId == "DF001");
    }
}
