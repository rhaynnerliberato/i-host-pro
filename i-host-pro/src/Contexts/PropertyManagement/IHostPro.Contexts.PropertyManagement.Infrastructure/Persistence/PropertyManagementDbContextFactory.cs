using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by <c>dotnet ef migrations
/// add</c> to generate migration code — never invoked at application runtime
/// (the Host registers <see cref="PropertyManagementDbContext"/> through
/// <c>PropertyManagementModuleExtensions.AddPropertyManagementModule</c>
/// instead). Mirrors <c>IdentityDbContextFactory</c> exactly.
/// </summary>
public sealed class PropertyManagementDbContextFactory : IDesignTimeDbContextFactory<PropertyManagementDbContext>
{
    public PropertyManagementDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PropertyManagementDbContext>();

        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"));

        return new PropertyManagementDbContext(optionsBuilder.Options, new TenantContext());
    }
}
