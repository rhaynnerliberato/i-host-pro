using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> to generate migration code — never invoked at application runtime
/// (the Host registers <see cref="ConfigurationDbContext"/> through
/// <c>ConfigurationModuleExtensions.AddConfigurationModule</c> instead).
/// Mirrors the design-time factory pattern already used by every other
/// Bounded Context exactly.
/// </summary>
public sealed class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConfigurationDbContext>
{
    public ConfigurationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ConfigurationDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"));

        return new ConfigurationDbContext(optionsBuilder.Options, new TenantContext());
    }
}
