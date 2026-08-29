using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> — never invoked at application runtime. Mirrors the design-time
/// factory pattern already used by every other Bounded Context exactly.
/// </summary>
public sealed class AIAgentDbContextFactory : IDesignTimeDbContextFactory<AIAgentDbContext>
{
    public AIAgentDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AIAgentDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "ai_agent"));

        return new AIAgentDbContext(optionsBuilder.Options, new TenantContext());
    }
}
