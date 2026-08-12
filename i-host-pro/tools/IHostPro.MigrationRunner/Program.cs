using System.Reflection;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
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
    log.LogInformation("Provisioning RabbitMQ messaging topology (identity-events, property-management-events, reservation-events, configuration-events, housekeeping-events exchanges)");

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
            })
            // Fase 6, Incremento 1, Checkpoint 1: bound to Housekeeping's own
            // "housekeeping.reservation-projection" queue — same decoupled
            // pub/sub pattern as property-management-events above.
            .DeclareExchange("reservation-events", exchange =>
            {
                exchange.ExchangeType = ExchangeType.Topic;
                exchange.BindQueue("housekeeping.reservation-projection", "reservation_created");
                exchange.BindQueue("housekeeping.reservation-projection", "reservation_cancelled");
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
            // events (CleaningCreated/Assigned/Started/InspectionStarted/
            // Completed/Cancelled) — no queue bound here yet, since no other
            // Bounded Context consumes them this increment; the exchange only
            // needs to exist so IHostPro.Api's publish-side routing has a
            // real destination.
            .DeclareExchange("housekeeping-events", exchange => exchange.ExchangeType = ExchangeType.Topic);
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
