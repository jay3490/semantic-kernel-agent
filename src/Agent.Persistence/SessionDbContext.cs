using Microsoft.EntityFrameworkCore;

namespace Agent.Persistence;

/// <summary>A chat session row. Times are UTC.</summary>
public sealed class StoredSession
{
    public required string Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime LastActiveAt { get; set; }

    public List<StoredMessage> Messages { get; set; } = [];
}

/// <summary>One message of a session's history, in order of <see cref="Sequence"/>.</summary>
public sealed class StoredMessage
{
    public long Id { get; set; }

    public required string SessionId { get; set; }

    public int Sequence { get; set; }

    /// <summary>system, user, assistant or tool; kept as a column for querying, the JSON is authoritative.</summary>
    public required string Role { get; set; }

    /// <summary>The message serialized by <see cref="ChatMessageSerializer"/>.</summary>
    public required string Content { get; set; }
}

/// <summary>EF Core model for persisted chat sessions.</summary>
public sealed class SessionDbContext(DbContextOptions<SessionDbContext> options) : DbContext(options)
{
    public DbSet<StoredSession> Sessions => Set<StoredSession>();

    public DbSet<StoredMessage> Messages => Set<StoredMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredSession>(session =>
        {
            session.ToTable("sessions");
            session.HasKey(s => s.Id);
            session.Property(s => s.Id).HasMaxLength(64);
            session.HasIndex(s => s.LastActiveAt);
            session.HasMany(s => s.Messages)
                .WithOne()
                .HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoredMessage>(message =>
        {
            message.ToTable("messages");
            message.HasKey(m => m.Id);
            message.Property(m => m.Role).HasMaxLength(16);
            message.HasIndex(m => new { m.SessionId, m.Sequence }).IsUnique();
        });
    }
}
