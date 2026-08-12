using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> to generate migration code — never invoked at application
/// runtime. Mirrors <c>ReservationsDbContextFactory</c> exactly.
/// </summary>
public sealed class HousekeepingDbContextFactory : IDesignTimeDbContextFactory<HousekeepingDbContext>
{
    public HousekeepingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<HousekeepingDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"));

        return new HousekeepingDbContext(optionsBuilder.Options, new TenantContext());
    }
}
