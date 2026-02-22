using Agent.Core.History;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.History;

public sealed class InMemoryChatSessionStoreTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();

    [Fact]
    public void New_sessions_start_with_the_system_prompt_and_are_reused()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);

        var first = store.GetOrCreate("abc", "be brief");
        first.History.AddUserMessage("hi");
        var again = store.GetOrCreate("abc", "ignored for existing sessions");

        Assert.Same(first, again);
        Assert.Equal(AuthorRole.System, again.History[0].Role);
        Assert.Equal("be brief", again.History[0].Content);
        Assert.Equal(2, again.History.Count);
    }

    [Fact]
    public void Evicts_the_least_recently_active_session()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 2);

        store.GetOrCreate("a", "s");
        _clock.Now = _clock.Now.AddMinutes(1);
        store.GetOrCreate("b", "s");
        _clock.Now = _clock.Now.AddMinutes(1);
        store.GetOrCreate("a", "s");
        _clock.Now = _clock.Now.AddMinutes(1);
        store.GetOrCreate("c", "s");

        Assert.Equal(2, store.Count);
        Assert.True(store.Remove("a"));
        Assert.False(store.Remove("b"));
        Assert.True(store.Remove("c"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../etc")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public void Rejects_malformed_session_ids(string id)
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);

        Assert.Throws<ArgumentException>(() => store.GetOrCreate(id, "s"));
    }
}
