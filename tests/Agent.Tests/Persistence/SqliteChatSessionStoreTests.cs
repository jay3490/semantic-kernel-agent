using Agent.Core;
using Agent.Persistence;
using Agent.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Agent.Tests.Persistence;

public sealed class SqliteChatSessionStoreTests : IDisposable
{
    private readonly SqliteDatabase _database = new();
    private readonly ManualClock _clock = new();

    public void Dispose() => _database.Dispose();

    /// <summary>A new store over the same database stands for the process after a restart.</summary>
    private SqliteChatSessionStore NewStore(int maxSessions = 100) =>
        new(_database.Contexts, _clock, Options.Create(new AgentOptions { MaxSessions = maxSessions }));

    private int CountRows(Func<SessionDbContext, int> count)
    {
        using var db = _database.Contexts.CreateDbContext();
        return count(db);
    }

    [Fact]
    public async Task Saved_sessions_survive_a_restart_with_tool_calls_intact()
    {
        var store = NewStore();
        var session = await store.GetOrCreateAsync("s1", "be brief");
        var call = new FunctionCallContent("utc_now", "time", "call-1");
        session.History.AddUserMessage("what time is it?");
        session.History.Add(new ChatMessageContent(AuthorRole.Assistant, [call]));
        session.History.Add(new FunctionResultContent(call, "\"12:00\"").ToChatMessage());
        session.History.AddAssistantMessage("noon");
        await store.SaveAsync(session);

        var reloaded = await NewStore().GetOrCreateAsync("s1", "ignored");

        Assert.NotSame(session, reloaded);
        Assert.Equal(
            [AuthorRole.System, AuthorRole.User, AuthorRole.Assistant, AuthorRole.Tool, AuthorRole.Assistant],
            reloaded.History.Select(m => m.Role));
        Assert.Equal("be brief", reloaded.History[0].Content);
        Assert.Equal("call-1", Assert.IsType<FunctionResultContent>(reloaded.History[3].Items[0]).CallId);
        Assert.Equal(session.CreatedAt, reloaded.CreatedAt);
    }

    [Fact]
    public async Task New_sessions_are_not_written_until_saved()
    {
        var store = NewStore();

        await store.GetOrCreateAsync("unsaved", "s");

        Assert.Equal(0, CountRows(db => db.Sessions.Count()));
        Assert.Single((await NewStore().GetOrCreateAsync("unsaved", "fresh")).History);
    }

    [Fact]
    public async Task Saving_replaces_the_stored_history()
    {
        var store = NewStore();
        var session = await store.GetOrCreateAsync("s1", "s");
        session.History.AddUserMessage("one");
        session.History.AddAssistantMessage("two");
        await store.SaveAsync(session);

        session.History.RemoveRange(1, 2);
        session.History.AddUserMessage("three");
        await store.SaveAsync(session);

        Assert.Equal(["s", "three"], (await NewStore().GetOrCreateAsync("s1", "s")).History.Select(m => m.Content));
        Assert.Equal(2, CountRows(db => db.Messages.Count()));
    }

    [Fact]
    public async Task The_same_session_object_is_returned_while_cached()
    {
        var store = NewStore();

        var first = await store.GetOrCreateAsync("s1", "s");
        var second = await store.GetOrCreateAsync("s1", "s");

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Removing_deletes_the_session_and_its_messages()
    {
        var store = NewStore();
        var session = await store.GetOrCreateAsync("s1", "s");
        session.History.AddUserMessage("hello");
        await store.SaveAsync(session);

        Assert.True(await store.RemoveAsync("s1"));
        Assert.False(await NewStore().RemoveAsync("s1"));
        Assert.Equal((0, 0), (CountRows(db => db.Sessions.Count()), CountRows(db => db.Messages.Count())));
    }

    [Fact]
    public async Task Inactive_sessions_are_removed_from_the_database_and_the_cache()
    {
        var store = NewStore();
        await store.SaveAsync(await store.GetOrCreateAsync("old", "s"));
        var busy = await store.GetOrCreateAsync("old-busy", "s");
        await store.SaveAsync(busy);
        await busy.Gate.WaitAsync();
        _clock.Advance(TimeSpan.FromHours(3));
        await store.SaveAsync(await store.GetOrCreateAsync("recent", "s"));

        var removed = await store.RemoveInactiveAsync(_clock.Now - TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.Equal(["old-busy", "recent"], CountRowsIds());
        busy.Gate.Release();
    }

    [Fact]
    public async Task Expired_rows_of_sessions_not_in_this_process_are_removed()
    {
        var other = NewStore();
        await other.SaveAsync(await other.GetOrCreateAsync("from-another-process", "s"));
        _clock.Advance(TimeSpan.FromDays(2));

        var removed = await NewStore().RemoveInactiveAsync(_clock.Now - TimeSpan.FromDays(1));

        Assert.Equal(1, removed);
        Assert.Empty(CountRowsIds());
    }

    [Fact]
    public async Task Malformed_ids_are_rejected_before_touching_the_database()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewStore().GetOrCreateAsync("../x", "s"));
    }

    [Fact]
    public void The_model_matches_the_migrations()
    {
        using var db = _database.Contexts.CreateDbContext();

        Assert.Empty(db.Database.GetPendingMigrations());

        // The latest snapshot must describe the current model, or a migration is missing.
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable)
        {
            snapshot = mutable.FinalizeModel();
        }

        snapshot = db.GetService<IModelRuntimeInitializer>().Initialize(snapshot);
        var differences = db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshot.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel());
        Assert.Empty(differences);
    }

    private List<string> CountRowsIds()
    {
        using var db = _database.Contexts.CreateDbContext();
        return db.Sessions.Select(s => s.Id).OrderBy(id => id).ToList();
    }
}
