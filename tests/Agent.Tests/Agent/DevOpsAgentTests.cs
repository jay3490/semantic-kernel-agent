using Agent.Core;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Agent;

public sealed class DevOpsAgentTests
{
    [Fact]
    public async Task Round_trip_runs_the_requested_tool_and_feeds_its_result_back()
    {
        var harness = new AgentHarness();
        harness.Model
            .ThenCallTool("kubernetes", "lint_manifest_file", new { path = "kubernetes/bad-deployment.yaml" })
            .Then(history =>
            {
                var toolMessage = history[^1];
                Assert.Equal(AuthorRole.Tool, toolMessage.Role);
                var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Items));
                Assert.Contains("\"ruleId\":\"K8S006\"", (string)result.Result!);
                return new ChatMessageContent(AuthorRole.Assistant, "The log-shipper container is privileged (K8S006, line 26).");
            });

        var reply = await harness.Agent.ChatAsync("s1", "Review samples/kubernetes/bad-deployment.yaml");

        Assert.Equal("The log-shipper container is privileged (K8S006, line 26).", reply.Reply);
        Assert.Equal([new ToolCallRecord("kubernetes.lint_manifest_file", Succeeded: true)], reply.ToolCalls);

        // The plugin really ran, through the kernel and therefore through the filter.
        Assert.Contains(harness.FilterLog.Entries, e => e.Message == "Tool call kubernetes.lint_manifest_file(path=kubernetes/bad-deployment.yaml)");

        Assert.Equal(
            [AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.Tool, AuthorRole.Assistant],
            harness.History("s1").Select(m => m.Role));
    }

    [Fact]
    public async Task Tools_are_offered_for_automatic_choice_but_invoked_by_the_agent()
    {
        var harness = new AgentHarness(new Dictionary<string, string?> { ["Agent:AllowedTools:0"] = "time.*" });
        harness.Model.ThenAnswer("hello");

        await harness.Agent.ChatAsync("s1", "hi");

        var behavior = harness.Model.Settings[0]!.FunctionChoiceBehavior!;
        var configuration = behavior.GetConfiguration(new FunctionChoiceBehaviorConfigurationContext(new ChatHistory())
        {
            Kernel = harness.Services.GetService(typeof(Kernel)) as Kernel,
        });

        Assert.Equal(FunctionChoice.Auto, configuration.Choice);
        Assert.False(configuration.AutoInvoke);
        Assert.Equal(["convert_time", "time_since", "utc_now"], configuration.Functions!.Select(f => f.Name).Order());
    }

    [Fact]
    public async Task Tools_outside_the_allow_list_are_refused_and_the_model_is_told()
    {
        var harness = new AgentHarness(new Dictionary<string, string?> { ["Agent:AllowedTools:0"] = "time.*" });
        harness.Model
            .ThenCallTool("kubernetes", "lint_manifest_file", new { path = "kubernetes/bad-deployment.yaml" })
            .Then(history =>
            {
                var result = Assert.IsType<FunctionResultContent>(Assert.Single(history[^1].Items));
                Assert.Equal("Error: The tool 'kubernetes.lint_manifest_file' is not on the allow-list.", result.Result);
                return new ChatMessageContent(AuthorRole.Assistant, "I am not allowed to lint manifests.");
            });

        var reply = await harness.Agent.ChatAsync("s1", "lint it");

        var call = Assert.Single(reply.ToolCalls);
        Assert.False(call.Succeeded);
        Assert.Contains(harness.FilterLog.Entries, e => e is { Level: LogLevel.Warning, Message: "Tool call kubernetes.lint_manifest_file blocked: not on the allow-list" });
        Assert.DoesNotContain(harness.FilterLog.Entries, e => e.Message.StartsWith("Tool call kubernetes.lint_manifest_file(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_errors_are_returned_to_the_model_instead_of_failing_the_turn()
    {
        var harness = new AgentHarness();
        harness.Model
            .ThenCallTool("logs", "analyze_log_file", new { path = "../../secrets.txt" })
            .ThenCallTool("kubernetes", "delete_cluster")
            .ThenAnswer("Neither request could be completed.");

        var reply = await harness.Agent.ChatAsync("s1", "do things");

        Assert.Equal(2, reply.ToolCalls.Count);
        Assert.All(reply.ToolCalls, c => Assert.False(c.Succeeded));
        Assert.Contains("outside the workspace root", reply.ToolCalls[0].Error);
        Assert.Equal("Neither request could be completed.", reply.Reply);
    }

    [Fact]
    public async Task Stops_at_the_tool_round_limit_and_leaves_no_call_unanswered()
    {
        var harness = new AgentHarness(new Dictionary<string, string?> { ["Agent:MaxToolRounds"] = "2" });
        harness.Model
            .ThenCallTool("time", "utc_now")
            .ThenCallTool("time", "utc_now")
            .ThenCallTool("time", "utc_now");

        var reply = await harness.Agent.ChatAsync("s1", "loop forever");

        Assert.StartsWith("I stopped after 2 rounds of tool calls", reply.Reply);
        Assert.Equal(2, reply.ToolCalls.Count);
        Assert.Equal(FunctionChoice.None, harness.Model.Settings[2]!.FunctionChoiceBehavior!
            .GetConfiguration(new FunctionChoiceBehaviorConfigurationContext(new ChatHistory())).Choice);

        var history = harness.History("s1");
        var callIds = history.SelectMany(m => m.Items.OfType<FunctionCallContent>()).Select(c => c.Id);
        var resultIds = history.SelectMany(m => m.Items.OfType<FunctionResultContent>()).Select(r => r.CallId);
        Assert.Equal(callIds, resultIds);
    }

    [Fact]
    public async Task History_carries_over_between_turns_of_a_session()
    {
        var harness = new AgentHarness();
        harness.Model.ThenAnswer("first answer").ThenAnswer("second answer");

        await harness.Agent.ChatAsync("s1", "first question");
        await harness.Agent.ChatAsync("s1", "second question");

        Assert.Equal(
            ["first question", "first answer", "second question"],
            harness.Model.Requests[1].Skip(1).Select(m => m.Content));
    }

    [Fact]
    public async Task A_failed_turn_is_removed_from_the_history()
    {
        var harness = new AgentHarness();
        harness.Model
            .ThenCallTool("time", "utc_now")
            .Then(_ => throw new HttpRequestException("model unavailable"))
            .ThenAnswer("recovered");

        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Agent.ChatAsync("s1", "what time is it?"));
        Assert.Single(harness.History("s1"));

        var reply = await harness.Agent.ChatAsync("s1", "try again");
        Assert.Equal("recovered", reply.Reply);
        Assert.Equal([AuthorRole.System, AuthorRole.User], harness.Model.Requests[2].Select(m => m.Role));
    }

    [Fact]
    public async Task Streaming_reports_tool_activity_then_the_text()
    {
        var harness = new AgentHarness();
        harness.Model
            .ThenCallTool("logs", "analyze_log_file", new { path = "logs/orders-api.log", top = 2 })
            .Then(history =>
            {
                var result = (string)Assert.IsType<FunctionResultContent>(Assert.Single(history[^1].Items)).Result!;
                Assert.Contains("payment gateway timeout after <n> for order <n>", result);
                return new ChatMessageContent(AuthorRole.Assistant, "The most frequent error is the payment gateway timeout (4 times).");
            });

        var events = new List<AgentStreamEvent>();
        await foreach (var e in harness.Agent.StreamChatAsync("s1", "What is failing in logs/orders-api.log?"))
        {
            events.Add(e);
        }

        Assert.Equal(new ToolCallStarted("logs.analyze_log_file"), events[0]);
        Assert.Equal(new ToolCallFinished(new ToolCallRecord("logs.analyze_log_file", true)), events[1]);

        var deltas = events.OfType<TextDelta>().Select(d => d.Text).ToArray();
        Assert.True(deltas.Length > 1);
        var completed = Assert.IsType<ReplyCompleted>(events[^1]);
        Assert.Equal(string.Concat(deltas), completed.Reply.Reply);
        Assert.Equal("The most frequent error is the payment gateway timeout (4 times).", completed.Reply.Reply);
    }

    [Fact]
    public async Task Chat_without_a_configured_model_fails_clearly()
    {
        var harness = new AgentHarness(withModel: false);

        await Assert.ThrowsAsync<ChatModelNotConfiguredException>(() => harness.Agent.ChatAsync("s1", "hi"));
    }
}
