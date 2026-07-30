using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Sessions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// <see cref="SessionReader"/> against a real PostgreSQL instance (Incremento
/// 3, Checkpoint 4) — proves the self-service session listing is genuinely
/// scoped by user AND by tenant (Row-Level Security), and returns active
/// sessions only. No outbox/Wolverine provisioning needed: this reader
/// publishes no event.
/// </summary>
public class SessionReaderTests : IClassFixture<SessionReaderTests.Fixture>
{
    private readonly string _appConnectionString;

    public SessionReaderTests(Fixture fixture) => _appConnectionString = fixture.AppConnectionString;

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _postgresContainer = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _postgresContainer.StartAsync();

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
            var migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(migratorConnectionString, Guid.NewGuid());
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();
    }

    private IdentityDbContext CreateDbContext(Guid tenantId) => CreateDbContext(_appConnectionString, tenantId);

    private static IdentityDbContext CreateDbContext(string connectionString, Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private async Task SeedTenantAsync(Guid tenantId)
    {
        await using var dbContext = CreateDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task SeedUserAsync(Guid tenantId, Guid userId)
    {
        await using var dbContext = CreateDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var user = User.Register(
            userId, tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Test User",
            PasswordHash.FromEncoded("fake-encoded-hash"), DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task SeedSessionAsync(Guid tenantId, Guid userId, Guid sessionId, bool active = true)
    {
        await using var dbContext = CreateDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var session = Session.Open(sessionId, tenantId, userId, DateTimeOffset.UtcNow, "iPhone", "Safari", "203.0.113.7");
        if (!active)
            session.Revoke("TestRevocation", DateTimeOffset.UtcNow);
        dbContext.Sessions.Add(session);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Only_sessions_of_the_requested_user_are_returned()
    {
        var tenantId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var targetSessionId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        await SeedUserAsync(tenantId, targetUserId);
        await SeedUserAsync(tenantId, otherUserId);
        await SeedSessionAsync(tenantId, targetUserId, targetSessionId);
        await SeedSessionAsync(tenantId, otherUserId, Guid.NewGuid());
        await using var dbContext = CreateDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = new SessionReader(dbContext);

        var sessions = await reader.ListActiveByUserIdAsync(targetUserId, CancellationToken.None);

        sessions.Should().ContainSingle(s => s.Id == targetSessionId);
    }

    [Fact]
    public async Task Only_active_sessions_are_returned()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var activeSessionId = Guid.NewGuid();
        var revokedSessionId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        await SeedUserAsync(tenantId, userId);
        await SeedSessionAsync(tenantId, userId, activeSessionId, active: true);
        await SeedSessionAsync(tenantId, userId, revokedSessionId, active: false);
        await using var dbContext = CreateDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = new SessionReader(dbContext);

        var sessions = await reader.ListActiveByUserIdAsync(userId, CancellationToken.None);

        sessions.Select(s => s.Id).Should().BeEquivalentTo([activeSessionId]);
    }

    [Fact]
    public async Task A_session_belonging_to_a_different_tenant_is_never_returned_even_with_the_same_user_id()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var sharedUserId = Guid.NewGuid(); // simulates a matching userId value across tenants
        await SeedTenantAsync(tenantA);
        await SeedUserAsync(tenantA, sharedUserId);
        await SeedSessionAsync(tenantA, sharedUserId, Guid.NewGuid());

        // Query scoped to tenant B's RLS context — Row-Level Security must
        // make tenant A's session invisible regardless of the userId filter.
        await SeedTenantAsync(tenantB);
        await using var dbContext = CreateDbContext(tenantB);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantB);
        var reader = new SessionReader(dbContext);

        var sessions = await reader.ListActiveByUserIdAsync(sharedUserId, CancellationToken.None);

        sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_result_is_valid_when_the_user_has_no_active_session()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await SeedTenantAsync(tenantId);
        await SeedUserAsync(tenantId, userId);
        await using var dbContext = CreateDbContext(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);
        var reader = new SessionReader(dbContext);

        var sessions = await reader.ListActiveByUserIdAsync(userId, CancellationToken.None);

        sessions.Should().BeEmpty();
    }
}
