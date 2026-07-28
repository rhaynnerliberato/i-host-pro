using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Real-PostgreSQL coverage of <see cref="DevelopmentIdentitySeeder"/>
/// (Incremento 2 plan, ajuste 3-4): enabled/disabled behavior, idempotency,
/// safety under concurrent host instances via the PostgreSQL advisory lock,
/// password-policy enforcement, absence of role assignment (explicitly out
/// of scope), and absence of the admin password in logs.
/// </summary>
public class DevelopmentIdentitySeederTests : IClassFixture<DevelopmentIdentitySeederTests.Fixture>
{
    private readonly Fixture _fixture;

    public DevelopmentIdentitySeederTests(Fixture fixture) => _fixture = fixture;

    /// <summary>
    /// Started once per test class — see <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s
    /// doc comment for the full rationale. Every test uses a freshly
    /// generated tenant slug/e-mail, so sharing the container across tests
    /// creates no collision.
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        public PostgreSqlContainer Container { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await Container.StartAsync();

            var adminConnectionString = Container.GetConnectionString();

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

            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
            builder.Username = "ihostpro_migrator";
            builder.Password = MigratorRolePassword;
            var migratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(migratorConnectionString, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await Container.DisposeAsync();
    }

    private const string CompliantPassword = "Dev-Seed-Passw0rd!";

    // ---- Host construction ------------------------------------------------

    private (IHost Host, TestLoggerProvider Logger) BuildUnstartedHost(
        bool enabled, string tenantSlug, string adminEmail, string adminPassword)
    {
        // AddIdentityModule unconditionally binds+validates JwtOptions,
        // RefreshTokenOptions and AccountLockoutOptions regardless of
        // isDevelopmentEnvironment — irrelevant to what this class tests, but
        // required for host.StartAsync() to get far enough to run the seeder
        // (mirrors IdentityIntegrationEventsTests.BuildHostAsync).
        using var signingKey = RSA.Create(2048);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _fixture.AppConnectionString,
            ["Identity:Jwt:Issuer"] = "https://identity.ihostpro.test",
            ["Identity:Jwt:Audience"] = "ihostpro-api-test",
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = signingKey.ExportRSAPrivateKeyPem(),
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
            ["Identity:DevelopmentSeed:Enabled"] = enabled.ToString(),
            ["Identity:DevelopmentSeed:TenantSlug"] = tenantSlug,
            ["Identity:DevelopmentSeed:TenantName"] = "Seeder Test Tenant",
            ["Identity:DevelopmentSeed:AdminEmail"] = adminEmail,
            ["Identity:DevelopmentSeed:AdminFullName"] = "Seeder Test Admin",
            ["Identity:DevelopmentSeed:AdminPassword"] = adminPassword,
        }).Build();

        var hostBuilder = Host.CreateApplicationBuilder();
        var logger = new TestLoggerProvider();
        hostBuilder.Logging.AddProvider(logger);
        // IHostPro.Api/Worker's Program.cs always registers ITenantContext
        // before calling AddIdentityModule — IdentityDbContext depends on it,
        // but it is deliberately not registered by AddIdentityModule itself
        // (mirrors IdentityIntegrationEventsTests.BuildHostAsync).
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddIdentityModule(configuration, isDevelopmentEnvironment: true);

        var host = hostBuilder.Build();
        return (host, logger);
    }

    private static string NewSlug() => $"seed-{Guid.NewGuid():N}"[..20];
    private static string NewEmail() => $"seed-{Guid.NewGuid():N}@dev.local";

    // ---- Tests --------------------------------------------------------

    [Fact]
    public async Task Disabled_seed_creates_no_tenant_or_user_even_when_other_fields_are_populated()
    {
        var slug = NewSlug();
        var (host, _) = BuildUnstartedHost(enabled: false, slug, NewEmail(), CompliantPassword);

        await host.StartAsync();
        try
        {
            (await FindTenantAsync(slug)).Should().BeNull();
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Enabled_seed_creates_tenant_and_user_with_correct_data_and_a_working_password_hash()
    {
        var slug = NewSlug();
        var email = NewEmail();
        var (host, _) = BuildUnstartedHost(enabled: true, slug, email, CompliantPassword);

        await host.StartAsync();
        try
        {
            var tenant = await FindTenantAsync(slug);
            tenant.Should().NotBeNull();
            tenant!.Name.Should().Be("Seeder Test Tenant");

            var user = await FindUserAsync(tenant.Id, email);
            user.Should().NotBeNull();
            user!.FullName.Should().Be("Seeder Test Admin");

            var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
            hasher.VerifyHashedPassword(null!, user.PasswordHash.Value, CompliantPassword)
                .Should().Be(PasswordVerificationResult.Success);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Running_the_seeder_twice_sequentially_is_idempotent()
    {
        var slug = NewSlug();
        var email = NewEmail();

        var (firstHost, _) = BuildUnstartedHost(enabled: true, slug, email, CompliantPassword);
        await firstHost.StartAsync();
        await StopGracefullyAsync(firstHost);

        var (secondHost, _) = BuildUnstartedHost(enabled: true, slug, email, CompliantPassword);
        await secondHost.StartAsync();
        await StopGracefullyAsync(secondHost);

        (await CountTenantsAsync(slug)).Should().Be(1);
        var tenant = await FindTenantAsync(slug);
        (await CountUsersAsync(tenant!.Id, email)).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_seeder_instances_do_not_duplicate_or_deadlock()
    {
        var slug = NewSlug();
        var email = NewEmail();

        var hosts = Enumerable.Range(0, 3)
            .Select(_ => BuildUnstartedHost(enabled: true, slug, email, CompliantPassword).Host)
            .ToList();

        // The PostgreSQL advisory lock (pg_advisory_xact_lock), not in-process
        // coordination, is what must make this safe — three hosts starting at
        // the same instant against the same database is exactly the scenario
        // it exists for (multiple Api/Worker instances in Development).
        await Task.WhenAll(hosts.Select(h => h.StartAsync()));
        try
        {
            (await CountTenantsAsync(slug)).Should().Be(1);
            var tenant = await FindTenantAsync(slug);
            (await CountUsersAsync(tenant!.Id, email)).Should().Be(1);
        }
        finally
        {
            await Task.WhenAll(hosts.Select(StopGracefullyAsync));
        }
    }

    [Fact]
    public async Task Weak_password_fails_startup_and_creates_neither_tenant_nor_user()
    {
        var slug = NewSlug();
        var (host, _) = BuildUnstartedHost(enabled: true, slug, NewEmail(), adminPassword: "weak");

        var act = async () => await host.StartAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().NotContain("weak");

        (await FindTenantAsync(slug)).Should().BeNull();
        host.Dispose();
    }

    [Fact]
    public async Task Seeded_user_is_not_assigned_any_role()
    {
        var slug = NewSlug();
        var email = NewEmail();
        var (host, _) = BuildUnstartedHost(enabled: true, slug, email, CompliantPassword);

        await host.StartAsync();
        try
        {
            var tenant = await FindTenantAsync(slug);
            var user = await FindUserAsync(tenant!.Id, email);

            await using var connection = new NpgsqlConnection(_fixture.AppConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            await using (var setTenant = connection.CreateCommand())
            {
                setTenant.Transaction = transaction;
                setTenant.CommandText = $"SET LOCAL app.tenant_id = '{tenant.Id:D}'";
                await setTenant.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM identity.user_roles WHERE user_id = @userId";
            command.Parameters.AddWithValue("userId", user!.Id);

            var roleCount = (long)(await command.ExecuteScalarAsync())!;
            roleCount.Should().Be(0);
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Seeded_admin_password_never_appears_in_a_logged_message()
    {
        const string distinctivePassword = "Dev-Seed-Distinctive-Passw0rd!";
        var slug = NewSlug();
        var (host, logger) = BuildUnstartedHost(enabled: true, slug, NewEmail(), distinctivePassword);

        await host.StartAsync();
        try
        {
            logger.Messages.Should().NotContain(m => m.Contains(distinctivePassword));
        }
        finally
        {
            await StopGracefullyAsync(host);
        }
    }

    // ---- DB helpers -----------------------------------------------------

    private async Task<Tenant?> FindTenantAsync(string slug)
    {
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, new TenantContext());
        return await dbContext.Tenants.FirstOrDefaultAsync(t => t.Slug == TenantSlug.Create(slug));
    }

    private async Task<long> CountTenantsAsync(string slug)
    {
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, new TenantContext());
        return await dbContext.Tenants.LongCountAsync(t => t.Slug == TenantSlug.Create(slug));
    }

    /// <summary>
    /// `users` carries FORCE ROW LEVEL SECURITY — reading it (even as the
    /// seeder's own <c>ihostpro_app</c> role) requires app.tenant_id set on
    /// the transaction, exactly as production code requires.
    /// </summary>
    private async Task<User?> FindUserAsync(Guid tenantId, string email)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        await using var dbContext = CreateDbContext(_fixture.AppConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
#pragma warning disable EF1002
        await dbContext.Database.ExecuteSqlRawAsync($"SET LOCAL app.tenant_id = '{tenantId:D}'");
#pragma warning restore EF1002
        var normalized = Email.Create(email).NormalizedValue;
        return await dbContext.Users.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalized);
    }

    private async Task<long> CountUsersAsync(Guid tenantId, string email)
    {
        var user = await FindUserAsync(tenantId, email);
        return user is null ? 0 : 1;
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }

    private static async Task StopGracefullyAsync(IHost host)
    {
        await host.StopAsync();
        host.Dispose();
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add(formatter(state, exception));
                    if (exception is not null)
                        sink.Add(exception.ToString());
                }
            }
        }
    }
}
