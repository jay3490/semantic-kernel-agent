using Agent.Core.History;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.History;

public sealed class TokenBudgetHistoryTrimmerTests
{
    /// <summary>Every message costs 10 tokens, which keeps the arithmetic in the tests obvious.</summary>
    private sealed class TenPerMessage : ITokenCounter
    {
        public int Count(ChatMessageContent message) => 10;
    }

    private readonly TokenBudgetHistoryTrimmer _trimmer = new(new TenPerMessage());

    /// <summary>system, then three turns: a tool-using turn (4 messages), a plain turn (2) and the current question (1).</summary>
    private static ChatHistory Conversation()
    {
        var history = new ChatHistory("You are a DevOps assistant.");

        history.AddUserMessage("lint samples/kubernetes/bad-deployment.yaml");
        history.Add(new ChatMessageContent(AuthorRole.Assistant,
            [new FunctionCallContent("lint_manifest_file", "kubernetes", "call-1", new KernelArguments { ["path"] = "samples/kubernetes/bad-deployment.yaml" })]));
        history.Add(new FunctionResultContent("lint_manifest_file", "kubernetes", "call-1", "{\"errors\":1}").ToChatMessage());
        history.AddAssistantMessage("One privileged container.");

        history.AddUserMessage("what time is it?");
        history.AddAssistantMessage("12:00 UTC.");

        history.AddUserMessage("and the Dockerfile?");
        return history;
    }

    [Fact]
    public void Leaves_history_alone_when_it_fits()
    {
        var history = Conversation();

        Assert.Equal(0, _trimmer.Trim(history, tokenBudget: 80));
        Assert.Equal(8, history.Count);
    }

    [Fact]
    public void Drops_whole_oldest_turns_so_tool_calls_keep_their_results()
    {
        var history = Conversation();

        // 79 cannot fit all 8 messages (80); dropping the first turn removes 4 messages at once.
        var removed = _trimmer.Trim(history, tokenBudget: 79);

        Assert.Equal(4, removed);
        Assert.Equal([AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.User], history.Select(m => m.Role));
        Assert.Equal("what time is it?", history[1].Content);
        Assert.DoesNotContain(history, m => m.Role == AuthorRole.Tool);
    }

    [Fact]
    public void Never_leaves_a_tool_result_without_its_call()
    {
        for (var budget = 0; budget <= 80; budget++)
        {
            var history = Conversation();
            _trimmer.Trim(history, budget);

            for (var i = 0; i < history.Count; i++)
            {
                if (history[i].Role == AuthorRole.Tool)
                {
                    Assert.Contains(history[i - 1].Items, item => item is FunctionCallContent);
                }
            }

            Assert.Equal(AuthorRole.User, history[1].Role);
        }
    }

    [Fact]
    public void Keeps_the_system_prompt_and_latest_turn_even_when_over_budget()
    {
        var history = Conversation();

        _trimmer.Trim(history, tokenBudget: 5);

        Assert.Equal(2, history.Count);
        Assert.Equal(AuthorRole.System, history[0].Role);
        Assert.Equal("and the Dockerfile?", history[1].Content);
    }

    [Fact]
    public void Does_nothing_without_user_messages()
    {
        var history = new ChatHistory("system only");

        Assert.Equal(0, _trimmer.Trim(history, tokenBudget: 0));
        Assert.Single(history);
    }

    [Fact]
    public void Approximate_counter_charges_text_tool_calls_and_results()
    {
        var counter = new ApproximateTokenCounter();

        Assert.Equal(4 + 3, counter.Count(new ChatMessageContent(AuthorRole.User, "twelve chars")));

        var call = new ChatMessageContent(AuthorRole.Assistant,
            [new FunctionCallContent("utc_now", "time", "c1", new KernelArguments())]);
        Assert.True(counter.Count(call) > ApproximateTokenCounter.PerMessageOverhead);

        var result = new FunctionResultContent("utc_now", "time", "c1", new string('x', 400)).ToChatMessage();
        Assert.Equal(4 + 100, counter.Count(result));
    }
}
