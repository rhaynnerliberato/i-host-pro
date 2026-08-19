using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> — never invoked at application runtime. Mirrors the design-time
/// factory pattern already used by every other Bounded Context exactly.
/// </summary>
public sealed class ExternalIntegrationsDbContextFactory : IDesignTimeDbContextFactory<ExternalIntegrationsDbContext>
{
    public ExternalIntegrationsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "external_integrations"));

        return new ExternalIntegrationsDbContext(optionsBuilder.Options, new TenantContext());
    }
}
