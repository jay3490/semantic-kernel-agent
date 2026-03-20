using Agent.Core.History;
using Agent.Tests.Fakes;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.History;

public sealed class InMemoryChatSessionStoreTests
{
    private readonly ManualClock _clock = new();

    [Fact]
    public async Task New_sessions_start_with_the_system_prompt_and_are_reused()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);

        var first = await store.GetOrCreateAsync("abc", "be brief");
        first.History.AddUserMessage("hi");
        var again = await store.GetOrCreateAsync("abc", "ignored for existing sessions");

        Assert.Same(first, again);
        Assert.Equal(AuthorRole.System, again.History[0].Role);
        Assert.Equal("be brief", again.History[0].Content);
        Assert.Equal(2, again.History.Count);
    }

    [Fact]
    public async Task Evicts_the_least_recently_active_session()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 2);

        await store.GetOrCreateAsync("a", "s");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.GetOrCreateAsync("b", "s");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.GetOrCreateAsync("a", "s");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.GetOrCreateAsync("c", "s");

        Assert.Equal(2, store.Count);
        Assert.True(await store.RemoveAsync("a"));
        Assert.False(await store.RemoveAsync("b"));
        Assert.True(await store.RemoveAsync("c"));
    }

    [Fact]
    public async Task Never_evicts_a_session_with_a_turn_in_progress()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 1);

        var busy = await store.GetOrCreateAsync("busy", "s");
        await busy.Gate.WaitAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await store.GetOrCreateAsync("other", "s");

        Assert.Same(busy, await store.GetOrCreateAsync("busy", "s"));
        busy.Gate.Release();
    }

    [Fact]
    public async Task Removes_sessions_inactive_since_the_cutoff_but_not_busy_ones()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);
        await store.GetOrCreateAsync("old", "s");
        var busy = await store.GetOrCreateAsync("old-but-busy", "s");
        await busy.Gate.WaitAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        var recent = await store.GetOrCreateAsync("recent", "s");
        await store.SaveAsync(recent);

        var removed = await store.RemoveInactiveAsync(_clock.Now - TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(await store.RemoveAsync("old"));
        Assert.True(await store.RemoveAsync("old-but-busy"));
        Assert.True(await store.RemoveAsync("recent"));
    }

    [Fact]
    public async Task Saving_marks_the_session_active()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);
        var session = await store.GetOrCreateAsync("s1", "s");

        _clock.Advance(TimeSpan.FromMinutes(5));
        await store.SaveAsync(session);

        Assert.Equal(_clock.Now, session.LastActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../etc")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task Rejects_malformed_session_ids(string id)
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetOrCreateAsync(id, "s"));
    }
}
