using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.History;

/// <summary>
/// Process-local session store. When it holds more than <c>maxSessions</c> sessions, the least recently
/// active idle one is evicted. Sessions are lost on restart.
/// </summary>
public sealed class InMemoryChatSessionStore(TimeProvider clock, int maxSessions) : IChatSessionStore
{
    private readonly ChatSessionCache _sessions = new(maxSessions);

    public int Count => _sessions.Count;

    public Task<ChatSession> GetOrCreateAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default)
    {
        ChatSession.ValidateId(sessionId);

        var now = clock.GetUtcNow();
        if (!_sessions.TryGet(sessionId, out var session))
        {
            session = _sessions.GetOrAdd(new ChatSession(sessionId, new ChatHistory(systemPrompt), now));
        }

        session.Touch(now);
        return Task.FromResult(session);
    }

    public Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        session.Touch(clock.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.Remove(sessionId));

    public Task<int> RemoveInactiveAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) =>
        Task.FromResult(_sessions.RemoveInactive(cutoff).Count);
}
