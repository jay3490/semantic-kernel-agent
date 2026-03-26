using Agent.Core.History;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Persistence;

/// <summary>
/// Deletes sessions that have been inactive for longer than <see cref="SessionStorageOptions.Ttl"/>: once at
/// startup and then every <see cref="SessionStorageOptions.CleanupInterval"/>. Works with any
/// <see cref="IChatSessionStore"/>. A failed pass is logged and retried at the next interval.
/// </summary>
public sealed partial class ChatSessionCleanupService(
    IChatSessionStore store,
    IOptions<SessionStorageOptions> options,
    TimeProvider clock,
    ILogger<ChatSessionCleanupService> logger) : BackgroundService
{
    /// <summary>Removes the sessions that have expired by now.</summary>
    /// <returns>How many were removed.</returns>
    public async Task<int> CleanUpAsync(CancellationToken cancellationToken)
    {
        var ttl = options.Value.Ttl;
        if (ttl <= TimeSpan.Zero)
        {
            return 0;
        }

        try
        {
            var removed = await store.RemoveInactiveAsync(clock.GetUtcNow() - ttl, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                LogRemoved(removed, ttl);
            }

            return removed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex);
            return 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Ttl <= TimeSpan.Zero)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.CleanupInterval, clock);
        try
        {
            do
            {
                await CleanUpAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Information, Message = "Removed {Count} chat session(s) inactive for more than {Ttl}")]
    private partial void LogRemoved(int count, TimeSpan ttl);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "Removing expired chat sessions failed; retrying at the next interval")]
    private partial void LogFailed(Exception exception);
}
