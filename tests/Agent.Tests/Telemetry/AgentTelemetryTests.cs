using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Agent.Core.Telemetry;
using Agent.Tests.Agent;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Telemetry;

public sealed class AgentTelemetryTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public AgentTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>The turn span of a session and every span in its trace (other tests run in parallel).</summary>
    private (Activity Turn, List<Activity> Children) TraceOf(string sessionId)
    {
        var turn = Assert.Single(_activities, a => a.OperationName == "invoke_agent devops-agent" && (string?)a.GetTagItem("session.id") == sessionId);
        return (turn, _activities.Where(a => a.TraceId == turn.TraceId && a != turn).OrderBy(a => a.StartTimeUtc).ToList());
    }

    private static ChatMessageContent AnswerWithUsage(string text, long input, long output) =>
        new(AuthorRole.Assistant, text, metadata: new Dictionary<string, object?>
        {
            ["Usage"] = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        });

    [Fact]
    public async Task A_turn_is_a_span_with_a_child_per_model_call_and_tool_call()
    {
        var harness = new AgentHarness();
        harness.Model.ThenCallTool("time", "utc_now", callId: "call-9").Then(_ => AnswerWithUsage("noon", 120, 8));

        await harness.Agent.ChatAsync("trace-1", "what time is it?");

        var (turn, children) = TraceOf("trace-1");
        Assert.Equal(ActivityStatusCode.Unset, turn.Status);
        Assert.Equal(["chat", "execute_tool time.utc_now", "chat"], children.Select(a => a.OperationName));
        Assert.All(children, child => Assert.Equal(turn.SpanId, child.ParentSpanId));

        var tool = children[1];
        Assert.Equal("execute_tool", tool.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("call-9", tool.GetTagItem("gen_ai.tool.call.id"));
        Assert.Equal("success", tool.GetTagItem("agent.tool.outcome"));
        Assert.Equal(120L, children[2].GetTagItem("gen_ai.usage.input_tokens"));
    }

    [Fact]
    public async Task Streaming_spans_keep_their_parent_across_yields()
    {
        var harness = new AgentHarness();
        harness.Model.ThenCallTool("time", "utc_now").ThenAnswer("noon");

        await foreach (var _ in harness.Agent.StreamChatAsync("trace-2", "time?"))
        {
        }

        var (turn, children) = TraceOf("trace-2");
        Assert.Equal(true, turn.GetTagItem("agent.streaming"));
        Assert.Equal(3, children.Count);
        Assert.All(children, child => Assert.Equal(turn.SpanId, child.ParentSpanId));
    }

    [Fact]
    public async Task Blocked_and_unknown_tools_are_marked_as_errors_with_bounded_names()
    {
        var harness = new AgentHarness(new Dictionary<string, string?> { ["Agent:AllowedTools:0"] = "time.*" });
        harness.Model
            .ThenCallTool("kubernetes", "lint_manifest", new { yaml = "kind: Pod" })
            .ThenCallTool("made_up", "anything")
            .ThenAnswer("done");

        await harness.Agent.ChatAsync("trace-3", "lint this");

        var tools = TraceOf("trace-3").Children.Where(a => a.OperationName.StartsWith("execute_tool", StringComparison.Ordinal)).ToList();
        Assert.Equal(["execute_tool kubernetes.lint_manifest", "execute_tool unknown"], tools.Select(a => a.OperationName));
        Assert.Equal(["blocked", "error"], tools.Select(a => (string?)a.GetTagItem("agent.tool.outcome")));
        Assert.All(tools, a => Assert.Equal(ActivityStatusCode.Error, a.Status));
    }

    [Fact]
    public async Task A_failed_turn_is_marked_as_an_error()
    {
        var harness = new AgentHarness();
        harness.Model.Then(_ => throw new HttpRequestException("model down"));

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Agent.ChatAsync("trace-4", "hello"));

        var (turn, children) = TraceOf("trace-4");
        Assert.Equal(ActivityStatusCode.Error, turn.Status);
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(children).Status);
    }

    [Fact]
    public async Task Metrics_count_tool_calls_model_calls_and_tokens()
    {
        var harness = new AgentHarness();
        harness.Model
            .Then(_ => new ChatMessageContent(AuthorRole.Assistant,
                [new FunctionCallContent("utc_now", "time", "c1")],
                metadata: new Dictionary<string, object?> { ["Usage"] = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 } }))
            .Then(_ => AnswerWithUsage("noon", 150, 5));

        var measurements = new ConcurrentQueue<(string Instrument, long Value, Dictionary<string, object?> Tags)>();
        var meterFactory = harness.Services.GetRequiredService<IMeterFactory>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Scope == meterFactory && instrument.Meter.Name == AgentTelemetry.SourceName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        await harness.Agent.ChatAsync("metrics-1", "time?");

        long Sum(string instrument, params (string Key, string Value)[] tags) =>
            measurements.Where(m => m.Instrument == instrument && tags.All(t => Equals(m.Tags.GetValueOrDefault(t.Key), t.Value))).Sum(m => m.Value);

        Assert.Equal(1, Sum("agent.tool.calls", ("tool", "time.utc_now"), ("outcome", "success")));
        Assert.Equal(2, Sum("agent.model.calls", ("operation", "chat"), ("outcome", "success")));
        Assert.Equal(250, Sum("agent.model.tokens", ("token.type", "input")));
        Assert.Equal(15, Sum("agent.model.tokens", ("token.type", "output")));
    }

    [Fact]
    public void Usage_is_read_from_openai_and_extensions_ai_metadata()
    {
        var openAi = OpenAI.Chat.OpenAIChatModelFactory.ChatTokenUsage(outputTokenCount: 7, inputTokenCount: 42, totalTokenCount: 49);

        Assert.Equal((42L, 7L), AgentTelemetry.ReadUsage(new Dictionary<string, object?> { ["Usage"] = openAi }));
        Assert.Equal((3L, 4L), AgentTelemetry.ReadUsage(new Dictionary<string, object?> { ["Usage"] = new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 } }));
        Assert.Null(AgentTelemetry.ReadUsage(new Dictionary<string, object?>()));
        Assert.Null(AgentTelemetry.ReadUsage(null));
    }
}
