namespace Agent.Persistence;

/// <summary>Where chat sessions are kept. Bound from the <c>Sessions</c> configuration section.</summary>
public sealed class SessionStorageOptions
{
    public const string SectionName = "Sessions";

    /// <summary><see cref="SessionStorageProvider.Memory"/> (lost on restart) or <see cref="SessionStorageProvider.Sqlite"/>.</summary>
    public SessionStorageProvider Provider { get; set; } = SessionStorageProvider.Memory;

    /// <summary>SQLite connection string, used when <see cref="Provider"/> is <see cref="SessionStorageProvider.Sqlite"/>.</summary>
    public string ConnectionString { get; set; } = "Data Source=sessions.db";
}

public enum SessionStorageProvider
{
    Memory,
    Sqlite,
}
