using Agent.Core.Logs;
using Agent.Core.Plugins;
using Agent.Core.Workspace;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;

namespace Agent.Tests.Plugins;

public sealed class LogAnalysisPluginTests
{
    private static LogAnalysisPlugin CreatePlugin() =>
        new(new LogAnalyzer(), new WorkspaceFiles(Options.Create(new WorkspaceOptions { Root = Samples.Root })));

    [Fact]
    public async Task Analyzes_a_file_through_the_kernel()
    {
        var kernel = new Kernel();
        kernel.Plugins.AddFromObject(CreatePlugin(), LogAnalysisPlugin.PluginName);

        var result = await kernel.InvokeAsync(LogAnalysisPlugin.PluginName, "analyze_log_file",
            new KernelArguments { ["path"] = "logs/orders-api.log", ["top"] = "2" });

        var report = result.GetValue<LogAnalysisReport>()!;
        Assert.Equal(2, report.TopErrors.Count);
        Assert.Equal(4, report.TopErrors[0].Count);
    }

    [Fact]
    public async Task Refuses_files_outside_the_workspace()
    {
        await Assert.ThrowsAsync<WorkspaceAccessException>(() => CreatePlugin().AnalyzeLogFileAsync("../Agent.Tests.dll"));
    }
}
