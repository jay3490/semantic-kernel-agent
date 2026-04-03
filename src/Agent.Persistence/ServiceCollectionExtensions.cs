using Agent.Core.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Persistence;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Chooses the session store from the <c>Sessions</c> section. With <c>Provider: Sqlite</c> the store is
    /// <see cref="SqliteChatSessionStore"/> and pending migrations are applied at startup; otherwise the in-memory
    /// store from <c>AddDevOpsAgent</c> stays. Call before or after <c>AddDevOpsAgent</c>.
    /// Either way, <see cref="ChatSessionCleanupService"/> deletes sessions idle for longer than <c>Sessions:Ttl</c>.
    /// </summary>
    public static IServiceCollection AddChatSessionStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionStorageOptions>()
            .Bind(configuration.GetSection(SessionStorageOptions.SectionName))
            .Validate(o => o.Provider != SessionStorageProvider.Sqlite || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Sessions:ConnectionString is required for the Sqlite provider.")
            .Validate(o => o.Ttl >= TimeSpan.Zero, "Sessions:Ttl cannot be negative.")
            .Validate(o => o.CleanupInterval > TimeSpan.Zero, "Sessions:CleanupInterval must be positive.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);

        var options = configuration.GetSection(SessionStorageOptions.SectionName).Get<SessionStorageOptions>() ?? new SessionStorageOptions();
        if (options.Provider == SessionStorageProvider.Sqlite)
        {
            services.AddDbContextFactory<SessionDbContext>(db => db.UseSqlite(options.ConnectionString));
            services.Replace(ServiceDescriptor.Singleton<IChatSessionStore, SqliteChatSessionStore>());

            // Registered first so the schema exists before the first cleanup pass.
            services.AddHostedService<SessionDatabaseMigrator>();
        }

        services.AddSingleton<ChatSessionCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<ChatSessionCleanupService>());
        return services;
    }

    /// <summary>With the SQLite provider, adds a <c>session-store</c> check that the database can be opened.</summary>
    public static IHealthChecksBuilder AddSessionStoreCheck(this IHealthChecksBuilder builder, IConfiguration configuration, params string[] tags)
    {
        var options = configuration.GetSection(SessionStorageOptions.SectionName).Get<SessionStorageOptions>() ?? new SessionStorageOptions();
        if (options.Provider == SessionStorageProvider.Sqlite)
        {
            builder.AddDbContextCheck<SessionDbContext>("session-store", tags: tags);
        }

        return builder;
    }
}
