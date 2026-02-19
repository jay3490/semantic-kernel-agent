using System.ComponentModel;
using Agent.Core.Logs;
using Agent.Core.Workspace;
using Microsoft.SemanticKernel;

namespace Agent.Core.Plugins;

/// <summary>Kernel functions that summarize application logs.</summary>
public sealed class LogAnalysisPlugin(LogAnalyzer analyzer, WorkspaceFiles files)
{
    public const string PluginName = "logs";

    [KernelFunction("analyze_log_file")]
    [Description("Reads a log file from the workspace, counts entries per level and groups ERROR/FATAL entries by message signature (ids, numbers and addresses masked). Returns the most frequent error signatures with counts and line numbers.")]
    public async Task<LogAnalysisReport> AnalyzeLogFileAsync(
        [Description("Path of the log file relative to the workspace root, for example samples/logs/orders-api.log.")] string path,
        [Description("How many error signatures to return, 1 to 50.")] int top = 5,
        CancellationToken cancellationToken = default) =>
        analyzer.Analyze(await files.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), top);

    [KernelFunction("analyze_log_text")]
    [Description("Same analysis as analyze_log_file, for log lines passed directly.")]
    public LogAnalysisReport AnalyzeLogText(
        [Description("The log lines.")] string text,
        [Description("How many error signatures to return, 1 to 50.")] int top = 5) =>
        analyzer.Analyze(text, top);
}
