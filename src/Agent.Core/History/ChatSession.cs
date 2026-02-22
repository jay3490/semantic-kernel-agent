using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Core.History;

/// <summary>The chat history of one conversation, plus a gate so that turns of the same session run one at a time.</summary>
public sealed class ChatSession(string id, ChatHistory history, DateTimeOffset createdAt)
{
    public string Id { get; } = id;

    public ChatHistory History { get; } = history;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    private long _lastActiveTicks = createdAt.UtcTicks;

    public DateTimeOffset LastActive => new(Interlocked.Read(ref _lastActiveTicks), TimeSpan.Zero);

    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastActiveTicks, now.UtcTicks);
}
