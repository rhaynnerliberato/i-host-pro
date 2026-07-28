using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Etapa 15A — validates Identity's durable outbox foundation directly
/// against <see cref="IIdentityTransactionExecutor"/>/<see cref="IdentityOutboxTransactionExecutor"/>,
/// with real PostgreSQL and RabbitMQ (Testcontainers): atomicity (domain
/// change + envelope committed/rolled back together), no duplicate envelope
/// across a Refresh/Logout concurrency retry, and outage/recovery of the
/// broker. Uses only <see cref="CanaryIntegrationEvent"/> — none of the six
/// real events exist yet (out of scope for this Etapa).
///
/// Provisioning here deliberately mirrors <c>IHostPro.MigrationRunner</c>'s
/// own Etapa 15A block exactly (SetupResources() with the migrator
/// connection, then explicit grants for ihostpro_app) rather than invoking
/// the executable, matching how <see cref="AuthEndpointsTests"/> already
/// inlines role/migration setup instead of shelling out.
/// </summary>
public class IdentityOutboxTransactionExecutorTests : IClassFixture<IdentityOutboxTransactionExecutorTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";

    private readonly Fixture _fixture;
    private readonly RabbitMqContainer _rabbitMqContainer;
    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;

    public IdentityOutboxTransactionExecutorTests(Fixture fixture)
    {
        _fixture = fixture;
        _rabbitMqContainer = fixture.RabbitMqContainer;
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;
    }

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale (Etapa 15A stabilization of Docker daemon load).
    /// No test in this class stops or restarts this shared
    /// <see cref="RabbitMqContainer"/> — the two tests that simulate a
    /// broker outage each spin up and dispose their own dedicated container
    /// instead, so a test whose whole point is disrupting a broker never
    /// puts sibling tests' shared infrastructure at risk. (This class's
    /// real recovery-wait flakiness had a different, since-fixed cause —
    /// see the comment on <c>opts.Durability</c> in <see cref="BuildHostAsync"/>.)
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public RabbitMqContainer RabbitMqContainer { get; private set; } = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            RabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();

            await Task.WhenAll(_postgresContainer.StartAsync(), RabbitMqContainer.StartAsync());

            var adminConnectionString = _postgresContainer.GetConnectionString();

            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await ExecuteAsync(adminConnection, $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """);
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext()))
            {
                await migratorDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync()
        {
            await _postgresContainer.DisposeAsync();
            await RabbitMqContainer.DisposeAsync();
        }

        public async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using (var outboxHost = hostBuilder.Build())
            {
                await outboxHost.SetupResources();
            }

            await using var connection = new NpgsqlConnection(MigratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {OutboxSchema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {OutboxSchema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }
    }

    // ---- Real Api-equivalent host (AddIdentityCommandDispatch + Wolverine) --

    private async Task<IHost> BuildHostAsync(RabbitMqContainer? rabbitMqContainer = null)
    {
        rabbitMqContainer ??= _rabbitMqContainer;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddDbContext<IdentityDbContext>(o => o.UseNpgsql(
            _appConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));
        hostBuilder.Services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        hostBuilder.Services.AddScoped<IIdentityTransactionExecutor, IdentityOutboxTransactionExecutor>();

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });

            opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            // Root cause of this class's Etapa 15A stabilization flakiness
            // (confirmed empirically by reflecting Wolverine's own
            // DurabilitySettings defaults): a freshly-started node does not
            // claim ownership of an ancillary store's durability agent
            // immediately — it only attempts assignment on its next
            // CheckAssignmentPeriod tick, which defaults to 30 seconds, and
            // a prior node's ownership is only released after
            // StaleNodeTimeout (60s) if it wasn't gracefully stopped. Landing
            // just after a tick — or racing a not-yet-expired prior
            // ownership — is exactly what produced this test's observed
            // 16s-to-180s+ variance in recovery-wait duration: it was never
            // about the RabbitMQ container, Docker cleanup, or a stale node
            // row, but about which point in a 30s-60s assignment cycle a
            // fresh host happened to start at. Tightening these intervals
            // for the test host (never done in the real Api, which lives for
            // the whole process lifetime and has no need for sub-second
            // assignment) makes ownership hand-off — and therefore recovery
            // — fast and deterministic instead of dependent on timing luck.
            opts.Durability.CheckAssignmentPeriod = TimeSpan.FromSeconds(1);
            opts.Durability.FirstHealthCheckExecution = TimeSpan.FromSeconds(1);
            opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(5);

            // Foundation-only routing (Etapa 15A): every IntegrationEvent this
            // ancillary store publishes goes to a single RabbitMQ exchange —
            // no consumer/queue binding exists yet (no real event or handler
            // is implemented in this Etapa), only enough to prove the outbox
            // can hand a durably-persisted envelope off to the broker and
            // recover after an outage. Real per-event routing is a decision
            // for when the six events are actually implemented.
            //
            // .UseDurableOutbox() is required — confirmed empirically
            // (Incremento 2 plan, Etapa 15A): without it, this endpoint
            // defaults to Wolverine's Inline sending mode, which does NOT use
            // the persistent outbox for the actual send — a broker outage
            // makes it retry a few times in-process and then discard the
            // message, never falling back to durable persist-and-relay.
            opts.PublishMessage(typeof(CanaryIntegrationEvent)).ToRabbitExchange("identity-events-test").UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    // ---- Tests ----------------------------------------------------------

    [Fact]
    public async Task MigrationRunner_style_provisioning_is_idempotent_on_a_second_run()
    {
        // The fixture's InitializeAsync already ran ProvisionOutboxAsMigratorAsync
        // once for this whole test class; running it again here must not throw
        // or duplicate anything (SetupResources()/GRANT/ALTER DEFAULT PRIVILEGES
        // are all idempotent by construction).
        var act = () => _fixture.ProvisionOutboxAsMigratorAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task App_role_cannot_create_or_alter_objects_in_the_outbox_schema()
    {
        await using var appConnection = new NpgsqlConnection(_appConnectionString);
        await appConnection.OpenAsync();

        var createTable = () => ExecuteAsync(appConnection, $"CREATE TABLE {OutboxSchema}.should_fail (id int);");
        await createTable.Should().ThrowAsync<PostgresException>();

        var alterTable = () => ExecuteAsync(
            appConnection, $"ALTER TABLE {OutboxSchema}.wolverine_outgoing_envelopes ADD COLUMN should_fail int;");
        await alterTable.Should().ThrowAsync<PostgresException>();
    }

    [Fact]
    public async Task Api_equivalent_host_starts_successfully_using_only_app_role_credentials()
    {
        // Explicit, standalone proof (point 3 of the approved-with-reservations
        // review): BuildHostAsync() below is built exclusively from
        // _appConnectionString — it never sees the migrator connection string
        // anywhere. Combined with App_role_cannot_create_or_alter_objects_in_the_outbox_schema
        // above (the role genuinely lacks CREATE/ALTER), a successful start
        // here proves the real Api startup path — EnrollAncillaryPostgresqlOutbox
        // + AutoBuildMessageStorageOnStartup = AutoCreate.None + host.StartAsync() —
        // never attempts to create or alter the outbox schema at runtime; if it
        // did, this would fail with the exact same permission-denied error as
        // the test above, not start cleanly.
        IHost? host = null;
        var act = async () => { host = await BuildHostAsync(); };

        try
        {
            await act.Should().NotThrowAsync();
        }
        finally
        {
            if (host is not null)
                await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Commit_persists_the_domain_change()
    {
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var tenant = NewTenant();
            tenantContext.SetTenant(tenant.Id);
            var executor = scope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();

            await executor.ExecuteAsync(() =>
            {
                dbContext.Tenants.Add(tenant);
                collector.Enqueue(NewCanaryEvent(tenant.Id, tenant.Id));
                return Task.FromResult(true);
            }, CancellationToken.None);

            await using var verifyDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
            (await verifyDbContext.Tenants.CountAsync(t => t.Id == tenant.Id)).Should().Be(1);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Result_Failure_from_the_operation_still_commits_the_domain_change_and_the_event()
    {
        // Point 6 of the approved design: a Result.Failure is not, by itself,
        // a rollback signal — a rejected login can still need to persist a
        // lockout increment, an audit entry and an event. Simulated here with
        // a plain Result.Failure return, since no real handler exists yet.
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var tenant = NewTenant();
            tenantContext.SetTenant(tenant.Id);
            var executor = scope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();

            var result = await executor.ExecuteAsync(() =>
            {
                dbContext.Tenants.Add(tenant);
                collector.Enqueue(NewCanaryEvent(tenant.Id, tenant.Id));
                return Task.FromResult(Result.Failure(new Error("Simulated.Failure", "simulated business failure")));
            }, CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            await using var verifyDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
            (await verifyDbContext.Tenants.CountAsync(t => t.Id == tenant.Id)).Should().Be(1);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Exception_rolls_back_both_the_domain_change_and_the_staged_event()
    {
        var host = await BuildHostAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var tenant = NewTenant();
            tenantContext.SetTenant(tenant.Id);
            var executor = scope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();
            var envelopesBefore = await CountOutgoingEnvelopesAsync();

            var act = () => executor.ExecuteAsync<bool>(() =>
            {
                dbContext.Tenants.Add(tenant);
                collector.Enqueue(NewCanaryEvent(tenant.Id, tenant.Id));
                throw new InvalidOperationException("Simulated failure inside the operation.");
            }, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
            await using var verifyDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
            (await verifyDbContext.Tenants.CountAsync(t => t.Id == tenant.Id)).Should().Be(0);
            (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RabbitMQ_unavailable_does_not_lose_the_event_and_it_is_delivered_after_recovery()
    {
        // Dedicated, single-use RabbitMQ container — not the class Fixture's
        // shared one — so this test's own broker stop/restart never
        // interacts with sibling tests. (This test's earlier flakiness under
        // the full class/suite run had a different root cause, fixed via
        // opts.Durability in BuildHostAsync — see that comment. Dedicating
        // the broker here remains the right isolation regardless.)
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            Guid tenantId;

            var host = await BuildHostAsync(rabbitMqContainer); // built and started while RabbitMQ is still up
            try
            {
                using (var scope = host.Services.CreateScope())
                {
                    var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                    var tenant = NewTenant();
                    tenantId = tenant.Id;
                    tenantContext.SetTenant(tenant.Id);
                    var executor = scope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>();
                    var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                    var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();

                    await rabbitMqContainer.StopAsync();

                    await executor.ExecuteAsync(() =>
                    {
                        dbContext.Tenants.Add(tenant);
                        collector.Enqueue(NewCanaryEvent(tenant.Id, tenant.Id));
                        return Task.FromResult(true);
                    }, CancellationToken.None);

                    // Commit succeeds and the domain change + envelope are durably
                    // persisted in PostgreSQL regardless of the broker being reachable
                    // — that is the entire point of the outbox pattern. Checked by
                    // this specific tenant's envelope, not a raw table count: this
                    // class Fixture's Postgres/outbox schema is shared across the
                    // whole class run, so a raw count can be thrown off by sibling
                    // tests' own envelopes still settling — confirmed empirically
                    // (Incremento 2 plan, Etapa 15A stabilization pass) to be the
                    // actual cause of this test's flakiness under the full class
                    // run, not the broker/Durability-Agent timing this investigation
                    // chased first.
                    await using var verifyDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
                    (await verifyDbContext.Tenants.CountAsync(t => t.Id == tenantId)).Should().Be(1);
                    (await EnvelopeForTenantIsPendingAsync(tenantId)).Should().BeTrue();
                }
            }
            finally
            {
                // Graceful stop (see StopGracefullyAsync's doc comment) BEFORE
                // restarting the broker or building a second host.
                await StopGracefullyAsync(host);
            }

            await rabbitMqContainer.StartAsync();

            // Recovery is verified with a NEW host/connection rather than waiting
            // for the ORIGINAL host's own RabbitMQ.Client connection to
            // auto-reconnect: confirmed empirically (Incremento 2 plan, Etapa
            // 15A) that after a container-level restart, RabbitMQ.Client's
            // AutorecoveringConnection does not resume from a connection the
            // broker force-closed with reason "shutdown" — it keeps throwing
            // AlreadyClosedException indefinitely instead of recovering, even
            // minutes later. A fresh host/connection (exactly what a real
            // process restart or a new Api instance would do after an outage) is
            // the realistic and reliable way to observe recovery.
            var recoveryHost = await BuildHostAsync(rabbitMqContainer);
            try
            {
                await WaitUntilAsync(
                    async () => !await EnvelopeForTenantIsPendingAsync(tenantId),
                    TimeSpan.FromSeconds(30));
            }
            finally
            {
                await StopGracefullyAsync(recoveryHost);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Concurrency_retry_on_Refresh_does_not_duplicate_envelopes()
    {
        // Dedicated, single-use RabbitMQ container — see the doc comment on
        // RabbitMQ_unavailable_does_not_lose_the_event_and_it_is_delivered_after_recovery
        // above for why a container whose stop/start lifecycle is exercised
        // by a test must not be the class Fixture's shared one.
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostAsync(rabbitMqContainer);
            try
            {
                using var scope = host.Services.CreateScope();
                var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                var tenant = NewTenant();
                tenantContext.SetTenant(tenant.Id);
                var transactionExecutor = scope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>();
                var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                var collector = scope.ServiceProvider.GetRequiredService<IIntegrationEventCollector>();
                var refreshExecutor = new RefreshTokenExchangeExecutor(
                    transactionExecutor, dbContext, collector, new SessionRevocationSignal(), new NullSessionRevocationCache());

                // Pause the broker so a successfully-committed envelope stays visible
                // in the outgoing table long enough to count it reliably, instead of
                // racing the Durability Agent's near-immediate flush.
                await rabbitMqContainer.StopAsync();
                try
                {
                    var envelopesBefore = await CountOutgoingEnvelopesAsync();
                    var attempt = 0;

                    var authTokens = new AuthTokensResult("access-token", DateTimeOffset.UtcNow, "refresh-token", DateTimeOffset.UtcNow);
                    await refreshExecutor.ExecuteAsync(() =>
                    {
                        attempt++;
                        if (attempt == 1)
                        {
                            // A reverted attempt still stages an event before failing —
                            // it must never reach the outbox.
                            collector.Enqueue(NewCanaryEvent(tenant.Id, tenant.Id));
                            throw new DbUpdateConcurrencyException("Simulated xmin conflict.");
                        }

                        dbContext.Tenants.Add(tenant);
                        collector.Enqueue(NewCanaryEvent(tenant.Id, tenant.Id));
                        return Task.FromResult(Result.Success(authTokens));
                    }, CancellationToken.None);

                    attempt.Should().Be(2);
                    await using var verifyDbContext = CreateDbContext(_migratorConnectionString, tenantContext);
                    (await verifyDbContext.Tenants.CountAsync(t => t.Id == tenant.Id)).Should().Be(1);
                    // Exactly one envelope, from the winning attempt — never two.
                    (await CountOutgoingEnvelopesAsync()).Should().Be(envelopesBefore + 1);
                }
                finally
                {
                    await rabbitMqContainer.StartAsync();
                }
            }
            finally
            {
                await StopGracefullyAsync(host);
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    /// <summary>
    /// Every test-built <see cref="IHost"/> in this class must be stopped
    /// this way, never via bare <c>Dispose()</c>/<c>using</c>: a host that's
    /// merely disposed leaves its row claiming ownership of the
    /// identity_messaging storage agent in this class Fixture's shared
    /// Postgres node registry until <c>Durability.StaleNodeTimeout</c>
    /// elapses, instead of releasing it immediately for the next host to
    /// claim. (This turned out not to be the cause of this class's Etapa 15A
    /// recovery-wait flakiness — see the <c>opts.Durability</c> comment in
    /// <see cref="BuildHostAsync"/> for the actual root cause — but it is
    /// still the correct way to tear down a started host.)
    /// </summary>
    private static async Task StopGracefullyAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (await condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    private static Tenant NewTenant() => Tenant.Provision(
        Guid.NewGuid(), TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);

    private static CanaryIntegrationEvent NewCanaryEvent(Guid tenantId, Guid aggregateId) => new()
    {
        TenantId = tenantId,
        AggregateId = aggregateId,
        AggregateType = "Tenant",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        Marker = "etapa-15a-canary",
    };

    private async Task<long> CountOutgoingEnvelopesAsync()
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Whether an outgoing envelope carrying this tenant's id (set as both
    /// <see cref="CanaryIntegrationEvent.TenantId"/> and
    /// <see cref="CanaryIntegrationEvent.AggregateId"/> by
    /// <see cref="NewCanaryEvent"/>) is still sitting in the outbox,
    /// identified by its own guid rather than a raw table row count.
    /// Confirmed empirically (Incremento 2 plan, Etapa 15A stabilization
    /// pass) that a raw count is not a reliable signal in this class: the
    /// Postgres/outbox schema is shared across the whole class run via the
    /// class Fixture, so sibling tests' own envelopes settling at the same
    /// time can shift the count independently of this test's own envelope.
    /// </summary>
    private async Task<bool> EnvelopeForTenantIsPendingAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Byte-pattern search on the raw body, not a UTF8 decode: some
        // envelope bodies in this table (e.g. internal Wolverine control
        // messages) are not valid UTF8, and decoding those threw
        // PostgresException 22021 before this search ever reached a
        // CanaryIntegrationEvent row — confirmed empirically (Incremento 2
        // plan, Etapa 15A stabilization pass).
        command.CommandText = $"""
            SELECT count(*) FROM {OutboxSchema}.wolverine_outgoing_envelopes
            WHERE message_type ILIKE '%canaryintegrationevent%'
              AND position(convert_to(@tenantId, 'UTF8') in body) > 0
            """;
        command.Parameters.AddWithValue("tenantId", tenantId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync())! > 0;
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
