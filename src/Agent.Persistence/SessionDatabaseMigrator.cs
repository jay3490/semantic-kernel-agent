using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.Persistence;

/// <summary>Applies pending EF Core migrations to the session database when the host starts, before requests are served.</summary>
public sealed partial class SessionDatabaseMigrator(IDbContextFactory<SessionDbContext> contexts, ILogger<SessionDatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        LogMigrated(pending.Length, string.Join(", ", pending));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Applied {Count} session database migration(s): {Migrations}")]
    private partial void LogMigrated(int count, string migrations);
}
