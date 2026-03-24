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
    /// </summary>
    public static IServiceCollection AddChatSessionStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionStorageOptions>()
            .Bind(configuration.GetSection(SessionStorageOptions.SectionName))
            .Validate(o => o.Provider != SessionStorageProvider.Sqlite || !string.IsNullOrWhiteSpace(o.ConnectionString),
                "Sessions:ConnectionString is required for the Sqlite provider.")
            .ValidateOnStart();

        var options = configuration.GetSection(SessionStorageOptions.SectionName).Get<SessionStorageOptions>() ?? new SessionStorageOptions();
        if (options.Provider == SessionStorageProvider.Sqlite)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddDbContextFactory<SessionDbContext>(db => db.UseSqlite(options.ConnectionString));
            services.Replace(ServiceDescriptor.Singleton<IChatSessionStore, SqliteChatSessionStore>());
            services.AddHostedService<SessionDatabaseMigrator>();
        }

        return services;
    }
}
