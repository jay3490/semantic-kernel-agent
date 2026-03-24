using Agent.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agent.Tests.Persistence;

/// <summary>A migrated SQLite database in a temporary file, deleted on dispose.</summary>
internal sealed class SqliteDatabase : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sessions-").FullName;

    public SqliteDatabase()
    {
        using var db = Contexts.CreateDbContext();
        db.Database.Migrate();
    }

    public string ConnectionString => $"Data Source={Path.Combine(_directory, "sessions.db")}";

    public IDbContextFactory<SessionDbContext> Contexts => new Factory(ConnectionString);

    public void Dispose()
    {
        // Pooled connections keep the file open, which blocks deleting it on Windows.
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class Factory(string connectionString) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<SessionDbContext>().UseSqlite(connectionString).Options);
    }
}
