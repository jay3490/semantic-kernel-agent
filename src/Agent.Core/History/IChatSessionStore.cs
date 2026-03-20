namespace Agent.Core.History;

/// <summary>Where chat sessions live between requests.</summary>
/// <remarks>
/// The agent loads a session at the start of a turn, changes its history in place while the turn runs, and calls
/// <see cref="SaveAsync"/> once the turn has completed. A turn that fails is rolled back in memory and never saved.
/// </remarks>
public interface IChatSessionStore
{
    /// <summary>Returns the session with this id, creating it with <paramref name="systemPrompt"/> if it does not exist.</summary>
    /// <exception cref="ArgumentException">The id is not 1-64 letters, digits, '-' or '_'.</exception>
    Task<ChatSession> GetOrCreateAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default);

    /// <summary>Records the session's current history and activity time.</summary>
    Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default);

    /// <summary>Forgets a session. Returns false when it did not exist.</summary>
    Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Removes every session last active before <paramref name="cutoff"/>, except ones with a turn in progress.</summary>
    /// <returns>How many sessions were removed.</returns>
    Task<int> RemoveInactiveAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);
}
