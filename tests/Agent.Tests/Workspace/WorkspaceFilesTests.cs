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

    [Fact]
    public async Task Lists_files_recursively_as_root_relative_paths()
    {
        Directory.CreateDirectory(Path.Combine(_root, "chart", "templates"));
        await File.WriteAllTextAsync(Path.Combine(_root, "chart", "Chart.yaml"), "name: x");
        await File.WriteAllTextAsync(Path.Combine(_root, "chart", "templates", "svc.yaml"), "kind: Service");
        await File.WriteAllTextAsync(Path.Combine(_root, "chart", "README.md"), "# x");

        var files = Create();

        Assert.Equal(["chart/Chart.yaml", "chart/README.md", "chart/templates/svc.yaml"], files.ListFiles("chart"));
        Assert.Equal(["chart/Chart.yaml", "chart/templates/svc.yaml"], files.ListFiles("chart", ".yaml"));
        Assert.Equal(3, files.ListFiles(".").Count);
        Assert.Equal("chart/", files.DirectoryPrefix("chart/templates/.."));
        Assert.Equal("", files.DirectoryPrefix("."));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("chart/../..")]
    public void Listing_refuses_directories_outside_the_root(string path)
    {
        Assert.Throws<WorkspaceAccessException>(() => Create().ListFiles(path));
    }

    [Fact]
    public void Listing_a_missing_directory_is_an_access_error()
    {
        var error = Assert.Throws<WorkspaceAccessException>(() => Create().ListFiles("nope"));

        Assert.Contains("not a directory", error.Message);
        Assert.False(Create().DirectoryExists("nope"));
    }
}
