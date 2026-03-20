using Agent.Core.History;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Tests.Agent;

public sealed class SessionLifecycleTests
{
    /// <summary>Wraps the in-memory store and records what the agent asks it to save.</summary>
    private sealed class RecordingStore : IChatSessionStore
    {
        private readonly InMemoryChatSessionStore _inner = new(TimeProvider.System, 10);

        public List<(string Id, int Messages)> Saves { get; } = [];

        public Task<ChatSession> GetOrCreateAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default) =>
            _inner.GetOrCreateAsync(sessionId, systemPrompt, cancellationToken);

        public Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
        {
            Assert.True(session.IsBusy, "A session is saved while the turn still holds it.");
            Saves.Add((session.Id, session.History.Count));
            return _inner.SaveAsync(session, cancellationToken);
        }

        public Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default) =>
            _inner.RemoveAsync(sessionId, cancellationToken);

        public Task<int> RemoveInactiveAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
            _inner.RemoveInactiveAsync(cutoff, cancellationToken);
    }

    [Fact]
    public async Task Completed_turns_are_saved_and_failed_turns_are_not()
    {
        var store = new RecordingStore();
        var harness = new AgentHarness(configure: services => services.AddSingleton<IChatSessionStore>(store));
        harness.Model
            .ThenCallTool("time", "utc_now")
            .ThenAnswer("noon")
            .Then(_ => throw new HttpRequestException("model unavailable"));

        await harness.Agent.ChatAsync("s1", "what time is it?");
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.Agent.ChatAsync("s1", "and now?"));

        // system, user, assistant tool call, tool result, assistant answer
        Assert.Equal([("s1", 5)], store.Saves);
        Assert.Equal(5, harness.History("s1").Count);
    }

    [Fact]
    public async Task Streaming_turns_are_saved_once_complete()
    {
        var store = new RecordingStore();
        var harness = new AgentHarness(configure: services => services.AddSingleton<IChatSessionStore>(store));
        harness.Model.ThenAnswer("streamed");

        await foreach (var _ in harness.Agent.StreamChatAsync("s2", "hello"))
        {
        }

        Assert.Equal([("s2", 3)], store.Saves);
    }

    [Fact]
    public async Task Reset_removes_the_session_from_the_store()
    {
        var store = new RecordingStore();
        var harness = new AgentHarness(configure: services => services.AddSingleton<IChatSessionStore>(store));
        harness.Model.ThenAnswer("hi");
        await harness.Agent.ChatAsync("s3", "hello");

        Assert.True(await harness.Agent.ResetAsync("s3"));
        Assert.False(await harness.Agent.ResetAsync("s3"));
    }
}
