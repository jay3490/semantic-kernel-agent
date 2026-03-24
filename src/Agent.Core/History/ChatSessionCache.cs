using System.Collections.Concurrent;

namespace Agent.Core.History;

/// <summary>
/// The sessions held in process memory, bounded by a capacity. Past it, the least recently active idle session is
/// evicted; a session with a turn in progress is never evicted, so two turns of one session cannot run on two
/// copies of it.
/// </summary>
public sealed class ChatSessionCache(int capacity)
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;

    /// <summary>Ids of the cached sessions.</summary>
    public IEnumerable<string> Ids => _sessions.Keys;

    public bool TryGet(string sessionId, out ChatSession session) => _sessions.TryGetValue(sessionId, out session!);

    /// <summary>Adds <paramref name="session"/> unless one with its id is already cached, and returns the cached one.</summary>
    public ChatSession GetOrAdd(ChatSession session)
    {
        var cached = _sessions.GetOrAdd(session.Id, session);
        EvictOverflow(keep: cached.Id);
        return cached;
    }

    public bool Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>Removes idle sessions last active before <paramref name="cutoff"/> and returns their ids.</summary>
    public IReadOnlyList<string> RemoveInactive(DateTimeOffset cutoff)
    {
        var removed = new List<string>();
        foreach (var session in _sessions.Values)
        {
            if (session.LastActive < cutoff && !session.IsBusy && _sessions.TryRemove(session.Id, out _))
            {
                removed.Add(session.Id);
            }
        }

        return removed;
    }

    private void EvictOverflow(string keep)
    {
        while (_sessions.Count > capacity)
        {
            var oldest = _sessions.Values.Where(s => s.Id != keep && !s.IsBusy).MinBy(s => s.LastActive);
            if (oldest is null || !_sessions.TryRemove(oldest.Id, out _))
            {
                return;
            }
        }
    }
}
