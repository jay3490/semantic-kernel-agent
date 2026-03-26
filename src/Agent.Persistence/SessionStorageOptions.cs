namespace Agent.Persistence;

/// <summary>Where chat sessions are kept and for how long. Bound from the <c>Sessions</c> configuration section.</summary>
public sealed class SessionStorageOptions
{
    public const string SectionName = "Sessions";

    /// <summary><see cref="SessionStorageProvider.Memory"/> (lost on restart) or <see cref="SessionStorageProvider.Sqlite"/>.</summary>
    public SessionStorageProvider Provider { get; set; } = SessionStorageProvider.Memory;

    /// <summary>SQLite connection string, used when <see cref="Provider"/> is <see cref="SessionStorageProvider.Sqlite"/>.</summary>
    public string ConnectionString { get; set; } = "Data Source=sessions.db";

    /// <summary>Sessions inactive for longer than this are deleted. Zero turns expiry off.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often <see cref="ChatSessionCleanupService"/> looks for expired sessions.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);
}

public enum SessionStorageProvider
{
    Memory,
    Sqlite,
}
