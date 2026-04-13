using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agent.Core.Filters;
using Agent.Core.History;
using Agent.Core.Linting;
using Agent.Core.Review;
using Agent.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core;

/// <summary>
/// Runs a conversation turn: append the user message, trim the history, ask the model, run any tool calls it
/// requests through the kernel (and therefore through the invocation filters), feed the results back, and repeat
/// until the model answers in plain text or the tool-round limit is reached.
/// </summary>
/// <remarks>
/// The model chooses tools automatically (<see cref="FunctionChoiceBehavior.Auto"/>), but invocation is done here
/// rather than inside the connector (<c>autoInvoke: false</c>). That keeps one tool loop for every
/// <see cref="IChatCompletionService"/> implementation, streaming or not, and lets the loop record each call.
/// </remarks>
public sealed partial class DevOpsAgent
{
    internal const string RoundLimitMessage = "Not executed: the tool round limit for this turn was reached.";

    // Tool results go to the model, not into HTML, so characters such as < and ' are left readable.
    private static readonly JsonSerializerOptions ToolResultJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Kernel _kernel;
    private readonly IChatSessionStore _sessions;
    private readonly TokenBudgetHistoryTrimmer _trimmer;
    private readonly AgentOptions _options;
    private readonly LintAnswerReviewer _reviewer;
    private readonly AgentTelemetry _telemetry;
    private readonly ILogger<DevOpsAgent> _logger;
    private readonly PromptExecutionSettings _toolSettings;
    private readonly PromptExecutionSettings _finalRoundSettings;

    public DevOpsAgent(
        Kernel kernel,
        IChatSessionStore sessions,
        TokenBudgetHistoryTrimmer trimmer,
        ToolAllowList allowList,
        IOptions<AgentOptions> options,
        LintAnswerReviewer reviewer,
        AgentTelemetry telemetry,
        ILogger<DevOpsAgent> logger)
    {
        _kernel = kernel;
        _sessions = sessions;
        _trimmer = trimmer;
        _options = options.Value;
        _reviewer = reviewer;
        _telemetry = telemetry;
        _logger = logger;

        // Only allowed functions are advertised to the model; the filter still guards invocation.
        var advertised = kernel.Plugins
            .SelectMany(plugin => plugin)
            .Where(function => allowList.IsAllowed(function.PluginName, function.Name))
            .ToArray();

        _toolSettings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(advertised, autoInvoke: false),
        };
        _finalRoundSettings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.None(advertised),
        };
    }

    /// <summary>Answers <paramref name="message"/> in the given session and returns the complete reply.</summary>
    public async Task<AgentReply> ChatAsync(string sessionId, string message, CancellationToken cancellationToken = default)
    {
        var chat = ResolveChatService();
        using var turn = AgentTelemetry.StartTurn(sessionId, streaming: false);
        var (session, turnStart) = await BeginTurnAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
        var completed = false;

        try
        {
            var toolCalls = new List<ToolCallRecord>();
            var evidence = new List<LintEvidence>();
            for (var round = 0; ; round++)
            {
                var response = await CallModelAsync(chat, session.History, SettingsFor(round), turn, cancellationToken).ConfigureAwait(false);

                var calls = FunctionCallContent.GetFunctionCalls(response).ToArray();
                session.History.Add(response);

                if (calls.Length == 0)
                {
                    var (answer, review) = await ReviewAsync(chat, session, message, response.Content ?? string.Empty, evidence, turn, cancellationToken)
                        .ConfigureAwait(false);
                    completed = true;
                    return new AgentReply(sessionId, answer, toolCalls, review);
                }

                if (round >= _options.MaxToolRounds)
                {
                    completed = true;
                    return EndAtRoundLimit(session, calls, toolCalls);
                }

                foreach (var call in calls)
                {
                    toolCalls.Add(await InvokeToolAsync(session.History, call, evidence, turn, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            MarkIncomplete(turn, completed);
            await EndTurnAsync(session, turnStart, completed).ConfigureAwait(false);
        }
    }

    /// <summary>Answers <paramref name="message"/> in the given session, yielding text and tool events as they happen.</summary>
    public async IAsyncEnumerable<AgentStreamEvent> StreamChatAsync(
        string sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chat = ResolveChatService();
        using var turn = AgentTelemetry.StartTurn(sessionId, streaming: true);
        var (session, turnStart) = await BeginTurnAsync(sessionId, message, cancellationToken).ConfigureAwait(false);
        var completed = false;

        try
        {
            var toolCalls = new List<ToolCallRecord>();
            var evidence = new List<LintEvidence>();
            for (var round = 0; ; round++)
            {
                var text = new StringBuilder();
                var callBuilder = new FunctionCallContentBuilder();

                // The span is parented explicitly: Activity.Current does not survive the yields of an async iterator.
                var modelCall = AgentTelemetry.StartModelCall("chat", AgentTelemetry.ModelIdOf(chat), turn?.Context ?? default);
                var started = Stopwatch.GetTimestamp();
                IReadOnlyDictionary<string, object?>? usage = null;
                var streamed = false;
                try
                {
                    await foreach (var update in chat.GetStreamingChatMessageContentsAsync(
                                       session.History, SettingsFor(round), _kernel, cancellationToken).ConfigureAwait(false))
                    {
                        callBuilder.Append(update);
                        if (update.Metadata?.ContainsKey("Usage") == true)
                        {
                            usage = update.Metadata;
                        }

                        if (!string.IsNullOrEmpty(update.Content))
                        {
                            text.Append(update.Content);
                            yield return new TextDelta(update.Content);
                        }
                    }

                    streamed = true;
                }
                finally
                {
                    _telemetry.RecordModelCall(modelCall, "chat", streamed, Stopwatch.GetElapsedTime(started), usage,
                        streamed ? null : "The streamed response did not complete.");
                    modelCall?.Dispose();
                }

                var calls = callBuilder.Build();
                session.History.Add(AssistantMessage(text.ToString(), calls));

                if (calls.Count == 0)
                {
                    var (answer, review) = await ReviewAsync(chat, session, message, text.ToString(), evidence, turn, cancellationToken)
                        .ConfigureAwait(false);
                    if (review is not null)
                    {
                        yield return new ReviewCompleted(review);
                    }

                    completed = true;
                    yield return new ReplyCompleted(new AgentReply(sessionId, answer, toolCalls, review));
                    yield break;
                }

                if (round >= _options.MaxToolRounds)
                {
                    completed = true;
                    yield return new ReplyCompleted(EndAtRoundLimit(session, calls, toolCalls));
                    yield break;
                }

                foreach (var call in calls)
                {
                    yield return new ToolCallStarted(ToolName(call));
                    var record = await InvokeToolAsync(session.History, call, evidence, turn, cancellationToken).ConfigureAwait(false);
                    toolCalls.Add(record);
                    yield return new ToolCallFinished(record);
                }
            }
        }
        finally
        {
            MarkIncomplete(turn, completed);
            await EndTurnAsync(session, turnStart, completed).ConfigureAwait(false);
        }
    }

    /// <summary>Forgets a session's history. Returns false when the session did not exist.</summary>
    public Task<bool> ResetAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _sessions.RemoveAsync(sessionId, cancellationToken);

    private IChatCompletionService ResolveChatService() =>
        _kernel.Services.GetService<IChatCompletionService>() ?? throw new ChatModelNotConfiguredException();

    /// <returns>The session, locked for this turn, and the index of the turn's user message.</returns>
    private async Task<(ChatSession Session, int TurnStart)> BeginTurnAsync(string sessionId, string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("The message is empty.", nameof(message));
        }

        if (message.Length > _options.MaxMessageCharacters)
        {
            throw new ArgumentException($"The message is {message.Length} characters; the limit is {_options.MaxMessageCharacters}.", nameof(message));
        }

        var session = await _sessions.GetOrCreateAsync(sessionId, _options.SystemPrompt, cancellationToken).ConfigureAwait(false);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        session.History.AddUserMessage(message);
        var removed = _trimmer.Trim(session.History, _options.HistoryTokenBudget);
        if (removed > 0)
        {
            LogTrimmed(sessionId, removed);
        }

        return (session, session.History.Count - 1);
    }

    /// <summary>
    /// Saves a completed turn and releases the session. A turn that did not complete (model error, cancellation,
    /// client gone) is removed from the history instead and never saved, so a half-finished tool exchange never
    /// reaches the model on the next turn.
    /// </summary>
    private async Task EndTurnAsync(ChatSession session, int turnStart, bool completed)
    {
        try
        {
            if (completed)
            {
                // The reply has been produced; save it even if the caller is no longer listening.
                await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                session.History.RemoveRange(turnStart, session.History.Count - turnStart);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private PromptExecutionSettings SettingsFor(int round) =>
        round < _options.MaxToolRounds ? _toolSettings : _finalRoundSettings;

    /// <summary>One non-streaming model request, traced and counted.</summary>
    private async Task<ChatMessageContent> CallModelAsync(
        IChatCompletionService chat, ChatHistory history, PromptExecutionSettings settings, Activity? turn, CancellationToken cancellationToken)
    {
        using var activity = AgentTelemetry.StartModelCall("chat", AgentTelemetry.ModelIdOf(chat), turn?.Context ?? default);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await chat.GetChatMessageContentAsync(history, settings, _kernel, cancellationToken).ConfigureAwait(false);
            _telemetry.RecordModelCall(activity, "chat", succeeded: true, Stopwatch.GetElapsedTime(started), response.Metadata);
            return response;
        }
        catch (Exception ex)
        {
            _telemetry.RecordModelCall(activity, "chat", succeeded: false, Stopwatch.GetElapsedTime(started), error: ex.Message);
            throw;
        }
    }

    private static void MarkIncomplete(Activity? turn, bool completed)
    {
        if (!completed)
        {
            turn?.SetStatus(ActivityStatusCode.Error, "The turn did not complete and was rolled back.");
        }
    }

    /// <summary>
    /// When reviewing is on and lint tools ran this turn, has the reviewer check the draft against their results.
    /// A revised answer replaces the draft in the history too, so the next turn builds on what the user saw.
    /// </summary>
    private async Task<(string Answer, AnswerReview? Review)> ReviewAsync(
        IChatCompletionService chat,
        ChatSession session,
        string question,
        string draft,
        IReadOnlyList<LintEvidence> evidence,
        Activity? turn,
        CancellationToken cancellationToken)
    {
        if (!_options.ReviewLintAnswers || evidence.Count == 0)
        {
            return (draft, null);
        }

        var outcome = await _reviewer.ReviewAsync(chat, question, draft, evidence, turn?.Context ?? default, cancellationToken).ConfigureAwait(false);
        if (outcome.Review.Verdict == AnswerReview.Revised)
        {
            session.History[^1] = new ChatMessageContent(AuthorRole.Assistant, outcome.Answer);
        }

        _telemetry.RecordReview(outcome.Review.Verdict);
        LogReviewed(session.Id, outcome.Review.Verdict, outcome.Review.Issues.Count);
        return (outcome.Answer, outcome.Review);
    }

    private async Task<ToolCallRecord> InvokeToolAsync(
        ChatHistory history, FunctionCallContent call, List<LintEvidence> evidence, Activity? turn, CancellationToken cancellationToken)
    {
        var tool = ToolName(call);

        // Tool names come from the model; unknown ones share one metric label so they cannot explode cardinality.
        var label = _kernel.Plugins.TryGetFunction(call.PluginName, call.FunctionName, out _) ? tool : "unknown";
        using var activity = AgentTelemetry.StartToolCall(label, call.Id, turn?.Context ?? default);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await call.InvokeAsync(_kernel, cancellationToken).ConfigureAwait(false);
            var json = result.Result switch
            {
                null => "null",
                string text => text,
                var value => JsonSerializer.Serialize(value, ToolResultJson),
            };

            history.Add(new FunctionResultContent(call, json).ToChatMessage());
            if (result.Result is LintReport)
            {
                evidence.Add(new LintEvidence(tool, json));
            }

            _telemetry.RecordToolCall(activity, label, "success", Stopwatch.GetElapsedTime(started));
            return new ToolCallRecord(tool, Succeeded: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The model gets the error as the tool result so it can explain or try something else.
            var error = ex.Message;
            history.Add(new FunctionResultContent(call, $"Error: {error}").ToChatMessage());
            _telemetry.RecordToolCall(activity, label, ex is ToolNotAllowedException ? "blocked" : "error", Stopwatch.GetElapsedTime(started), error);
            LogToolFailed(tool, error);
            return new ToolCallRecord(tool, Succeeded: false, error);
        }
    }

    /// <summary>
    /// The model asked for more tools after its last allowed round. Every call still needs a result in the history,
    /// or the next request would be rejected, so each one is answered with a "not executed" result.
    /// </summary>
    private AgentReply EndAtRoundLimit(ChatSession session, IReadOnlyCollection<FunctionCallContent> calls, List<ToolCallRecord> toolCalls)
    {
        foreach (var call in calls)
        {
            session.History.Add(new FunctionResultContent(call, RoundLimitMessage).ToChatMessage());
        }

        LogRoundLimit(session.Id, _options.MaxToolRounds);
        var reply = $"I stopped after {_options.MaxToolRounds} rounds of tool calls without reaching an answer. Try a narrower question.";
        session.History.AddAssistantMessage(reply);
        return new AgentReply(session.Id, reply, toolCalls);
    }

    private static ChatMessageContent AssistantMessage(string text, IEnumerable<FunctionCallContent> calls)
    {
        var items = new ChatMessageContentItemCollection();
        if (text.Length > 0)
        {
            items.Add(new TextContent(text));
        }

        foreach (var call in calls)
        {
            items.Add(call);
        }

        return new ChatMessageContent(AuthorRole.Assistant, items);
    }

    private static string ToolName(FunctionCallContent call) =>
        call.PluginName is null ? call.FunctionName : $"{call.PluginName}.{call.FunctionName}";

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Session {SessionId}: trimmed {Removed} messages to fit the token budget")]
    private partial void LogTrimmed(string sessionId, int removed);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Tool {Tool} failed; the error was returned to the model: {Error}")]
    private partial void LogToolFailed(string tool, string error);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Session {SessionId}: stopped after {Rounds} tool rounds")]
    private partial void LogRoundLimit(string sessionId, int rounds);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "Session {SessionId}: answer review {Verdict} with {Issues} issue(s)")]
    private partial void LogReviewed(string sessionId, string verdict, int issues);
}
