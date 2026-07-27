using System.Reflection;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    using var host = builder.Build();

    var log = host.Services.GetRequiredService<ILogger<Program>>();

    log.LogInformation("IHostPro.MigrationRunner starting");

    // Discovers every Bounded Context's DbContext by scanning for implementations of
    // IModuleDbContext and applies its pending migrations independently, each within
    // its own PostgreSQL schema and its own migrations-history table
    // (Architecture Principles, Sections 10 and 16). This process is the ONLY place
    // that applies migrations — no DbContext ever calls Database.Migrate() at
    // application startup, avoiding races between multiple instances.
    //
    // This connects exclusively as the ihostpro_migrator role — its connection
    // string always comes from THIS process's own appsettings/environment,
    // never from IHostPro.Api/IHostPro.Worker's configuration, which only ever
    // holds the ihostpro_app credential (Incremento 1 plan, adendo final,
    // Section 7).
    var moduleAssemblies = new List<Assembly>
    {
        typeof(IdentityDbContext).Assembly,
        // typeof(ReservationsDbContext).Assembly, // added when Reservation & Scheduling exists
    };

    var moduleDbContextTypes = moduleAssemblies
        .SelectMany(assembly => assembly.GetTypes())
        .Where(type => typeof(IModuleDbContext).IsAssignableFrom(type) && !type.IsAbstract)
        .ToList();

    if (moduleDbContextTypes.Count == 0)
    {
        log.LogWarning("No Bounded Context DbContext discovered.");
    }

    foreach (var dbContextType in moduleDbContextTypes)
    {
        var connectionStringKey = dbContextType.Name.EndsWith("DbContext", StringComparison.Ordinal)
            ? dbContextType.Name[..^"DbContext".Length]
            : dbContextType.Name;

        var connectionString = builder.Configuration.GetConnectionString(connectionStringKey)
            ?? throw new InvalidOperationException(
                $"Missing connection string 'ConnectionStrings:{connectionStringKey}' for {dbContextType.Name}.");

        log.LogInformation("Applying migrations for {DbContext}", dbContextType.Name);

        // Every module DbContext inherits BaseDbContext, whose constructor
        // shape is fixed by convention: (DbContextOptions<TSelf>,
        // ITenantContext) (Architecture Principles, Sections 10/16) — this is
        // what lets this process construct any module's DbContext purely by
        // reflection, without any module-specific code. Tenant context is
        // irrelevant to schema/DDL operations, so a fresh, unresolved
        // TenantContext is sufficient.
        var optionsBuilderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(dbContextType);

        // The EF Core migrations history table must live inside the module's
        // own schema, never the default `public` (Architecture Principles,
        // Section 10 — "cada DbContext possui sua própria tabela de
        // histórico de migrations"; also the concrete fix for the
        // "permission denied for schema public" failure found during
        // Incremento 1 homologation, since ihostpro_migrator is never
        // granted CREATE on public). SchemaName is a property with no
        // dependency on a real connection, so a throwaway, unconfigured
        // instance is enough to read it before building the real options.
        var probeOptionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;
        using var probeDbContext = (DbContext)Activator.CreateInstance(dbContextType, probeOptionsBuilder.Options, new TenantContext())!;
        var schemaName = ((IModuleDbContext)probeDbContext).SchemaName;

        var optionsBuilder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsBuilderType)!;
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
            npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", schemaName));

        var dbContext = (DbContext)Activator.CreateInstance(dbContextType, optionsBuilder.Options, new TenantContext())!;

        await using (dbContext)
        {
            await dbContext.Database.MigrateAsync();
        }
    }

    log.LogInformation("IHostPro.MigrationRunner finished");
}
catch (Exception ex)
{
    Log.Fatal(ex, "IHostPro.MigrationRunner terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    Log.CloseAndFlush();
}
