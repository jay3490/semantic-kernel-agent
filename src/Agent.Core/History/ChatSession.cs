using System.Text.RegularExpressions;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.History;

/// <summary>The chat history of one conversation, plus a gate so that turns of the same session run one at a time.</summary>
public sealed partial class ChatSession(string id, ChatHistory history, DateTimeOffset createdAt)
{
    private long _lastActiveTicks = createdAt.UtcTicks;

    public string Id { get; } = id;

    public ChatHistory History { get; } = history;

    public DateTimeOffset CreatedAt { get; } = createdAt;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>True while a turn holds <see cref="Gate"/>.</summary>
    public bool IsBusy => Gate.CurrentCount == 0;

    public DateTimeOffset LastActive => new(Interlocked.Read(ref _lastActiveTicks), TimeSpan.Zero);

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastActiveTicks, now.UtcTicks);

    /// <summary>Session ids are 1-64 characters of letters, digits, '-' and '_', so they are safe in URLs, logs and keys.</summary>
    public static bool IsValidId(string? sessionId) => sessionId is not null && SessionId().IsMatch(sessionId);

    /// <exception cref="ArgumentException">The id is not valid.</exception>
    public static void ValidateId(string sessionId)
    {
        if (!IsValidId(sessionId))
        {
            throw new ArgumentException("Session ids are 1-64 characters of letters, digits, '-' and '_'.", nameof(sessionId));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SessionId();
}
