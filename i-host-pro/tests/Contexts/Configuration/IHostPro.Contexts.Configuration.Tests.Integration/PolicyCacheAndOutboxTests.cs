using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Domain;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Caching;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace IHostPro.Contexts.Configuration.Tests.Integration;

/// <summary>
/// Real PostgreSQL + real Redis (Testcontainers) for the cache tests, plus a
/// real (deliberately stopped-before-asserting) RabbitMQ for the outbox tests
/// — Fase 5, Incremento 1, Checkpoint 6 ("cache; invalidação; outbox;
/// PolicyUpdated; ...; teste de invalidação determinístico"). Drives
/// <see cref="ICreatePolicyValueVersionExecutor"/> directly through the real
/// composition root (never a hand-rolled substitute), exactly like
/// <c>ConfigurationEndpointsTests</c>'s own outbox wiring — the difference
/// here is a real Redis behind <c>IPolicyValueCache</c>/<c>IPolicyCacheInvalidator</c>,
/// so the deterministic-invalidation property (§6: "invalidação imediata
/// depois de commit bem-sucedido") can be proven end to end, and (for the
/// outbox tests specifically) a real <c>PublishMessage(...).ToRabbitRoutingKey(...).UseDurableOutbox()</c>
/// rule for <c>PolicyUpdated</c> — mirrors <c>CondominiumIntegrationEventsTests</c>'s
/// exact technique (stop the broker before the command runs, so Wolverine's
/// near-immediate delivery attempt never races the envelope-existence
/// assertion to zero rows — confirmed empirically there and reproduced here).
///
/// The cache tests use the fixture's own single shared host (no RabbitMQ —
/// they never publish anything, only call <see cref="IPolicyCacheInvalidator.InvalidateAsync"/>
/// directly, exactly as the real <see cref="PolicyUpdatedCacheInvalidation"/>
/// would); the outbox tests build their own short-lived host with RabbitMQ
/// added, reusing the fixture's Postgres/Redis connection strings.
///
/// The real <c>PolicyUpdated</c> consumer hosted in <c>IHostPro.Worker</c>,
/// and true end-to-end delivery through a live RabbitMQ, are deliberately NOT
/// exercised here — reserved for Checkpoint 7 (mirrors the same reasoning
/// already recorded for the Playwright E2E suite in Checkpoint 5, avoiding a
/// second ad-hoc RabbitMQ port-5672 environment swap). Cache outage-tolerance
/// is covered by <c>PolicyResolutionTests</c>'s own fixture, which points at
/// a permanently unreachable Redis for its entire suite (documented there).
/// </summary>
public class PolicyCacheAndOutboxTests : IClassFixture<PolicyCacheAndOutboxTests.Fixture>
{
    private const string ConfigurationOutboxSchema = "configuration_messaging";
    private const string MainSchema = "platform_messaging";

    private readonly Fixture _fixture;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PolicyCacheAndOutboxTests(Fixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        private RedisContainer _redisContainer = null!;
        private IHost _host = null!;
        private string _migratorConnectionString = null!;
        private string _appConnectionString = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();
            _redisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();

            await Task.WhenAll(_postgresContainer.StartAsync(), _redisContainer.StartAsync());

            var adminConnectionString = _postgresContainer.GetConnectionString();
            await using (var adminConnection = new NpgsqlConnection(adminConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = $"""
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD '{MigratorRolePassword}';
                    CREATE ROLE ihostpro_app LOGIN PASSWORD '{AppRolePassword}';
                    GRANT CREATE ON DATABASE ihostpro_test TO ihostpro_migrator;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Username = "ihostpro_migrator", Password = MigratorRolePassword };
            _migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            _appConnectionString = builder.ConnectionString;

            await using (var dbContext = CreateDbContext(_migratorConnectionString))
                await dbContext.Database.MigrateAsync();

            await ProvisionOutboxAsync();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Configuration"] = _appConnectionString,
                ["Configuration:PolicyCache:ConnectionString"] = _redisContainer.GetConnectionString(),
                // A short TTL keeps this suite's own assertions honest — a
                // stale value must be proven wrong by INVALIDATION, not by
                // outliving a long default while the test happens to run fast.
                ["Configuration:PolicyCache:TimeToLive"] = "00:05:00",
            }).Build();

            var appBuilder = Host.CreateApplicationBuilder();
            appBuilder.Services.AddScoped<ITenantContext, TenantContext>();
            appBuilder.Services.AddConfigurationModule(configuration);
            appBuilder.Services.AddConfigurationCommandDispatch();
            appBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, ConfigurationOutboxSchema, typeof(ConfigurationDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            _host = appBuilder.Build();
            await _host.StartAsync();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await Task.WhenAll(_postgresContainer.DisposeAsync().AsTask(), _redisContainer.DisposeAsync().AsTask());
        }

        public AsyncServiceScope CreateScope() => _host.Services.CreateAsyncScope();

        public string AppConnectionString => _appConnectionString;
        public string MigratorConnectionString => _migratorConnectionString;
        public string RedisConnectionString => _redisContainer.GetConnectionString();

        private async Task ProvisionOutboxAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(_migratorConnectionString, MainSchema);
                opts.EnrollAncillaryPostgresqlOutbox(_migratorConnectionString, ConfigurationOutboxSchema, typeof(ConfigurationDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var provisioningHost = hostBuilder.Build();
            await provisioningHost.SetupResources();

            // Both schemas: the Main store (platform_messaging — needed for
            // Wolverine's own node/agent bookkeeping at startup, even though
            // this suite never enrolls a second Ancillary store alongside it)
            // and the Ancillary outbox (configuration_messaging) — mirrors
            // ConfigurationEndpointsTests.Fixture's own two separate
            // GrantSchemaAsync calls exactly.
            await GrantSchemaAsync(MainSchema);
            await GrantSchemaAsync(ConfigurationOutboxSchema);
        }

        private async Task GrantSchemaAsync(string schema)
        {
            await using var connection = new NpgsqlConnection(_migratorConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                GRANT USAGE ON SCHEMA {schema} TO ihostpro_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO ihostpro_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;
                ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA {schema}
                  GRANT USAGE, SELECT ON SEQUENCES TO ihostpro_app;
                """;
            await command.ExecuteNonQueryAsync();
        }

        private static ConfigurationDbContext CreateDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
                .Options;
            return new ConfigurationDbContext(options, new TenantContext());
        }
    }

    // ---- Deterministic invalidation ----

    [Fact]
    public async Task A_stale_cached_resolution_is_only_corrected_after_invalidation_never_on_its_own()
    {
        var tenantId = Guid.NewGuid();

        var beforeCreate = await ResolveAsync(tenantId);
        beforeCreate.Status.Should().Be(PolicyReadStatus.NotConfigured);

        // A direct DB write (bypassing CreatePolicyValueVersionExecutor entirely) — never
        // CreateEarlyCheckInVersionAsync, which now also invalidates the cache synchronously as
        // part of the same write (Checkpoint 7 homologação, real defect found and fixed: the
        // command-handler write path used to leave callers racing the async
        // outbox→RabbitMQ→IHostPro.Worker invalidation; see CreatePolicyValueVersionExecutor's own
        // doc comment). Mirrors Invalidating_a_tenant_scoped_policy_also_invalidates_a_differently_scoped_Property_cache_entry's
        // own established technique for exactly the same reason: this test proves the CACHE
        // mechanism itself never self-corrects without an explicit invalidation call, independent
        // of whichever write path a real caller happens to go through.
        await using (var dbContext = CreateAppDbContext(tenantId))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
            var value = PolicyValue.CreateInitialVersion(
                Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
                """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""",
                DateTimeOffset.UtcNow, Guid.NewGuid(), "direct DB write for the test");
            dbContext.PolicyValues.Add(value);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var beforeInvalidation = await ResolveAsync(tenantId);
        beforeInvalidation.Status.Should().Be(
            PolicyReadStatus.NotConfigured,
            "the first resolution's NotConfigured answer must still be served from cache until something invalidates it — this is the baseline that makes the next assertion meaningful");

        await using (var scope = _fixture.CreateScope())
        {
            var invalidator = scope.ServiceProvider.GetRequiredService<IPolicyCacheInvalidator>();
            await invalidator.InvalidateAsync(tenantId, "EARLY_CHECKIN", CancellationToken.None);
        }

        var afterInvalidation = await ResolveAsync(tenantId);
        afterInvalidation.Status.Should().Be(PolicyReadStatus.Resolved);
        afterInvalidation.Value!.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Invalidating_a_tenant_scoped_policy_also_invalidates_a_differently_scoped_Property_cache_entry()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await CreateEarlyCheckInVersionAsync(tenantId, expectedVersion: null, allowed: false);

        // Cache the Property-scope resolution too — it currently inherits the Tenant value (false), since no Property-level override exists.
        var beforePropertyResolution = await ResolveAsync(tenantId, propertyId);
        beforePropertyResolution.Value!.Allowed.Should().BeFalse();

        // Change the Tenant value directly (bypassing the app, mirroring PolicyResolutionTests' own precedent) — the cached Property-scope entry above must still be stale until invalidated.
        await using (var dbContext = CreateAppDbContext(tenantId))
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");
            var current = await dbContext.PolicyValues.FirstAsync(v => v.TenantId == tenantId && v.IsCurrent);
            current.Supersede();
            var next = PolicyValue.CreateNextVersion(
                Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), PolicyVersion.Create(current.Version),
                """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""",
                DateTimeOffset.UtcNow, Guid.NewGuid(), "direct DB change for the test");
            dbContext.PolicyValues.Add(next);
            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var stillStale = await ResolveAsync(tenantId, propertyId);
        stillStale.Value!.Allowed.Should().BeFalse("the Property-scope cache entry inherited the old Tenant value and nothing invalidated it yet");

        await using (var scope = _fixture.CreateScope())
        {
            var invalidator = scope.ServiceProvider.GetRequiredService<IPolicyCacheInvalidator>();
            await invalidator.InvalidateAsync(tenantId, "EARLY_CHECKIN", CancellationToken.None);
        }

        var afterInvalidation = await ResolveAsync(tenantId, propertyId);
        afterInvalidation.Value!.Allowed.Should().BeTrue(
            "invalidation is deliberately per (tenantId, policyCode), not per exact scope — a Tenant-level change can affect every Property that inherits it, and those cache entries cannot be enumerated individually");
    }

    // ---- Outbox ----

    private const string ConfigurationEventsExchange = "configuration-events-test";
    private const string PolicyUpdatedMessageType = "IHostPro.Contexts.Configuration.Contracts.PolicyUpdated";

    [Fact]
    public async Task Creating_a_new_version_stages_a_PolicyUpdated_envelope_in_the_outbox_only_on_success()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostWithRabbitMqAsync(rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();

                var tenantId = Guid.NewGuid();
                var result = await CreateEarlyCheckInVersionAsync(host, tenantId, expectedVersion: null, allowed: true);

                result.IsSuccess.Should().BeTrue();
                (await CountOutgoingPolicyUpdatedEnvelopesAsync(tenantId)).Should().Be(1);
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_rejected_command_stages_no_outbox_envelope()
    {
        var rabbitMqContainer = new RabbitMqBuilder().WithImage("rabbitmq:3-management-alpine").Build();
        await rabbitMqContainer.StartAsync();
        try
        {
            var host = await BuildHostWithRabbitMqAsync(rabbitMqContainer);
            try
            {
                await rabbitMqContainer.StopAsync();

                var tenantId = Guid.NewGuid();
                await CreateEarlyCheckInVersionAsync(host, tenantId, expectedVersion: null, allowed: true);

                // A stale/wrong expectedVersion is rejected as version_conflict before any write commits.
                var rejected = await CreateEarlyCheckInVersionAsync(host, tenantId, expectedVersion: 99, allowed: false);

                rejected.IsFailure.Should().BeTrue();
                (await CountOutgoingPolicyUpdatedEnvelopesAsync(tenantId)).Should().Be(
                    1, "only the one successful command from setup — the rejected one must never stage an envelope");
            }
            finally
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
        finally
        {
            await rabbitMqContainer.DisposeAsync();
        }
    }

    /// <summary>
    /// Fase 5, Incremento 1, official decision 7 ("meta de 50ms p95") —
    /// Checkpoint 7 homologação. Measures <see cref="IEarlyCheckInPolicyReader.GetEffectiveAsync"/>
    /// directly (the public, typed query port another Bounded Context would
    /// actually call), never an HTTP round-trip through <c>PoliciesController</c>
    /// (which would also measure ASP.NET routing/auth/model-binding overhead
    /// that decision 7 was never about) — but backed by this fixture's real
    /// Postgres and real Redis (Testcontainers), never a mock or an in-memory
    /// substitute, per the same instruction. 100 warm-up calls establish a hot
    /// cache (and let the JIT settle) before the 1000 measured calls, run at
    /// 20 concurrent callers, each through its own fresh DI scope — mirrors a
    /// real request's own scope lifecycle instead of reusing one scope
    /// unrealistically across every call.
    /// </summary>
    [Fact]
    public async Task Benchmark_EARLY_CHECKIN_effective_resolution_meets_the_50ms_p95_target_with_a_warm_cache()
    {
        const int warmUpCalls = 100;
        const int measuredCalls = 1000;
        const int concurrency = 20;

        var tenantId = Guid.NewGuid();
        var created = await CreateEarlyCheckInVersionAsync(tenantId, expectedVersion: null, allowed: true);
        created.IsSuccess.Should().BeTrue();

        // Cache-miss diagnostic only, never part of the p95 assertion below (decision 7: "medir
        // cache miss separadamente apenas para diagnóstico") — the very first resolution for this
        // tenant, before anything has ever populated the cache for it.
        var missStopwatch = Stopwatch.StartNew();
        await ResolveAsync(tenantId);
        missStopwatch.Stop();

        for (var i = 0; i < warmUpCalls; i++)
            await ResolveAsync(tenantId);

        var latenciesMs = new ConcurrentBag<double>();
        using var semaphore = new SemaphoreSlim(concurrency);
        var tasks = new List<Task>(measuredCalls);
        for (var i = 0; i < measuredCalls; i++)
        {
            await semaphore.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var result = await ResolveAsync(tenantId);
                    stopwatch.Stop();
                    result.Status.Should().Be(PolicyReadStatus.Resolved);
                    latenciesMs.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);

        var sorted = latenciesMs.OrderBy(x => x).ToArray();
        sorted.Should().HaveCount(measuredCalls);

        double Percentile(double p)
        {
            var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
            return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
        }

        var p50 = Percentile(50);
        var p95 = Percentile(95);
        var p99 = Percentile(99);
        var max = sorted[^1];

        _output.WriteLine("=== Fase 5, decisão oficial 7 — benchmark de resolução de política (EARLY_CHECKIN, warm cache) ===");
        _output.WriteLine($"Build configuration: {(System.Diagnostics.Debugger.IsAttached ? "Debug (debugger attached)" : "Debug")}");
        _output.WriteLine($"Warm-up calls: {warmUpCalls}; measured calls: {measuredCalls}; concurrency: {concurrency}");
        _output.WriteLine($"Cache-miss (first-ever resolution, diagnostic only): {missStopwatch.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"p50: {p50:F2} ms");
        _output.WriteLine($"p95: {p95:F2} ms");
        _output.WriteLine($"p99: {p99:F2} ms");
        _output.WriteLine($"max: {max:F2} ms");

        p95.Should().BeLessOrEqualTo(50, "official decision 7 requires p95 <= 50ms with a warm cache");
    }

    /// <summary>
    /// A short-lived host, separate from the fixture's own shared one, built
    /// fresh per outbox test — mirrors <c>CondominiumIntegrationEventsTests.BuildHostAsync</c>
    /// exactly, adding the one <c>PublishMessage(...).ToRabbitRoutingKey(...).UseDurableOutbox()</c>
    /// rule <c>PolicyUpdated</c> actually needs to be durably staged (the
    /// fixture's own shared host has no such rule — it never publishes
    /// anything, see this class's own doc comment).
    /// </summary>
    private async Task<IHost> BuildHostWithRabbitMqAsync(RabbitMqContainer rabbitMqContainer)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Configuration"] = _fixture.AppConnectionString,
            ["Configuration:PolicyCache:ConnectionString"] = _fixture.RedisConnectionString,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddConfigurationModule(configuration);
        hostBuilder.Services.AddConfigurationCommandDispatch();

        hostBuilder.UseWolverine(opts =>
        {
            opts.UseRabbitMq(rabbit =>
            {
                rabbit.HostName = rabbitMqContainer.Hostname;
                rabbit.Port = rabbitMqContainer.GetMappedPublicPort(5672);
                rabbit.UserName = RabbitMqBuilder.DefaultUsername;
                rabbit.Password = RabbitMqBuilder.DefaultPassword;
            });

            opts.PersistMessagesWithPostgresql(_fixture.AppConnectionString, MainSchema);
            opts.EnrollAncillaryPostgresqlOutbox(_fixture.AppConnectionString, ConfigurationOutboxSchema, typeof(ConfigurationDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();

            opts.PublishMessage(typeof(PolicyUpdated))
                .ToRabbitRoutingKey(ConfigurationEventsExchange, "policy_updated", exchange => exchange.ExchangeType = ExchangeType.Topic)
                .UseDurableOutbox();
        });

        var host = hostBuilder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// Filtered by <paramref name="tenantId"/>'s presence in the envelope
    /// body — this fixture's Postgres container (and its
    /// <c>configuration_messaging</c> schema) is shared by every test in this
    /// class, and every write in this increment produces the same
    /// <c>PolicyUpdated</c> message type, so a plain type-only count would
    /// see envelopes staged by OTHER tests too. Mirrors
    /// <c>CondominiumIntegrationEventsTests.EnvelopeIsPendingAsync</c>'s own
    /// technique of searching for a specific id inside <c>body</c>.
    /// </summary>
    private async Task<int> CountOutgoingPolicyUpdatedEnvelopesAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_fixture.MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT count(*) FROM {ConfigurationOutboxSchema}.wolverine_outgoing_envelopes
            WHERE message_type = @messageType
              AND position(convert_to(@tenantId, 'UTF8') in body) > 0
            """;
        command.Parameters.AddWithValue("messageType", PolicyUpdatedMessageType);
        command.Parameters.AddWithValue("tenantId", tenantId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<Result<PolicyValueDetailResult>> CreateEarlyCheckInVersionAsync(
        IHost host, Guid tenantId, int? expectedVersion, bool allowed)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();
        var value = $$"""{"allowed":{{(allowed ? "true" : "false")}},"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""";
        return await dispatcher.Send(
            new CreatePolicyValueVersionCommand(tenantId, Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, value, "test setup", expectedVersion, null),
            CancellationToken.None);
    }

    // ---- Helpers (cache tests) ----

    private async Task<PolicyReadResult<EarlyCheckInPolicy>> ResolveAsync(Guid tenantId, Guid? propertyId = null)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IEarlyCheckInPolicyReader>();
        return await reader.GetEffectiveAsync(tenantId, propertyId);
    }

    private async Task<Result<PolicyValueDetailResult>> CreateEarlyCheckInVersionAsync(Guid tenantId, int? expectedVersion, bool allowed)
    {
        await using var scope = _fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IConfigurationRequestDispatcher>();
        var value = $$"""{"allowed":{{(allowed ? "true" : "false")}},"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""";
        return await dispatcher.Send(
            new CreatePolicyValueVersionCommand(tenantId, Guid.NewGuid(), "EARLY_CHECKIN", "Tenant", null, value, "test setup", expectedVersion, null),
            CancellationToken.None);
    }

    /// <summary>
    /// <paramref name="tenantId"/> is set on the returned context's own
    /// <see cref="ITenantContext"/> — required for any subsequent READ
    /// through this instance (e.g. <c>PolicyValues.FirstAsync(...)</c>): EF
    /// Core's Global Query Filter fails closed (matches nothing) when
    /// <see cref="ITenantContext.IsResolved"/> is false, independently of the
    /// <c>app.tenant_id</c> RLS session variable a caller sets separately —
    /// the same two-mechanism gotcha already documented for
    /// <c>PolicyResolutionTests</c> (Checkpoint 3). Writes (Add + SaveChanges)
    /// are unaffected by the filter, which is why every other raw DbContext
    /// in this file/PolicyResolutionTests that only ever writes never needed
    /// this.
    /// </summary>
    private ConfigurationDbContext CreateAppDbContext(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseNpgsql(_fixture.AppConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
            .Options;
        return new ConfigurationDbContext(options, tenantContext);
    }
}
