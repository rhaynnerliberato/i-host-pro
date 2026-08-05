using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> to generate migration code — never invoked at application runtime
/// (the Host registers <see cref="ReservationsDbContext"/> through
/// <c>ReservationsModuleExtensions.AddReservationsModule</c> instead).
/// Mirrors <c>PropertyManagementDbContextFactory</c> exactly.
/// </summary>
public sealed class ReservationsDbContextFactory : IDesignTimeDbContextFactory<ReservationsDbContext>
{
    public ReservationsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ReservationsDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations"));

        return new ReservationsDbContext(optionsBuilder.Options, new TenantContext());
    }
}
