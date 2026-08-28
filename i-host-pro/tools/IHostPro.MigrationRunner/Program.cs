using System.Reflection;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
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
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;

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
        typeof(PropertyManagementDbContext).Assembly,
        typeof(ReservationsDbContext).Assembly,
        typeof(ConfigurationDbContext).Assembly,
        typeof(HousekeepingDbContext).Assembly,
        typeof(DashboardDbContext).Assembly,
        typeof(CommunicationDbContext).Assembly,
        typeof(ExternalIntegrationsDbContext).Assembly,
        typeof(GuestOperationsDbContext).Assembly,
        typeof(PaymentsDbContext).Assembly,
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

    // ADR-017 — Deployment-time Bootstrap for Event-derived Projections.
    // Runs after every module's schema migrations so every source AND
    // destination schema referenced below is guaranteed to exist. Each step
    // is a one-time, idempotent backfill of a projection whose consumer
    // could not have received historical events (RabbitMQ never replays to
    // a newly-bound queue) — deployment/data-migration concern only, never a
    // runtime dependency between Bounded Contexts. The list below is the
    // single source of truth for which bootstrap steps exist and in what
    // order they run — no discovery, no configuration, no implicit ordering.
    var housekeepingConnectionString = builder.Configuration.GetConnectionString("Housekeeping")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Housekeeping'.");

    // Dashboard's four bootstrap steps read cross-schema (Reservations,
    // Housekeeping, PropertyManagement) and write only to `dashboard` — same
    // single-physical-database exception ADR-017 §12 grants MigrationRunner,
    // reusing the ihostpro_migrator connection already used for Dashboard's
    // own schema migrations.
    var dashboardBootstrapConnectionString = builder.Configuration.GetConnectionString("Dashboard")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Dashboard'.");

    // Fase 10, Checkpoint 2 (Existing Reservation Upgrade Strategy) — Guest
    // Operations' own bootstrap step reads cross-schema (Reservations) and
    // writes only to `guest_operations` — same single-physical-database
    // exception ADR-017 §12 grants MigrationRunner, reusing the
    // ihostpro_migrator connection already used for Guest Operations' own
    // schema migrations.
    var guestOperationsBootstrapConnectionString = builder.Configuration.GetConnectionString("GuestOperations")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:GuestOperations'.");

    var projectionBootstrapSteps = new List<IProjectionBootstrapStep>
    {
        new PropertyProjectionBootstrapStep(housekeepingConnectionString),
        new DashboardReservationProjectionBootstrapStep(dashboardBootstrapConnectionString),
        new DashboardCleaningProjectionBootstrapStep(dashboardBootstrapConnectionString),
        new DashboardPropertyProjectionBootstrapStep(dashboardBootstrapConnectionString),
        new DashboardOccurrenceProjectionBootstrapStep(dashboardBootstrapConnectionString),
        new GuestStayOperationBackfillBootstrapStep(guestOperationsBootstrapConnectionString),
    };

    foreach (var step in projectionBootstrapSteps)
    {
        log.LogInformation("Running projection bootstrap step {StepName}", step.Name);
        await step.ExecuteAsync(log, CancellationToken.None);
    }

    // Wolverine's own Main message store (Fase 2, Incremento 1, Checkpoint 6
    // homologação — found and fixed during real-host startup validation):
    // IHostPro.Api registers Identity's and Property Management's outboxes as
    // MessageStoreRole.Ancillary, and Wolverine requires exactly one store
    // designated MessageStoreRole.Main whenever any Ancillary store exists —
    // with none, the real host fails to start with
    // InvalidWolverineStorageConfigurationException. platform_messaging is
    // deliberately NOT a Bounded Context: it holds only Wolverine's own
    // runtime coordination tables (node/agent registration, scheduled-job
    // locking) — no DbContext is ever .Enroll()-ed into it, so no domain
    // event/Integration Event is ever routed here. Provisioned here (as
    // ihostpro_migrator) exactly like Identity's/Property Management's own
    // outboxes below — IHostPro.Api never creates/alters it at runtime
    // (AutoBuildMessageStorageOnStartup = AutoCreate.None, same as the other
    // two).
    var platformMessagingMigratorConnectionString = builder.Configuration.GetConnectionString("Platform")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Platform'.");

    log.LogInformation("Provisioning Wolverine's Main message store (schema platform_messaging)");

    var platformMessagingHostBuilder = Host.CreateApplicationBuilder();
    platformMessagingHostBuilder.UseWolverine(opts =>
    {
        // MessageStoreRole defaults to Main — deliberately not passed
        // explicitly, mirroring IHostPro.Api's own registration of this same
        // store.
        opts.PersistMessagesWithPostgresql(platformMessagingMigratorConnectionString, "platform_messaging");
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var platformMessagingHost = platformMessagingHostBuilder.Build())
    {
        await platformMessagingHost.SetupResources();
    }

    // Same least-privilege grant shape as Identity's/Property Management's
    // outbox schemas below (Incremento 2 plan, Etapa 15A) — ihostpro_app gets
    // only SELECT/INSERT/UPDATE/DELETE on tables and USAGE/SELECT on
    // sequences, never CREATE/ALTER/DROP/TRUNCATE, never schema ownership.
    await using (var connection = new NpgsqlConnection(platformMessagingMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA platform_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA platform_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA platform_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA platform_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Wolverine's Main message store provisioned");

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

    // Property Management's own durable outbox (Fase 2, Incremento 1,
    // Checkpoint 1 plan, item 6) — mirrors Identity's provisioning above
    // exactly, in its own schema (property_management_messaging), never
    // sharing identity_messaging. No Integration Event is routed/published
    // yet (Checkpoint 1 restriction) — this only provisions the schema/
    // tables so a future checkpoint's first real event has them ready.
    var propertyManagementMigratorConnectionString = builder.Configuration.GetConnectionString("PropertyManagement")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:PropertyManagement'.");

    log.LogInformation("Provisioning Property Management's durable outbox (schema property_management_messaging)");

    var propertyManagementOutboxHostBuilder = Host.CreateApplicationBuilder();
    propertyManagementOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            propertyManagementMigratorConnectionString, "property_management_messaging", typeof(PropertyManagementDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var propertyManagementOutboxHost = propertyManagementOutboxHostBuilder.Build())
    {
        await propertyManagementOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(propertyManagementMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA property_management_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA property_management_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA property_management_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA property_management_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA property_management_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Property Management's durable outbox provisioned");

    // Reservations' own durable outbox (Fase 3, Incremento 1 plan) — mirrors
    // Identity's/Property Management's provisioning above exactly, in its
    // own schema (reservations_messaging), never sharing either.
    var reservationsMigratorConnectionString = builder.Configuration.GetConnectionString("Reservations")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Reservations'.");

    log.LogInformation("Provisioning Reservations' durable outbox (schema reservations_messaging)");

    var reservationsOutboxHostBuilder = Host.CreateApplicationBuilder();
    reservationsOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            reservationsMigratorConnectionString, "reservations_messaging", typeof(ReservationsDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var reservationsOutboxHost = reservationsOutboxHostBuilder.Build())
    {
        await reservationsOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(reservationsMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA reservations_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA reservations_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA reservations_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA reservations_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA reservations_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Reservations' durable outbox provisioned");

    // Configuration & Policy's own durable outbox (Fase 5, Incremento 1 —
    // Policy Engine Foundation, Checkpoint 1) — mirrors Identity's/Property
    // Management's/Reservations' provisioning above exactly, in its own
    // schema (configuration_messaging), never sharing any of the other
    // three. No Integration Event is routed/published yet (PolicyUpdated is
    // Checkpoint 6) — this only provisions the schema/tables so that
    // checkpoint's first real event has them ready, mirroring Property
    // Management's own Checkpoint 1 precedent.
    var configurationMigratorConnectionString = builder.Configuration.GetConnectionString("Configuration")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Configuration'.");

    log.LogInformation("Provisioning Configuration & Policy's durable outbox (schema configuration_messaging)");

    var configurationOutboxHostBuilder = Host.CreateApplicationBuilder();
    configurationOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            configurationMigratorConnectionString, "configuration_messaging", typeof(ConfigurationDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var configurationOutboxHost = configurationOutboxHostBuilder.Build())
    {
        await configurationOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(configurationMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA configuration_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA configuration_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA configuration_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA configuration_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA configuration_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Configuration & Policy's durable outbox provisioned");

    // Housekeeping's own durable outbox (Fase 6, Incremento 1, Checkpoint 1) —
    // mirrors Identity's/Property Management's/Reservations'/Configuration &
    // Policy's provisioning above exactly, in its own schema
    // (housekeeping_messaging), never sharing any of the other four.
    var housekeepingMigratorConnectionString = builder.Configuration.GetConnectionString("Housekeeping")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Housekeeping'.");

    log.LogInformation("Provisioning Housekeeping's durable outbox (schema housekeeping_messaging)");

    var housekeepingOutboxHostBuilder = Host.CreateApplicationBuilder();
    housekeepingOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            housekeepingMigratorConnectionString, "housekeeping_messaging", typeof(HousekeepingDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var housekeepingOutboxHost = housekeepingOutboxHostBuilder.Build())
    {
        await housekeepingOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(housekeepingMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA housekeeping_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA housekeeping_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA housekeeping_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA housekeeping_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA housekeeping_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Housekeeping's durable outbox provisioned");

    // Dashboard & Reporting's own durable outbox (Fase 7, Incremento 2,
    // Checkpoint 1) — mirrors every other context's provisioning above
    // exactly, in its own schema (dashboard_messaging), never sharing any
    // of the other five. Dashboard publishes no Integration Event of its
    // own this increment (Checkpoint 0 decision, §13), but still needs this
    // schema for IDbContextOutbox<DashboardDbContext> to resolve at all —
    // same requirement already found for Housekeeping/Reservations.
    var dashboardMigratorConnectionString = builder.Configuration.GetConnectionString("Dashboard")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Dashboard'.");

    log.LogInformation("Provisioning Dashboard & Reporting's durable outbox (schema dashboard_messaging)");

    var dashboardOutboxHostBuilder = Host.CreateApplicationBuilder();
    dashboardOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            dashboardMigratorConnectionString, "dashboard_messaging", typeof(DashboardDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var dashboardOutboxHost = dashboardOutboxHostBuilder.Build())
    {
        await dashboardOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(dashboardMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA dashboard_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA dashboard_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA dashboard_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA dashboard_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA dashboard_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Dashboard & Reporting's durable outbox provisioned");

    // External Integrations' own durable outbox (Fase 9, Checkpoint 2.3.3,
    // ADR-022 item 13) — mirrors every other context's provisioning above
    // exactly, in its own schema (external_integrations_messaging), never
    // shared with any of the other six. WhatsAppMessageStatusChanged
    // (routed by IHostPro.Api's own Program.cs) is its first published
    // Integration Event.
    var externalIntegrationsMigratorConnectionString = builder.Configuration.GetConnectionString("ExternalIntegrations")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:ExternalIntegrations'.");

    log.LogInformation("Provisioning External Integrations' durable outbox (schema external_integrations_messaging)");

    var externalIntegrationsOutboxHostBuilder = Host.CreateApplicationBuilder();
    externalIntegrationsOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            externalIntegrationsMigratorConnectionString, "external_integrations_messaging", typeof(ExternalIntegrationsDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var externalIntegrationsOutboxHost = externalIntegrationsOutboxHostBuilder.Build())
    {
        await externalIntegrationsOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(externalIntegrationsMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA external_integrations_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA external_integrations_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA external_integrations_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA external_integrations_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA external_integrations_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("External Integrations' durable outbox provisioned");

    // Guest Operations' own durable outbox (Fase 10, Checkpoint 1 — Guest
    // Operations Foundation) — mirrors every other context's provisioning
    // above exactly, in its own schema (guest_operations_messaging), never
    // shared with any of the other seven. GuestCheckedOut (routed below) is
    // its first published Integration Event.
    var guestOperationsMigratorConnectionString = builder.Configuration.GetConnectionString("GuestOperations")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:GuestOperations'.");

    log.LogInformation("Provisioning Guest Operations' durable outbox (schema guest_operations_messaging)");

    var guestOperationsOutboxHostBuilder = Host.CreateApplicationBuilder();
    guestOperationsOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            guestOperationsMigratorConnectionString, "guest_operations_messaging", typeof(GuestOperationsDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var guestOperationsOutboxHost = guestOperationsOutboxHostBuilder.Build())
    {
        await guestOperationsOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(guestOperationsMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA guest_operations_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA guest_operations_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA guest_operations_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA guest_operations_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA guest_operations_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Guest Operations' durable outbox provisioned");

    // Payments' own durable outbox (Fase 10, Checkpoint 5 — PIX/Payment
    // Deterministic Foundation) — mirrors every other context's provisioning
    // above exactly, in its own schema (payments_messaging), never shared
    // with any of the other eight. PixChargeCreated/PixChargeConfirmed
    // (routed below) are its published Integration Events.
    var paymentsMigratorConnectionString = builder.Configuration.GetConnectionString("Payments")
        ?? throw new InvalidOperationException("Missing connection string 'ConnectionStrings:Payments'.");

    log.LogInformation("Provisioning Payments' durable outbox (schema payments_messaging)");

    var paymentsOutboxHostBuilder = Host.CreateApplicationBuilder();
    paymentsOutboxHostBuilder.UseWolverine(opts =>
    {
        opts.EnrollAncillaryPostgresqlOutbox(
            paymentsMigratorConnectionString, "payments_messaging", typeof(PaymentsDbContext));
        opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
        opts.UseEntityFrameworkCoreTransactions();
    });

    using (var paymentsOutboxHost = paymentsOutboxHostBuilder.Build())
    {
        await paymentsOutboxHost.SetupResources();
    }

    await using (var connection = new NpgsqlConnection(paymentsMigratorConnectionString))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            GRANT USAGE ON SCHEMA payments_messaging TO ihostpro_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA payments_messaging TO ihostpro_app;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA payments_messaging TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA payments_messaging
              GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
            ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA payments_messaging
              GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
            """;
        await command.ExecuteNonQueryAsync();
    }

    log.LogInformation("Payments' durable outbox provisioned");

    // RabbitMQ messaging topology (Checkpoint 6 homologação, third production
    // defect: neither IHostPro.Api nor IHostPro.Worker ever declared the
    // topic exchanges they publish/route to — AutoProvision defaults to
    // false on WolverineOptions.UseRabbitMq and is deliberately never
    // enabled in either Host's own Program.cs; confirmed via
    // rabbitmqctl list_exchanges against the homologação broker that
    // identity-events/property-management-events genuinely did not exist).
    // Declared here through Wolverine's own public IStatefulResource
    // mechanism — RabbitMqTransportExpression.DeclareExchange(...) +
    // host.SetupResources() — the SAME public API this file already uses
    // for the three PostgreSQL message stores above; never a raw
    // RabbitMQ.Client ExchangeDeclare call. DeclareExchange's own doc
    // comment: "Declare a new exchange without impacting message routing or
    // listening" — it only ensures the exchange itself exists, matching
    // exactly what Program.cs's own ToRabbitRoutingKey(...) calls configure
    // (ExchangeType.Topic; IsDurable=true/AutoDelete=false are
    // RabbitMqExchange's own defaults, identical to what those routes
    // produce). Reuses UseIHostProRabbitMq(...) — the SAME connection wiring
    // IHostPro.Api/IHostPro.Worker use — so host/vhost/user/password/
    // timeouts can never drift between this provisioning step and the real
    // runtime connection.
    log.LogInformation("Provisioning RabbitMQ messaging topology (identity-events, property-management-events, reservation-events, configuration-events, housekeeping-events, external-integrations-events, guest-operations-events, payments-events, payments-commands exchanges)");

    var messagingTopologyHostBuilder = Host.CreateApplicationBuilder();
    messagingTopologyHostBuilder.UseWolverine(opts =>
    {
        opts.UseIHostProRabbitMq(builder.Configuration, listen: false)
            .DeclareExchange("identity-events", exchange => exchange.ExchangeType = ExchangeType.Topic)
            // Fase 6, Incremento 1, Checkpoint 1: bound to Housekeeping's own
            // "housekeeping.property-projection" queue — a NEW subscriber
            // queue on an exchange OWNED BY ANOTHER Bounded Context (Property
            // Management never needs to know Housekeeping is listening; same
            // single-provisioning-authority pattern as every other queue in
            // this platform).
            .DeclareExchange("property-management-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("housekeeping.property-projection", "property_created");
                exchange.BindQueue("housekeeping.property-projection", "property_activated");
                exchange.BindQueue("housekeeping.property-projection", "property_deactivated");
                exchange.BindQueue("housekeeping.property-projection", "property_archived");
                // Fase 7, Incremento 2 (Dashboard & Reporting Foundation),
                // Checkpoint 1: bound to Dashboard's own
                // "dashboard.property-projection" queue — same decoupled
                // pub/sub pattern, a second, independent subscriber queue on
                // this same exchange (Property Management never needs to
                // know Dashboard is listening, exactly like it never needed
                // to know about Housekeeping's own queue above).
                exchange.BindQueue("dashboard.property-projection", "property_created");
                exchange.BindQueue("dashboard.property-projection", "property_activated");
                exchange.BindQueue("dashboard.property-projection", "property_deactivated");
                exchange.BindQueue("dashboard.property-projection", "property_archived");
            })
            // Fase 6, Incremento 1, Checkpoint 1: bound to Housekeeping's own
            // "housekeeping.reservation-projection" queue — same decoupled
            // pub/sub pattern as property-management-events above.
            .DeclareExchange("reservation-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("housekeeping.reservation-projection", "reservation_created");
                exchange.BindQueue("housekeeping.reservation-projection", "reservation_cancelled");
                // Fase 7, Incremento 2 (Dashboard & Reporting Foundation),
                // Checkpoint 1: bound to Dashboard's own
                // "dashboard.reservation-projection" queue — a second,
                // independent subscriber queue on this same exchange.
                // Dashboard also needs ReservationUpdated (for current
                // CheckInAt/CheckOutAt on reschedule), which no prior
                // consumer needed — its own routing key is bound here for
                // the first time.
                exchange.BindQueue("dashboard.reservation-projection", "reservation_created");
                exchange.BindQueue("dashboard.reservation-projection", "reservation_updated");
                exchange.BindQueue("dashboard.reservation-projection", "reservation_cancelled");
                // Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): a
                // third, independent subscriber queue on this same exchange
                // — Workflow never needs Reservations to know it exists,
                // same decoupled pub/sub pattern as Housekeeping's/
                // Dashboard's own queues above. Bound to ONLY
                // reservation_created (the single trigger this Checkpoint
                // implements — approved decision, no ReservationUpdated/
                // ReservationCancelled reaction this checkpoint).
                exchange.BindQueue("workflow.reservation-created-trigger", "reservation_created");
                // Fase 10, Checkpoint 2 (Check-in/Checkout Core): a fifth,
                // independent subscriber queue on this same exchange — Guest
                // Operations reacts DIRECTLY to ReservationCreated
                // (choreography, the same registered criterion as
                // Communication's own reaction below — never through
                // Workflow Orchestration) to auto-create its own
                // GuestStayOperation. Reservations never needs to know Guest
                // Operations is listening, same decoupled pub/sub pattern as
                // every queue above. Unconditional (not gated to
                // Development): unlike Communication's own reaction below,
                // this consumer has no fake/real connector distinction —
                // auto-creating a local GuestStayOperation is always correct,
                // in every environment.
                exchange.BindQueue("guestoperations.reservation-created-trigger", "reservation_created");
                // Fase 9, Checkpoint 1 ("Comunicação e Integrações do MVP"):
                // a fourth, independent subscriber queue on this same
                // exchange — Communication reacts DIRECTLY to
                // ReservationCreated (choreography, Fase 8's own registered
                // criterion — never through Workflow Orchestration).
                // Reservations never needs to know Communication exists,
                // same decoupled pub/sub pattern as every queue above.
                //
                // Provisioned in Development ONLY (CP1 closure — corrective
                // homologation, same IsDevelopment() allowlist as
                // IHostPro.Worker's own registration gate): CP1's only
                // connector is FakeWhatsAppConnector, and IHostPro.Worker
                // never listens to this queue outside Development (see its
                // own Program.cs). Binding it here unconditionally would
                // let a real ReservationCreated, in any non-Development
                // environment, accumulate on a queue nothing consumes —
                // exactly the backlog CP2's real connector must never be
                // allowed to replay retroactively. This uses builder's own
                // native IHostEnvironment (Host.CreateApplicationBuilder,
                // the same Generic Host IHostPro.Worker uses) — no new
                // configuration abstraction. Every other exchange/queue/
                // binding on reservation-events (Housekeeping's/Dashboard's/
                // Workflow's own) remains provisioned in every environment,
                // unaffected.
                if (builder.Environment.IsDevelopment())
                {
                    exchange.BindQueue("communication.reservation-created-trigger", "reservation_created");
                }
            })
            // Fase 5, Incremento 1 (Policy Engine Foundation), Checkpoint 1:
            // declared ahead of PolicyUpdated (Checkpoint 6) — same
            // "infrastructure ready, content added later" precedent as the
            // configuration_messaging outbox schema above.
            //
            // Checkpoint 7 homologação, real defect found and fixed: the
            // "configuration.policy-updated" queue itself was never
            // declared/bound anywhere — IHostPro.Worker's Program.cs used
            // opts.Publish(...).ToRabbitTopics(...).BindTopic(...).ToQueue(...),
            // which is a SENDER-side routing rule and, confirmed against
            // Wolverine's own RabbitMQ documentation and by direct
            // observation against a real broker, never creates a queue or
            // makes any process listen to it — publish and listen are
            // separate concerns in Wolverine's RabbitMQ transport. The queue
            // must be provisioned here, the same single authority every
            // other messaging object in this platform already goes through
            // (mirrors ex.BindQueue(...) from Wolverine's own
            // object-management documentation); IHostPro.Worker now only
            // calls opts.ListenToRabbitQueue("configuration.policy-updated").
            .DeclareExchange("configuration-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("configuration.policy-updated", "policy_updated");
            })
            // Fase 6, Incremento 1, Checkpoint 1: Housekeeping's OWN published
            // events. Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1)
            // binds the first external subscriber — Reservation & Scheduling's
            // own "reservations.cleaning-schedule-projection" queue — same
            // decoupled pub/sub pattern as property-management-events/
            // reservation-events above: Housekeeping never needs to know
            // Reservations is listening.
            //
            // Checkpoint 1 closure (status-coverage gap fix): generalized
            // from the initial six-routing-key set to all ten real Cleaning
            // events except cleaning_delayed. cleaning_needs_help/
            // cleaning_needs_material were already published by Housekeeping
            // since Incremento 2A but never routed by IHostPro.Api at all — a
            // real defect fixed alongside this queue-binding generalization
            // (see Documento 07 §29.4). cleaning_in_transit/cleaning_interrupted
            // are brand-new events (Documento 07 §29.5-§29.6) for the two
            // remaining real Cleaning.Status transitions that previously
            // published nothing. cleaning_delayed remains deliberately NOT
            // bound — it changes no field the Agenda projection displays
            // (Documento 07 §29.8).
            .DeclareExchange("housekeeping-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_created");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_assigned");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_in_transit");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_started");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_inspection_started");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_completed");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_interrupted");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_needs_help");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_needs_material");
                exchange.BindQueue("reservations.cleaning-schedule-projection", "cleaning_cancelled");
                // Fase 7, Incremento 2 (Dashboard & Reporting Foundation),
                // Checkpoint 1: bound to Dashboard's own
                // "dashboard.cleaning-projection" queue — a second,
                // independent subscriber queue on this same exchange, same
                // decoupled pub/sub pattern as Reservations' own queue above
                // (Housekeeping never needs to know Dashboard is listening).
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_created");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_assigned");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_in_transit");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_started");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_inspection_started");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_completed");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_interrupted");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_needs_help");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_needs_material");
                exchange.BindQueue("dashboard.cleaning-projection", "cleaning_cancelled");
                // Fase 7, Incremento 2, Checkpoint 0/1 decision 3:
                // CleaningOccurrenceRegistered — a distinct queue from
                // dashboard.cleaning-projection (mirrors Housekeeping's own
                // convention of one queue per projection concern, even
                // though both live on this same exchange).
                exchange.BindQueue("dashboard.occurrence-projection", "cleaning_occurrence_registered");
            })
            // Fase 8, Checkpoint 1 (Workflow Orchestration — ADR-018): the
            // codebase's first cross-context COMMAND, never an Integration
            // Event — a deliberately separate, narrowly-scoped exchange
            // from every *-events exchange above, Direct (not Topic) since
            // there is exactly one sender (Workflow) and exactly one
            // destination queue (Housekeeping's own), never a fan-out
            // pattern. The queue name itself makes ownership explicit:
            // Housekeeping owns and consumes it, mirroring every other
            // queue's own "<owning context>.<purpose>" naming convention in
            // this platform.
            .DeclareExchange("workflow-orchestration-commands", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Direct;
                exchange.BindQueue("housekeeping.workflow-commands", "create_cleaning_for_reservation");
                // Fase 10, Checkpoint 1 (Guest Operations Foundation): a
                // second cross-context command on this SAME exchange (never
                // a second exchange) — Reservations owns and consumes
                // "reservations.workflow-commands", mirroring
                // Housekeeping's own queue-naming convention exactly.
                exchange.BindQueue("reservations.workflow-commands", "close_reservation");
                // Fase 10, Checkpoint 3 (Early Check-in / Late Checkout): a
                // third and fourth cross-context command, same
                // "reservations.workflow-commands" queue (two more routing
                // keys, never a second queue) — Reservations already owns
                // and consumes this queue for close_reservation above.
                exchange.BindQueue("reservations.workflow-commands", "reschedule_for_early_check_in");
                exchange.BindQueue("reservations.workflow-commands", "reschedule_for_late_checkout");
            })
            // Fase 9, Checkpoint 2.3.3 (ADR-022 item 13/14): External
            // Integrations' OWN published event, bound to Communication's
            // "communication.whatsapp-status-projection" queue — same
            // decoupled pub/sub pattern as every exchange above (External
            // Integrations never needs to know Communication is listening).
            // Unconditional in every environment: unlike
            // "communication.reservation-created-trigger" above (gated to
            // Development because CP1's only connector is
            // FakeWhatsAppConnector), the webhook's inbound status ingestion
            // has no fake/real distinction — the signature-verified webhook
            // itself is always real. Exactly one consumer in-process
            // (Communication) — no AddStickyHandler needed (ADR-020's own
            // "single discovered handler" default), confirmed before wiring
            // IHostPro.Worker's own listener.
            .DeclareExchange("external-integrations-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("communication.whatsapp-status-projection", "whatsapp_message_status_changed");
                // Fase 9, Checkpoint 3.2 ("Airbnb Deterministic Foundation"):
                // a second, independent subscriber queue on this same
                // exchange — Reservations' own Airbnb reservation import/
                // update/cancel consumer. External Integrations never needs
                // to know Reservations is listening, same decoupled pub/sub
                // pattern as every other queue in this platform.
                // airbnb_sync_started is deliberately not bound to anything
                // — AirbnbSyncStarted has no consumer this checkpoint.
                exchange.BindQueue("reservations.airbnb-import", "airbnb_reservation_imported");
                exchange.BindQueue("reservations.airbnb-import", "airbnb_reservation_updated");
                exchange.BindQueue("reservations.airbnb-import", "airbnb_reservation_cancelled");
            })
            // Fase 10, Checkpoint 1 (Guest Operations Foundation): Guest
            // Operations' OWN published event, bound to Workflow
            // Orchestration's "workflow.guest-checked-out-trigger" queue —
            // same decoupled pub/sub pattern as every exchange above (Guest
            // Operations never needs to know Workflow is listening).
            .DeclareExchange("guest-operations-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("workflow.guest-checked-out-trigger", "guest_checked_out");
                // guest_checked_in (Fase 10, Checkpoint 2) had no consumer
                // through Checkpoint 3 — Checkpoint 4 (Portaria Notification
                // Foundation) adds Communication's own front desk consumer
                // below (Development-gated, same precedent as
                // "communication.reservation-created-trigger" above).
                // early_checkin_denied/late_checkout_denied remain
                // deliberately NOT bound to any queue — no consumer exists
                // for either — see the two events' own PublishMessage rules
                // in IHostPro.Api's Program.cs.

                // Fase 10, Checkpoint 3 (Early Check-in / Late Checkout):
                // Workflow Orchestration's third trigger consumer — same
                // decoupled pub/sub pattern as guest_checked_out above.
                exchange.BindQueue("workflow.early-checkin-approved-trigger", "early_checkin_approved");

                // Fase 10, Checkpoint 3: LateCheckoutApproved has TWO
                // independent subscriber queues on this exchange — Workflow
                // Orchestration's own reschedule orchestrator (always) and
                // Housekeeping's own reaction (gated on UpdatesCleaning,
                // ADR-020 second consumer) — Guest Operations never needs to
                // know either is listening.
                exchange.BindQueue("workflow.late-checkout-approved-trigger", "late_checkout_approved");
                exchange.BindQueue("housekeeping.late-checkout-approved-trigger", "late_checkout_approved");

                // Fase 10, Checkpoint 4 (Portaria Notification Foundation):
                // Communication's three Front Desk notification queues —
                // gated to Development ONLY, same IsDevelopment() allowlist
                // as "communication.reservation-created-trigger" above (same
                // FakeWhatsAppConnector, same "no real provider yet"
                // reasoning; IHostPro.Worker never listens to these queues
                // outside Development either — see its own Program.cs).
                // guest_checked_in gains its first-ever bound queue here.
                // early_checkin_approved/late_checkout_approved each gain
                // one more independent subscriber queue, on top of the
                // Workflow/Housekeeping ones already bound above.
                if (builder.Environment.IsDevelopment())
                {
                    exchange.BindQueue("communication.guest-checked-in-trigger", "guest_checked_in");
                    exchange.BindQueue("communication.early-checkin-approved-trigger", "early_checkin_approved");
                    exchange.BindQueue("communication.late-checkout-approved-trigger", "late_checkout_approved");

                    // Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery
                    // Corrective Implementation): guest_access_delivery_requested
                    // gains its first-ever bound queue here — same
                    // Development-only gate as every other Communication
                    // consumer on this exchange (GuestAccessDeliveryProcessor
                    // depends on FakeWhatsAppConnector).
                    exchange.BindQueue("communication.guest-access-delivery-trigger", "guest_access_delivery_requested");
                }

                // Fase 10, Checkpoint 5 (PIX/Payment Deterministic
                // Foundation): Payments' own reaction to
                // LateCheckoutPaymentRequired — a sixth, independent
                // subscriber queue on this same exchange (Guest Operations
                // never needs to know Payments is listening). Unconditional
                // (not gated to Development): PixCharge creation has no
                // fake/real provider distinction that makes it unsafe to run
                // everywhere — FakePixProvider is this checkpoint's only
                // provider, in every environment.
                exchange.BindQueue("payments.late-checkout-payment-required-trigger", "late_checkout_payment_required");
            })
            // Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation):
            // Payments' OWN published events. Bound to Guest Operations'
            // "guestoperations.pixcharge-confirmed-trigger" queue
            // (unconditional — same reasoning as the binding above) and to
            // Communication's "communication.pixcharge-created-trigger"
            // queue (Development-only, same allowlist as every other
            // Communication queue above — the delivery processor depends on
            // FakeWhatsAppConnector).
            .DeclareExchange("payments-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("guestoperations.pixcharge-confirmed-trigger", "pix_charge_confirmed");

                if (builder.Environment.IsDevelopment())
                {
                    exchange.BindQueue("communication.pixcharge-created-trigger", "pix_charge_created");
                }
            })
            // Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation):
            // Payments' inbound provider-neutral confirmation seam (mandate
            // items 30/54) — a deliberately separate, narrowly-scoped Direct
            // exchange (mirrors workflow-orchestration-commands' own
            // reasoning: exactly one destination queue, never a fan-out
            // pattern), never the Topic payments-events exchange above. This
            // checkpoint has no real PIX provider/webhook — the only
            // publisher today is the E2E test harness itself, simulating the
            // provider-neutral fact via a real Wolverine send (never a
            // test-only HTTP endpoint). The Payments-side handler is genuine
            // production code representing the seam a FUTURE
            // ExternalIntegrations webhook-normalization step is expected to
            // publish through, unchanged.
            //
            // Fase 10, Checkpoint 5.1 (Payment Failure/Expiration Evidence
            // Corrective Gate): two more routing keys on this SAME Direct
            // exchange — same provider-neutral seam, same "E2E test harness
            // is the only publisher today" reasoning as the confirmation
            // binding above. Never a new exchange — mirrors
            // workflow-orchestration-commands' own multi-routing-key
            // pattern (RescheduleReservationForEarlyCheckIn/
            // RescheduleReservationForLateCheckout already share ONE
            // exchange with two routing keys each).
            .DeclareExchange("payments-commands", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Direct;
                exchange.BindQueue("payments.confirmation-received", "pix_charge_confirmation_received");
                exchange.BindQueue("payments.failure-received", "pix_charge_failure_received");
                exchange.BindQueue("payments.expiration-received", "pix_charge_expiration_received");
            });
    });

    using (var messagingTopologyHost = messagingTopologyHostBuilder.Build())
    {
        // SetupResources() logs and swallows a per-endpoint setup failure
        // rather than throwing (confirmed by reading Wolverine's own
        // BrokerResource.Setup()) — this platform's own constitution forbids
        // reporting a silent partial success, so this explicitly verifies
        // every declared exchange with CheckAsync() (the same call
        // BrokerResource.Check() itself makes) and fails the whole process
        // if any is missing, rather than trusting SetupResources() alone.
        await messagingTopologyHost.SetupResources();

        var messagingRuntime = messagingTopologyHost.Services.GetRequiredService<IWolverineRuntime>();
        foreach (var transport in messagingRuntime.Options.Transports)
        {
            foreach (var endpoint in transport.Endpoints().OfType<IBrokerEndpoint>())
            {
                var exists = await endpoint.CheckAsync();
                if (!exists)
                {
                    throw new InvalidOperationException(
                        $"RabbitMQ messaging topology provisioning failed: endpoint '{endpoint.Uri}' does not exist after SetupResources().");
                }
            }
        }
    }

    log.LogInformation("RabbitMQ messaging topology provisioned");

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
