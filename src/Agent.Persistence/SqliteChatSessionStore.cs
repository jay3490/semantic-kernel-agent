using Agent.Core;
using Agent.Core.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Persistence;

/// <summary>
/// Keeps chat sessions in SQLite through EF Core, so conversations survive restarts. Recently used sessions are
/// also held in a <see cref="ChatSessionCache"/>: the agent works on the cached copy, which is what keeps turns of
/// one session serialized, and <see cref="SaveAsync"/> writes it back after each completed turn.
/// </summary>
/// <remarks>
/// A save rewrites the session's messages in one transaction. Histories are kept inside the agent's token budget,
/// so that is a few dozen rows at most. The cache is per process: with several replicas, route a session's
/// requests to one replica (or use one replica), or two replicas can overwrite each other's turns.
/// </remarks>
public sealed class SqliteChatSessionStore(
    IDbContextFactory<SessionDbContext> contexts,
    TimeProvider clock,
    IOptions<AgentOptions> options) : IChatSessionStore
{
    private readonly ChatSessionCache _cache = new(options.Value.MaxSessions);

    public async Task<ChatSession> GetOrCreateAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default)
    {
        ChatSession.ValidateId(sessionId);
        var now = clock.GetUtcNow();

        if (!_cache.TryGet(sessionId, out var session))
        {
            session = _cache.GetOrAdd(await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false) ??
                                      new ChatSession(sessionId, new ChatHistory(systemPrompt), now));
        }

        session.Touch(now);
        return session;
    }

    public async Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        session.Touch(now);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var stored = await db.Sessions.FindAsync([session.Id], cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            db.Sessions.Add(new StoredSession { Id = session.Id, CreatedAt = session.CreatedAt.UtcDateTime, LastActiveAt = now.UtcDateTime });
        }
        else
        {
            stored.LastActiveAt = now.UtcDateTime;
            await db.Messages.Where(m => m.SessionId == session.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        db.Messages.AddRange(session.History.Select((message, index) => new StoredMessage
        {
            SessionId = session.Id,
            Sequence = index,
            Role = message.Role.Label,
            Content = ChatMessageSerializer.Serialize(message),
        }));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var cached = _cache.Remove(sessionId);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await db.Sessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return cached || deleted > 0;
    }

    public async Task<int> RemoveInactiveAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        var evicted = _cache.RemoveInactive(cutoff);

        // Sessions still cached were used after the cutoff, or have a turn in progress; their rows stay.
        var keep = _cache.Ids.ToList();
        var cutoffUtc = cutoff.UtcDateTime;

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var expired = await db.Sessions
            .Where(s => s.LastActiveAt < cutoffUtc && !keep.Contains(s.Id))
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expired.Count > 0)
        {
            await db.Sessions.Where(s => expired.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return expired.Union(evicted, StringComparer.Ordinal).Count();
    }

    private async Task<ChatSession?> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stored = await db.Sessions
            .AsNoTracking()
            .Include(s => s.Messages.OrderBy(m => m.Sequence))
            .SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        var history = new ChatHistory(stored.Messages.Select(m => ChatMessageSerializer.Deserialize(m.Content)));
        var session = new ChatSession(stored.Id, history, new DateTimeOffset(DateTime.SpecifyKind(stored.CreatedAt, DateTimeKind.Utc)));
        session.Touch(new DateTimeOffset(DateTime.SpecifyKind(stored.LastActiveAt, DateTimeKind.Utc)));
        return session;
    }
}
