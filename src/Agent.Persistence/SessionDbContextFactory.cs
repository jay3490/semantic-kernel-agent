using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agent.Persistence;

/// <summary>Lets <c>dotnet ef migrations add</c> create the context without starting an application host.</summary>
internal sealed class SessionDbContextFactory : IDesignTimeDbContextFactory<SessionDbContext>
{
    public SessionDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SessionDbContext>().UseSqlite("Data Source=sessions.db").Options);
}
