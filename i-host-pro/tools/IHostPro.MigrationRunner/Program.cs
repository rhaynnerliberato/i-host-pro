using System.Reflection;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Serilog;
using Wolverine;
using Wolverine.EntityFrameworkCore;

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

    // Identity's own durable outbox (Incremento 2 plan, Etapa 15A; ADR-004;
    // Architecture Principles §11) — provisioned via Wolverine/Weasel's own
    // resource management (host.SetupResources()), never via a plain EF Core
    // migration: Wolverine's envelope/node tables are not part of
    // IdentityDbContext's EF model, so no EF migration could create them.
    // This is the ONLY place in the platform that provisions them —
    // IHostPro.Api/IHostPro.Worker both set AutoBuildMessageStorageOnStartup
    // = AutoCreate.None and never call this. A separate, throwaway Wolverine
    // host is built here (the `host` above was already built before the
    // migration loop ran) purely to reach SetupResources() — it is never
    // started/run, only used to apply outstanding schema changes and then
    // disposed.
    var identityMigratorConnectionString = builder.Configuration.GetConnectionString("Identity")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Identity'.");

    log.LogInformation("Provisioning Identity's durable outbox (schema identity_messaging)");

    var outboxHostBuilder = Host.CreateApplicationBuilder();
    outboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            identityMigratorConnectionString, "identity_messaging", typeof(IdentityDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var outboxHost = outboxHostBuilder.Build())
    {
        await outboxHost.SetupResources();
    }

    // Wolverine/Weasel owns table/sequence creation (as ihostpro_migrator, the
    // role this process always connects as) but knows nothing about
    // ihostpro_app — grant it only what it needs to read/write envelopes at
    // runtime, mirroring exactly the grant pattern InitialCreate's EF
    // migration already uses for Identity's own tables. Confirmed empirically
    // (Incremento 2 plan, Etapa 15A) that SetupResources() creates exactly 8
    // tables (wolverine_outgoing_envelopes, wolverine_incoming_envelopes,
    // wolverine_dead_letters, wolverine_nodes, wolverine_node_assignments,
    // wolverine_node_records, wolverine_control_queue,
    // wolverine_agent_restrictions), 2 sequences (backing the two `serial`
    // columns) and zero functions/views — "ALL TABLES"/"ALL SEQUENCES IN
    // SCHEMA" covers exactly that set today; ALTER DEFAULT PRIVILEGES covers
    // any table/sequence a future Wolverine version adds without requiring a
    // change here. USAGE+SELECT (not USAGE alone) on sequences because
    // Npgsql/EF-style drivers can call currval() after nextval(), not just
    // nextval() itself. GRANT/ALTER DEFAULT PRIVILEGES are idempotent by
    // construction — safe to reapply every run.
    await using (var connection = new NpgsqlConnection(identityMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA identity_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA identity_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA identity_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Identity's durable outbox provisioned");

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
