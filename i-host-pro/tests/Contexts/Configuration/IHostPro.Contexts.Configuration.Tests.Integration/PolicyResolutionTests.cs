using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Domain;
using IHostPro.Contexts.Configuration.Infrastructure;
using IHostPro.Contexts.Configuration.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Configuration.Tests.Integration;

/// <summary>
/// Exercises the real PROPERTY → TENANT → GLOBAL resolution algorithm and
/// the two typed readers (<see cref="IEarlyCheckInPolicyReader"/>,
/// <see cref="ILateCheckoutPolicyReader"/>) against a real PostgreSQL
/// instance (Testcontainers) — Fase 5, Incremento 1, Checkpoint 3 (Resolução
/// e contrato). Readers are resolved through the same public composition
/// root (<c>AddConfigurationModule</c>) any real consumer would use — the
/// internal resolver/reader implementations are never referenced by name
/// here, only through <c>Configuration.Contracts</c>.
/// </summary>
public class PolicyResolutionTests : IClassFixture<PolicyResolutionTests.Fixture>, IAsyncLifetime
{
    private readonly Fixture _fixture;

    public PolicyResolutionTests(Fixture fixture) => _fixture = fixture;

    /// <summary>
    /// <c>global_policy_values</c> carries no tenant boundary — unlike every
    /// other table in this suite, its rows are not naturally isolated by a
    /// fresh <c>Guid.NewGuid()</c> tenant per test. Truncating the mutable
    /// tables before every test (the seeded <c>policy_definitions</c>
    /// catalog is left untouched) guarantees each test starts from a clean
    /// slate regardless of execution order.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "TRUNCATE TABLE configuration.policy_values, configuration.policy_audit_log, configuration.global_policy_values;";
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public sealed class Fixture : IAsyncLifetime
    {
        private PostgreSqlContainer _container = null!;
        private ServiceProvider _serviceProvider = null!;
        public string ConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            // The InitialCreate migration unconditionally GRANTs to
            // ihostpro_app and references ihostpro_migrator in its ALTER
            // DEFAULT PRIVILEGES statement (Checkpoint 2) — both roles must
            // exist even though this test suite, unlike
            // ConfigurationFoundationTests, connects as the container's own
            // superuser throughout for everything (privilege boundaries are
            // already covered there; this suite is about resolution
            // correctness only).
            await using (var adminConnection = new NpgsqlConnection(ConnectionString))
            {
                await adminConnection.OpenAsync();
                await using var command = adminConnection.CreateCommand();
                command.CommandText = """
                    CREATE ROLE ihostpro_app LOGIN PASSWORD 'test_app_password';
                    CREATE ROLE ihostpro_migrator LOGIN PASSWORD 'test_migrator_password';
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Configuration"] = ConnectionString,
                    // This suite is about resolution correctness, not caching
                    // (that is RedisPolicyValueCacheTests' own job, Checkpoint
                    // 6) — a syntactically valid but never-actually-reachable
                    // address is deliberate: every test here exercises
                    // RedisPolicyValueCache's fail-closed-to-PostgreSQL path
                    // implicitly, on every single resolution, which is itself
                    // a meaningful (if incidental) confirmation that a
                    // permanently unavailable cache never breaks a real read.
                    ["Configuration:PolicyCache:ConnectionString"] = "localhost:1",
                })
                .Build();

            // ITenantContext is normally registered once by the Host
            // (IHostPro.Api/Program.cs) — reproduced here at the minimum
            // scope needed for a realistic composition root.
            services.AddScoped<ITenantContext, TenantContext>();
            // AddConfigurationModule resolves ILogger<RedisPolicyValueCache>
            // (Checkpoint 6) — a real Host always registers logging; this
            // bare ServiceCollection composition root must do so explicitly.
            services.AddLogging();
            services.AddConfigurationModule(configuration);
            _serviceProvider = services.BuildServiceProvider();

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ConfigurationDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync()
        {
            await _serviceProvider.DisposeAsync();
            await _container.DisposeAsync();
        }

        public AsyncServiceScope CreateScope() => _serviceProvider.CreateAsyncScope();
    }

    // ---- Precedence ----

    [Fact]
    public async Task Property_value_takes_precedence_over_tenant_and_global()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":false}""");
        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Property(propertyId), """{"allowed":true,"requiresCleaningCompleted":true,"requiresForm":true,"notifyFrontDesk":true}""");

        var result = await ResolveEarlyCheckInAsync(tenantId, propertyId);

        result.Status.Should().Be(PolicyReadStatus.Resolved);
        result.ResolvedScope.Should().Be(PolicyResolvedScope.Property);
        result.Value!.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Tenant_value_is_used_when_no_property_value_exists()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""");
        await SeedGlobalAsync("EARLY_CHECKIN", """{"allowed":false}""");

        var result = await ResolveEarlyCheckInAsync(tenantId, propertyId);

        result.Status.Should().Be(PolicyReadStatus.Resolved);
        result.ResolvedScope.Should().Be(PolicyResolvedScope.Tenant);
        result.Value!.Allowed.Should().BeTrue();
        result.Version.Should().Be(1);
    }

    [Fact]
    public async Task Global_value_is_used_when_no_tenant_or_property_value_exists()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await SeedGlobalAsync("EARLY_CHECKIN", """{"allowed":true,"requiresForm":true}""");

        var result = await ResolveEarlyCheckInAsync(tenantId, propertyId);

        result.Status.Should().Be(PolicyReadStatus.Resolved);
        result.ResolvedScope.Should().Be(PolicyResolvedScope.Global);
        result.Value!.RequiresForm.Should().BeTrue();
        result.Version.Should().BeNull("GLOBAL values carry no version history");
    }

    [Fact]
    public async Task NotConfigured_is_returned_when_nothing_exists_at_any_level()
    {
        var result = await ResolveEarlyCheckInAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Status.Should().Be(PolicyReadStatus.NotConfigured);
        result.Value.Should().BeNull();
        result.ResolvedScope.Should().BeNull();
        result.Version.Should().BeNull();
    }

    [Fact]
    public async Task Resolving_without_a_propertyId_skips_the_property_level()
    {
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();

        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Property(propertyId), """{"allowed":false}""");
        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""");

        var result = await ResolveEarlyCheckInAsync(tenantId, propertyId: null);

        result.ResolvedScope.Should().Be(PolicyResolvedScope.Tenant);
        result.Value!.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Superseded_versions_are_never_resolved()
    {
        var tenantId = Guid.NewGuid();

        await using var dbContext = CreateDbContext(_fixture.ConnectionString);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var v1 = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":false}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "initial setup");
        dbContext.PolicyValues.Add(v1);
        await dbContext.SaveChangesAsync();

        v1.Supersede();
        var v2 = PolicyValue.CreateNextVersion(
            Guid.NewGuid(), tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), PolicyVersion.Create(1),
            """{"allowed":true}""", DateTimeOffset.UtcNow, Guid.NewGuid(), "policy change");
        dbContext.PolicyValues.Add(v2);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        var result = await ResolveEarlyCheckInAsync(tenantId, propertyId: null);

        result.Version.Should().Be(2);
        result.Value!.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Cross_tenant_data_is_never_resolved()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedTenantScopedAsync(tenantA, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""");

        var result = await ResolveEarlyCheckInAsync(tenantB, propertyId: null);

        result.Status.Should().Be(PolicyReadStatus.NotConfigured);
    }

    // ---- Typed readers: full round trip ----

    [Fact]
    public async Task EarlyCheckIn_resolves_correctly_with_all_fields_round_tripped()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantScopedAsync(
            tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(),
            """{"allowed":true,"earliestTime":"13:30:00","requiresCleaningCompleted":true,"requiresForm":false,"notifyFrontDesk":true}""");

        var result = await ResolveEarlyCheckInAsync(tenantId, propertyId: null);

        result.Value.Should().Be(new EarlyCheckInPolicy(true, new TimeOnly(13, 30), true, false, true));
    }

    [Fact]
    public async Task LateCheckout_resolves_correctly_with_all_fields_round_tripped()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantScopedAsync(
            tenantId, "LATE_CHECKOUT", PolicyScope.Tenant(),
            """{"allowed":true,"latestTime":"15:00:00","chargeType":"percentage","chargeValue":10.5,"requiresPix":true,"blocksCalendar":true,"updatesCleaning":true}""");

        await using var scope = _fixture.CreateScope();
        SetAmbientTenant(scope, tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<ILateCheckoutPolicyReader>();

        var result = await reader.GetEffectiveAsync(tenantId, null);

        result.Value.Should().Be(new LateCheckoutPolicy(true, new TimeOnly(15, 0), LateCheckoutChargeType.Percentage, 10.5m, true, true, true));
    }

    // ---- Fail-closed: unavailability is never hidden as NotConfigured ----

    [Fact]
    public async Task A_malformed_stored_value_throws_PolicyEngineUnavailableException_never_NotConfigured()
    {
        var tenantId = Guid.NewGuid();

        // PolicyValue.Value only validates non-emptiness at the domain
        // layer (JSON schema validation is a Checkpoint 4/API-time
        // concern), and the jsonb column itself only guarantees syntactic
        // JSON validity, not shape — "allowed" holding a string instead of
        // a boolean is valid JSON that a future write-side validator would
        // still reject, and is exactly the kind of corrupt/incompatible row
        // this test proves the reader fails closed on, rather than silently
        // treating it as "nothing configured".
        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":"not-a-boolean"}""");

        var act = async () => await ResolveEarlyCheckInAsync(tenantId, propertyId: null);

        await act.Should().ThrowAsync<PolicyEngineUnavailableException>();
    }

    // ---- No cross-transaction leak ----

    [Fact]
    public async Task Two_consecutive_resolutions_on_the_same_reader_both_succeed_without_a_leaked_transaction()
    {
        var tenantId = Guid.NewGuid();
        await SeedTenantScopedAsync(tenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""");

        await using var scope = _fixture.CreateScope();
        SetAmbientTenant(scope, tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IEarlyCheckInPolicyReader>();

        var first = await reader.GetEffectiveAsync(tenantId, null);
        var second = await reader.GetEffectiveAsync(tenantId, null);

        first.Status.Should().Be(PolicyReadStatus.Resolved);
        second.Status.Should().Be(PolicyReadStatus.Resolved);
    }

    // ---- Helpers ----

    private async Task<PolicyReadResult<EarlyCheckInPolicy>> ResolveEarlyCheckInAsync(Guid tenantId, Guid? propertyId)
    {
        await using var scope = _fixture.CreateScope();
        SetAmbientTenant(scope, tenantId);
        var reader = scope.ServiceProvider.GetRequiredService<IEarlyCheckInPolicyReader>();
        return await reader.GetEffectiveAsync(tenantId, propertyId);
    }

    /// <summary>
    /// In production, <c>TenantResolutionMiddleware</c> resolves the
    /// request's tenant onto the DI-scoped <see cref="ITenantContext"/>
    /// (shared by every Bounded Context's DbContext in that scope, including
    /// <c>ConfigurationDbContext</c>'s own Global Query Filter) before any
    /// handler or reader runs. There is no middleware here, so every test
    /// must reproduce that one step itself — <see cref="IPolicyValueResolver"/>'s
    /// own throwaway <see cref="TenantContext"/> only ever drives the RLS
    /// session variable, never the EF Global Query Filter.
    /// </summary>
    private static void SetAmbientTenant(AsyncServiceScope scope, Guid tenantId) =>
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);

    private async Task SeedTenantScopedAsync(Guid tenantId, string policyCode, PolicyScope scope, string value)
    {
        await using var dbContext = CreateDbContext(_fixture.ConnectionString);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        var policyValue = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), tenantId, policyCode, scope, value, DateTimeOffset.UtcNow, Guid.NewGuid(), "test setup");
        dbContext.PolicyValues.Add(policyValue);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task SeedGlobalAsync(string policyCode, string value)
    {
        await using var dbContext = CreateDbContext(_fixture.ConnectionString);

        var globalValue = GlobalPolicyValue.Create(Guid.NewGuid(), policyCode, value, DateTimeOffset.UtcNow);
        dbContext.GlobalPolicyValues.Add(globalValue);

        await dbContext.SaveChangesAsync();
    }

    private static async Task SetTenantAsync(ConfigurationDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static ConfigurationDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ConfigurationDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "configuration"))
            .Options;

        return new ConfigurationDbContext(options, new TenantContext());
    }
}
