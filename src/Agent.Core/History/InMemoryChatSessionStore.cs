using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.History;

/// <summary>Where chat sessions live between requests.</summary>
public interface IChatSessionStore
{
    /// <summary>Returns the session with this id, creating it with <paramref name="systemPrompt"/> if it does not exist.</summary>
    ChatSession GetOrCreate(string sessionId, string systemPrompt);

    bool Remove(string sessionId);
}

/// <summary>
/// Process-local session store. When it holds more than <c>maxSessions</c> sessions, the least recently
/// active one is evicted. Sessions are lost on restart.
/// </summary>
public sealed partial class InMemoryChatSessionStore(TimeProvider clock, int maxSessions) : IChatSessionStore
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    public ChatSession GetOrCreate(string sessionId, string systemPrompt)
    {
        if (!IsValidId(sessionId))
        {
            throw new ArgumentException("Session ids are 1-64 characters of letters, digits, '-' and '_'.", nameof(sessionId));
        }

        var now = clock.GetUtcNow();
        var session = _sessions.GetOrAdd(sessionId, id => new ChatSession(id, new ChatHistory(systemPrompt), now));
        session.Touch(now);

        while (_sessions.Count > maxSessions)
        {
            var oldest = _sessions.Values.Where(s => s.Id != sessionId).MinBy(s => s.LastActive);
            if (oldest is null || !_sessions.TryRemove(oldest.Id, out _))
            {
                break;
            }
        }

        return session;
    }

    public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public static bool IsValidId(string? sessionId) => sessionId is not null && SessionId().IsMatch(sessionId);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SessionId();
}
