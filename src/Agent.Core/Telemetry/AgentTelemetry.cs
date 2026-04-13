using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Services;
using OpenAI.Chat;

namespace Agent.Core.Telemetry;

/// <summary>
/// Traces and metrics of the agent loop, named after the OpenTelemetry GenAI conventions where they apply.
/// Every turn is an <c>invoke_agent</c> span with a <c>chat</c> span per model call and an <c>execute_tool</c> span
/// per tool call. Metrics count tool calls by tool and outcome, model calls by operation and outcome, and tokens
/// by type when the model reports usage. Export is configured by the host (see the API's telemetry setup).
/// </summary>
public sealed class AgentTelemetry
{
    /// <summary>Name of both the <see cref="ActivitySource"/> and the meter.</summary>
    public const string SourceName = "Agent.Core";

    public const string AgentName = "devops-agent";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    private readonly Counter<long> _toolCalls;
    private readonly Histogram<double> _toolDuration;
    private readonly Counter<long> _modelCalls;
    private readonly Histogram<double> _modelDuration;
    private readonly Counter<long> _tokens;
    private readonly Counter<long> _reviews;

    public AgentTelemetry(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(SourceName);
        _toolCalls = meter.CreateCounter<long>("agent.tool.calls", "{call}", "Tool calls made by the model, by tool and outcome (success, error, blocked).");
        _toolDuration = meter.CreateHistogram<double>("agent.tool.duration", "s", "Duration of tool calls.");
        _modelCalls = meter.CreateCounter<long>("agent.model.calls", "{call}", "Chat model requests, by operation (chat, review) and outcome.");
        _modelDuration = meter.CreateHistogram<double>("agent.model.duration", "s", "Duration of chat model requests.");
        _tokens = meter.CreateCounter<long>("agent.model.tokens", "{token}", "Tokens reported by the model, by type (input, output) and operation.");
        _reviews = meter.CreateCounter<long>("agent.review.verdicts", "{review}", "Reviewer verdicts on lint answers.");
    }

    public static Activity? StartTurn(string sessionId, bool streaming)
    {
        var activity = ActivitySource.StartActivity($"invoke_agent {AgentName}", ActivityKind.Internal);
        activity?.SetTag("gen_ai.operation.name", "invoke_agent");
        activity?.SetTag("gen_ai.agent.name", AgentName);
        activity?.SetTag("session.id", sessionId);
        activity?.SetTag("agent.streaming", streaming);
        return activity;
    }

    /// <summary>Starts the span of one model request; <paramref name="operation"/> is <c>chat</c> or <c>review</c>.</summary>
    public static Activity? StartModelCall(string operation, string? modelId, ActivityContext parent = default)
    {
        var activity = ActivitySource.StartActivity(modelId is null ? "chat" : $"chat {modelId}", ActivityKind.Client, parent);
        activity?.SetTag("gen_ai.operation.name", "chat");
        activity?.SetTag("agent.model.operation", operation);
        if (modelId is not null)
        {
            activity?.SetTag("gen_ai.request.model", modelId);
        }

        return activity;
    }

    public static Activity? StartToolCall(string tool, string? callId, ActivityContext parent = default)
    {
        var activity = ActivitySource.StartActivity($"execute_tool {tool}", ActivityKind.Internal, parent);
        activity?.SetTag("gen_ai.operation.name", "execute_tool");
        activity?.SetTag("gen_ai.tool.name", tool);
        activity?.SetTag("gen_ai.tool.call.id", callId);
        return activity;
    }

    /// <param name="outcome"><c>success</c>, <c>error</c> or <c>blocked</c>.</param>
    public void RecordToolCall(Activity? activity, string tool, string outcome, TimeSpan duration, string? error = null)
    {
        var tags = new TagList { { "tool", tool }, { "outcome", outcome } };
        _toolCalls.Add(1, tags);
        _toolDuration.Record(duration.TotalSeconds, tags);

        activity?.SetTag("agent.tool.outcome", outcome);
        if (error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
        }
    }

    /// <summary>Records a finished model request and, when the response metadata carries usage, its tokens.</summary>
    public void RecordModelCall(Activity? activity, string operation, bool succeeded, TimeSpan duration, IReadOnlyDictionary<string, object?>? metadata = null, string? error = null)
    {
        var outcome = succeeded ? "success" : "error";
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        _modelCalls.Add(1, tags);
        _modelDuration.Record(duration.TotalSeconds, tags);

        if (error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error);
        }

        if (ReadUsage(metadata) is not { } usage)
        {
            return;
        }

        var (input, output) = usage;
        _tokens.Add(input, new TagList { { "token.type", "input" }, { "operation", operation } });
        _tokens.Add(output, new TagList { { "token.type", "output" }, { "operation", operation } });
        activity?.SetTag("gen_ai.usage.input_tokens", input);
        activity?.SetTag("gen_ai.usage.output_tokens", output);
    }

    public void RecordReview(string verdict) => _reviews.Add(1, new TagList { { "verdict", verdict } });

    /// <summary>The model id a chat service reports in its attributes, if any.</summary>
    public static string? ModelIdOf(IAIService service) =>
        service.Attributes.TryGetValue(AIServiceExtensions.ModelIdKey, out var id) ? id as string : null;

    /// <summary>
    /// Token usage from a response's metadata: the OpenAI connector reports <see cref="ChatTokenUsage"/> under
    /// <c>Usage</c>; services built on Microsoft.Extensions.AI report <see cref="UsageDetails"/>.
    /// </summary>
    internal static (long Input, long Output)? ReadUsage(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("Usage", out var usage))
        {
            return null;
        }

        return usage switch
        {
            ChatTokenUsage openAi => (openAi.InputTokenCount, openAi.OutputTokenCount),
            UsageDetails details => (details.InputTokenCount ?? 0, details.OutputTokenCount ?? 0),
            _ => null,
        };
    }
}
