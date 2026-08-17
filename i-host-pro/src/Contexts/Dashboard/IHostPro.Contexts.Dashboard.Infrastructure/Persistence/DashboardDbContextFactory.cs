using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> to generate migration code — never invoked at application runtime
/// (the Host registers <see cref="DashboardDbContext"/> through
/// <c>DashboardModuleExtensions.AddDashboardModule</c> instead). Mirrors
/// <c>ReservationsDbContextFactory</c> exactly.
/// </summary>
public sealed class DashboardDbContextFactory : IDesignTimeDbContextFactory<DashboardDbContext>
{
    public DashboardDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DashboardDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"));

        return new DashboardDbContext(optionsBuilder.Options, new TenantContext());
    }
}
