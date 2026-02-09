using Agent.Core.Workspace;
using Microsoft.Extensions.Options;

namespace Agent.Tests.Workspace;

public sealed class WorkspaceFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("workspace-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WorkspaceFiles Create(long maxBytes = 1024) =>
        new(Options.Create(new WorkspaceOptions { Root = _root, MaxFileBytes = maxBytes }));

    [Fact]
    public async Task Reads_a_file_inside_the_root()
    {
        Directory.CreateDirectory(Path.Combine(_root, "k8s"));
        await File.WriteAllTextAsync(Path.Combine(_root, "k8s", "app.yaml"), "kind: Pod");

        var text = await Create().ReadAllTextAsync("k8s/app.yaml");

        Assert.Equal("kind: Pod", text);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("k8s/../../outside.txt")]
    public async Task Rejects_paths_that_escape_the_root(string path)
    {
        var error = await Assert.ThrowsAsync<WorkspaceAccessException>(() => Create().ReadAllTextAsync(path));

        Assert.Contains("outside the workspace root", error.Message);
    }

    [Fact]
    public async Task Rejects_absolute_paths()
    {
        var absolute = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(absolute, "x");

        var error = await Assert.ThrowsAsync<WorkspaceAccessException>(() => Create().ReadAllTextAsync(absolute));

        Assert.Contains("absolute", error.Message);
    }

    [Fact]
    public async Task Rejects_files_over_the_size_limit()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "big.log"), new string('x', 2048));

        var error = await Assert.ThrowsAsync<WorkspaceAccessException>(() => Create(maxBytes: 1024).ReadAllTextAsync("big.log"));

        Assert.Contains("limit is 1024 bytes", error.Message);
    }

    [Fact]
    public async Task Reports_missing_files()
    {
        var error = await Assert.ThrowsAsync<WorkspaceAccessException>(() => Create().ReadAllTextAsync("nope.yaml"));

        Assert.Contains("does not exist", error.Message);
    }
}
