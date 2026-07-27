using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory used exclusively by `dotnet ef migrations add` to
/// generate migration code — never invoked at application runtime (the Host
/// registers <see cref="IdentityDbContext"/> through
/// <c>IdentityModuleExtensions.AddIdentityModule</c> instead, with a
/// connection string and a real, request-scoped <see cref="ITenantContext"/>).
/// The connection string here only needs to be syntactically valid for the
/// Npgsql provider to generate SQL against — no actual connection is opened
/// during migration generation.
/// </summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<IdentityDbContext>();

        // The migrations history table must live inside the module's own
        // schema, never the default `public` — kept identical to
        // IdentityModuleExtensions.AddIdentityModule and
        // IHostPro.MigrationRunner/Program.cs to avoid divergence between
        // design-time, application runtime and migration execution
        // (Architecture Principles, Section 10).
        optionsBuilder.UseNpgsql(
            "Host=localhost;Database=ihostpro;Username=design_time_only;Password=design_time_only",
            npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"));

        return new IdentityDbContext(optionsBuilder.Options, new TenantContext());
    }
}
