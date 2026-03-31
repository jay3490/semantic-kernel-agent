using Agent.Core;
using Agent.Core.Linting;
using Agent.Core.Logs;
using Agent.Core.Plugins;
using Agent.Core.Review;
using Agent.Core.Workspace;

namespace Agent.Cli;

/// <summary>
/// Interactive loop. Plain lines go to the agent and the reply is streamed; lines starting with '/' are commands,
/// some of which call the plugins directly so they work without a chat model.
/// </summary>
internal sealed class Repl(
    DevOpsAgent agent,
    KubernetesManifestPlugin kubernetes,
    DockerfilePlugin dockerfile,
    LogAnalysisPlugin logs,
    WorkspaceFiles workspace,
    TextReader input,
    TextWriter output)
{
    private const string SessionId = "cli";

    private CancellationTokenSource? _currentTurn;

    public async Task RunAsync()
    {
        output.WriteLine("DevOps agent. Type a question, or /help for commands.");
        output.WriteLine($"Workspace: {workspace.Root}");

        while (true)
        {
            output.Write("> ");
            var line = await input.ReadLineAsync();
            if (line is null)
            {
                return;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('/'))
            {
                if (!await RunCommandAsync(line))
                {
                    return;
                }

                continue;
            }

            await ChatAsync(line);
        }
    }

    /// <summary>Cancels the reply in progress, if there is one. Returns false when nothing was running.</summary>
    public bool CancelCurrentTurn()
    {
        var turn = _currentTurn;
        if (turn is null)
        {
            return false;
        }

        turn.Cancel();
        return true;
    }

    private async Task<bool> RunCommandAsync(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "/exit" or "/quit":
                    return false;
                case "/reset":
                    await agent.ResetAsync(SessionId);
                    output.WriteLine("Conversation cleared.");
                    break;
                case "/k8s" when parts.Length == 2:
                    PrintReport(await kubernetes.LintManifestFileAsync(parts[1]));
                    break;
                case "/docker" when parts.Length == 2:
                    PrintReport(await dockerfile.LintDockerfileFileAsync(parts[1]));
                    break;
                case "/logs" when parts.Length is 2 or 3:
                    var top = parts.Length == 3 && int.TryParse(parts[2], out var n) ? n : 5;
                    PrintLogReport(await logs.AnalyzeLogFileAsync(parts[1], top));
                    break;
                default:
                    PrintHelp();
                    break;
            }
        }
        catch (WorkspaceAccessException ex)
        {
            output.WriteLine($"error: {ex.Message}");
        }

        return true;
    }

    private async Task ChatAsync(string message)
    {
        using var turn = new CancellationTokenSource();
        _currentTurn = turn;
        var midLine = false;

        try
        {
            await foreach (var streamEvent in agent.StreamChatAsync(SessionId, message, turn.Token))
            {
                switch (streamEvent)
                {
                    case ToolCallStarted started:
                        output.Write($"  [tool] {started.Tool} ... ");
                        break;
                    case ToolCallFinished finished:
                        output.WriteLine(finished.Call.Succeeded ? "ok" : $"failed: {finished.Call.Error}");
                        break;
                    case TextDelta delta:
                        output.Write(delta.Text);
                        midLine = true;
                        break;
                    case ReviewCompleted reviewed:
                        output.WriteLine();
                        output.Write($"  [review] {reviewed.Review.Verdict}");
                        foreach (var issue in reviewed.Review.Issues)
                        {
                            output.Write($"{Environment.NewLine}    - {issue}");
                        }

                        midLine = true;
                        break;
                    case ReplyCompleted { Reply.Review.Verdict: AnswerReview.Revised } revised:
                        output.WriteLine();
                        output.Write($"Revised answer:{Environment.NewLine}{revised.Reply.Reply}");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            output.Write(midLine ? Environment.NewLine : string.Empty);
            output.Write("(cancelled)");
        }
        catch (ChatModelNotConfiguredException ex)
        {
            output.Write(ex.Message);
            output.Write(" The /k8s, /docker and /logs commands work without a model.");
        }
        catch (Exception ex) when (ChatModelFailure.IsModelFailure(ex))
        {
            output.Write(midLine ? Environment.NewLine : string.Empty);
            output.Write($"error: the chat model request failed ({ChatModelFailure.Describe(ex)})");
        }
        finally
        {
            _currentTurn = null;
        }

        output.WriteLine();
    }

    private void PrintReport(LintReport report)
    {
        foreach (var finding in report.Findings)
        {
            var line = finding.Line is { } l ? $"line {l}" : "";
            output.WriteLine($"  {finding.Severity,-7} {finding.RuleId} {line,-8} {finding.Target}");
            output.WriteLine($"          {finding.Message}");
        }

        output.WriteLine($"{report.Errors} error(s), {report.Warnings} warning(s), {report.Infos} info: {(report.Passed ? "passed" : "failed")}");
    }

    private void PrintLogReport(LogAnalysisReport report)
    {
        output.WriteLine($"{report.Entries} entries in {report.TotalLines} lines: " +
                         string.Join(", ", report.Levels.OrderBy(l => l.Key).Select(l => $"{l.Key} {l.Value}")));
        output.WriteLine($"{report.ErrorEntries} error entries, {report.DistinctErrorSignatures} distinct signatures");
        foreach (var group in report.TopErrors)
        {
            output.WriteLine($"  {group.Count,4}x  {group.Signature}  (lines {group.FirstLine}-{group.LastLine})");
        }
    }

    private void PrintHelp()
    {
        output.WriteLine("""
            Ask anything in plain text; the agent decides which tools to use.
              /k8s <path>          lint a Kubernetes manifest (no model needed)
              /docker <path>       lint a Dockerfile (no model needed)
              /logs <path> [top]   summarize errors in a log file (no model needed)
              /reset               forget the conversation
              /exit                quit
            Paths are relative to the workspace. Ctrl+C cancels a reply in progress.
            """);
    }
}
