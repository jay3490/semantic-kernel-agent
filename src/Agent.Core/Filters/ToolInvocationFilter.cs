using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Agent.Core.Filters;

/// <summary>
/// Runs around every kernel function invocation: refuses functions that are not on the allow-list and logs
/// each call with its (truncated) arguments, duration and outcome.
/// </summary>
public sealed partial class ToolInvocationFilter(ToolAllowList allowList, ILogger<ToolInvocationFilter> logger) : IFunctionInvocationFilter
{
    private const int MaxLoggedArgumentLength = 80;

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        var function = context.Function;
        var tool = $"{function.PluginName}.{function.Name}";

        if (!allowList.IsAllowed(function.PluginName, function.Name))
        {
            LogBlocked(tool);
            throw new ToolNotAllowedException(tool);
        }

        LogCalling(tool, DescribeArguments(context.Arguments));
        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context).ConfigureAwait(false);
            LogCompleted(tool, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, tool, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private static string DescribeArguments(KernelArguments arguments) =>
        string.Join(", ", arguments.Select(a => $"{a.Key}={Truncate(a.Value?.ToString())}"));

    private static string Truncate(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        var singleLine = value.ReplaceLineEndings(" ");
        return singleLine.Length <= MaxLoggedArgumentLength ? singleLine : $"{singleLine[..MaxLoggedArgumentLength]}... ({value.Length} chars)";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Tool call {Tool}({Arguments})")]
    private partial void LogCalling(string tool, string arguments);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Tool call {Tool} completed in {ElapsedMs:F1} ms")]
    private partial void LogCompleted(string tool, double elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Tool call {Tool} failed after {ElapsedMs:F1} ms")]
    private partial void LogFailed(Exception exception, string tool, double elapsedMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Tool call {Tool} blocked: not on the allow-list")]
    private partial void LogBlocked(string tool);
}
