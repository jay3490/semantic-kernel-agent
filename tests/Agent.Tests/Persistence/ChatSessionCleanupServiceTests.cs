using Agent.Core.History;
using Agent.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Agent.Tests.Persistence;

public sealed class ChatSessionCleanupServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 3, 14, 12, 0, 0, TimeSpan.Zero));

    private ChatSessionCleanupService Service(IChatSessionStore store, TimeSpan ttl, TimeSpan? interval = null) =>
        new(store, Options.Create(new SessionStorageOptions { Ttl = ttl, CleanupInterval = interval ?? TimeSpan.FromMinutes(10) }),
            _clock, NullLogger<ChatSessionCleanupService>.Instance);

    /// <summary>Records each cutoff it is asked to clean up to.</summary>
    private sealed class CutoffRecorder : IChatSessionStore
    {
        public List<DateTimeOffset> Cutoffs { get; } = [];

        public Exception? Failure { get; set; }

        public SemaphoreSlim Calls { get; } = new(0);

        public Task<int> RemoveInactiveAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
        {
            Cutoffs.Add(cutoff);
            Calls.Release();
            return Failure is null ? Task.FromResult(2) : Task.FromException<int>(Failure);
        }

        public Task<ChatSession> GetOrCreateAsync(string sessionId, string systemPrompt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Removes_sessions_idle_for_longer_than_the_ttl()
    {
        var store = new InMemoryChatSessionStore(_clock, maxSessions: 10);
        await store.GetOrCreateAsync("stale", "s");
        _clock.Advance(TimeSpan.FromHours(25));
        await store.GetOrCreateAsync("fresh", "s");

        var removed = await Service(store, TimeSpan.FromHours(24)).CleanUpAsync(CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.False(await store.RemoveAsync("stale"));
        Assert.True(await store.RemoveAsync("fresh"));
    }

    [Fact]
    public async Task A_zero_ttl_turns_expiry_off()
    {
        var store = new CutoffRecorder();

        Assert.Equal(0, await Service(store, TimeSpan.Zero).CleanUpAsync(CancellationToken.None));
        Assert.Empty(store.Cutoffs);
    }

    [Fact]
    public async Task Store_failures_are_logged_and_do_not_escape()
    {
        var store = new CutoffRecorder { Failure = new InvalidOperationException("database is locked") };

        Assert.Equal(0, await Service(store, TimeSpan.FromHours(1)).CleanUpAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Runs_at_startup_and_then_every_interval()
    {
        var store = new CutoffRecorder();
        var service = Service(store, TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));

        await service.StartAsync(CancellationToken.None);
        Assert.True(await store.Calls.WaitAsync(TimeSpan.FromSeconds(5)));

        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await store.Calls.WaitAsync(TimeSpan.FromSeconds(5)));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal([_clock.Start - TimeSpan.FromHours(1), _clock.Start + TimeSpan.FromMinutes(10) - TimeSpan.FromHours(1)], store.Cutoffs);
    }

    [Fact]
    public void Is_registered_as_a_hosted_service_for_both_providers()
    {
        foreach (var provider in new[] { "Memory", "Sqlite" })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Sessions:Provider"] = provider })
                .Build();
            var services = new ServiceCollection().AddLogging().AddChatSessionStorage(configuration);

            Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
            Assert.Contains(services, d => d.ServiceType == typeof(ChatSessionCleanupService));
        }
    }

    [Fact]
    public void Negative_ttl_is_rejected_at_startup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sessions:Ttl"] = "-00:01:00" })
            .Build();
        using var provider = new ServiceCollection().AddLogging().AddChatSessionStorage(configuration).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SessionStorageOptions>>().Value);
        Assert.Contains("Sessions:Ttl", error.Message);
    }
}
