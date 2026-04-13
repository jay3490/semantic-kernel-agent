using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Core.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.Review;

/// <summary>The raw result of one lint tool call made during a turn.</summary>
/// <param name="Tool">Plugin and function, for example <c>helm.lint_helm_chart</c>.</param>
/// <param name="Json">The result exactly as the model received it.</param>
public sealed record LintEvidence(string Tool, string Json);

/// <summary>How the reviewer judged a draft answer.</summary>
/// <param name="Verdict">
/// <c>approved</c>: the draft matches the tool results. <c>revised</c>: the reviewer replaced it with a corrected
/// answer. <c>flagged</c>: problems were found but no correction was given, so the draft was kept.
/// <c>unavailable</c>: the review could not be completed, so the draft was kept.
/// </param>
/// <param name="Issues">What the reviewer and the automatic checks found wrong with the draft.</param>
public sealed record AnswerReview(string Verdict, IReadOnlyList<string> Issues)
{
    public const string Approved = "approved";
    public const string Revised = "revised";
    public const string Flagged = "flagged";
    public const string Unavailable = "unavailable";
}

/// <summary>A review and the answer that should be sent: the draft, or the reviewer's correction.</summary>
public sealed record ReviewOutcome(AnswerReview Review, string Answer);

/// <summary>
/// A second agent pass over answers that summarize lint results. The primary agent drafts an answer from the tool
/// results; this reviewer gets the question, the draft and the raw results, with no tools, and either approves the
/// draft or returns a corrected one. Before asking the model it runs a deterministic check: every rule id the draft
/// mentions must appear in some result, so an invented finding is caught even if the reviewer model misses it.
/// </summary>
public sealed partial class LintAnswerReviewer(IOptions<AgentOptions> options, AgentTelemetry telemetry, ILogger<LintAnswerReviewer> logger)
{
    /// <summary>Raw results longer than this are cut, so a huge report cannot crowd the reviewer's context.</summary>
    internal const int MaxEvidenceCharacters = 24_000;

    private static readonly JsonSerializerOptions VerdictJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // No tools: the reviewer judges what it is shown and cannot go and fetch more.
    private static readonly PromptExecutionSettings ReviewSettings = new();

    public async Task<ReviewOutcome> ReviewAsync(
        IChatCompletionService chat,
        string question,
        string draft,
        IReadOnlyList<LintEvidence> evidence,
        ActivityContext parent,
        CancellationToken cancellationToken)
    {
        var unsupported = UnsupportedRuleIds(draft, evidence);
        List<string> automaticIssues = unsupported.Count == 0
            ? []
            : new List<string> { $"The draft cites rule ids that no tool result contains: {string.Join(", ", unsupported)}." };

        var history = new ChatHistory(options.Value.ReviewerPrompt);
        history.AddUserMessage(BuildRequest(question, draft, evidence, automaticIssues));

        string reply;
        using var activity = AgentTelemetry.StartModelCall("review", AgentTelemetry.ModelIdOf(chat), parent);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await chat.GetChatMessageContentAsync(history, ReviewSettings, kernel: null, cancellationToken).ConfigureAwait(false);
            telemetry.RecordModelCall(activity, "review", succeeded: true, Stopwatch.GetElapsedTime(started), response.Metadata);
            reply = response.Content ?? string.Empty;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            telemetry.RecordModelCall(activity, "review", succeeded: false, Stopwatch.GetElapsedTime(started), error: ex.Message);
            LogReviewFailed(ex);
            return new ReviewOutcome(new AnswerReview(AnswerReview.Unavailable, [.. automaticIssues, $"The reviewer model call failed: {ChatModelFailure.Describe(ex)}"]), draft);
        }

        if (ParseVerdict(reply) is not { } verdict)
        {
            LogUnparsedVerdict(reply.Length);
            return new ReviewOutcome(new AnswerReview(AnswerReview.Unavailable, [.. automaticIssues, "The reviewer's reply was not a JSON verdict."]), draft);
        }

        var issues = automaticIssues.Concat(verdict.Issues ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        var revise = string.Equals(verdict.Verdict, "revise", StringComparison.OrdinalIgnoreCase);

        if (revise && !string.IsNullOrWhiteSpace(verdict.RevisedAnswer))
        {
            return new ReviewOutcome(new AnswerReview(AnswerReview.Revised, issues), verdict.RevisedAnswer.Trim());
        }

        return issues.Count > 0 || revise
            ? new ReviewOutcome(new AnswerReview(AnswerReview.Flagged, issues), draft)
            : new ReviewOutcome(new AnswerReview(AnswerReview.Approved, []), draft);
    }

    /// <summary>Rule ids such as K8S002 or HELM004 that appear in the draft but in none of the results.</summary>
    internal static IReadOnlyList<string> UnsupportedRuleIds(string draft, IReadOnlyList<LintEvidence> evidence)
    {
        var known = evidence.SelectMany(e => RuleId().Matches(e.Json)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        return RuleId().Matches(draft).Select(m => m.Value).Where(id => !known.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string BuildRequest(string question, string draft, IReadOnlyList<LintEvidence> evidence, IReadOnlyList<string> automaticIssues)
    {
        var request = new StringBuilder()
            .AppendLine("Question:").AppendLine(question).AppendLine()
            .AppendLine("Draft answer:").AppendLine(draft).AppendLine();

        if (automaticIssues.Count > 0)
        {
            request.AppendLine("Automatic checks found:");
            foreach (var issue in automaticIssues)
            {
                request.Append("- ").AppendLine(issue);
            }

            request.AppendLine();
        }

        request.AppendLine("Tool results:");
        var budget = MaxEvidenceCharacters;
        foreach (var item in evidence)
        {
            var json = item.Json.Length <= budget ? item.Json : item.Json[..Math.Max(0, budget)] + " ...(truncated)";
            budget -= json.Length;
            request.Append('[').Append(item.Tool).AppendLine("]").AppendLine(json);
        }

        return request.ToString();
    }

    /// <summary>Reads the JSON object in the reply, tolerating a code fence or text around it.</summary>
    private static VerdictReply? ParseVerdict(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var verdict = JsonSerializer.Deserialize<VerdictReply>(reply[start..(end + 1)], VerdictJson);
            return verdict?.Verdict is null ? null : verdict;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class VerdictReply
    {
        public string? Verdict { get; init; }

        public List<string>? Issues { get; init; }

        public string? RevisedAnswer { get; init; }
    }

    [GeneratedRegex(@"\b(?:K8S|DF|HELM|TF|GHA)\d{3}\b")]
    private static partial Regex RuleId();

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "The answer review failed; the draft answer was sent unreviewed")]
    private partial void LogReviewFailed(Exception exception);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "The reviewer's reply ({Length} characters) held no JSON verdict; the draft answer was sent unreviewed")]
    private partial void LogUnparsedVerdict(int length);
}
