using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Api.Contracts;
using IHostPro.Contexts.Dashboard.Api.Controllers;
using IHostPro.Contexts.Dashboard.Infrastructure;
using IHostPro.Contexts.Dashboard.Infrastructure.Persistence;
using IHostPro.Contexts.Dashboard.Infrastructure.Projections;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.Dashboard.Tests.Integration;

/// <summary>
/// End-to-end test of <see cref="DashboardController"/> against the REAL
/// composition root wiring (real ASP.NET Core host/TestServer, real JWT,
/// real PostgreSQL for Identity's permission catalog and Dashboard's own
/// projections) — Fase 7, Incremento 2 (Dashboard &amp; Reporting
/// Foundation, Checkpoint 2). Mirrors <c>ScheduleEndpointsTests</c>'s
/// structure. No Wolverine/outbox setup is needed — the Overview query
/// never publishes an event and <c>TenantAwareUnitOfWork&lt;DashboardDbContext&gt;</c>
/// has no Wolverine dependency (unlike Reservations' own composition, which
/// also wires write commands). Rows are seeded directly into Dashboard's
/// own projection tables — the real event-consumption path is already
/// proven separately by the CP1 Worker round-trip tests; this file's own
/// subject is the read/composition/authorization API layer built on top.
/// </summary>
public class DashboardOverviewEndpointsTests : IClassFixture<DashboardOverviewEndpointsTests.Fixture>
{
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public DashboardOverviewEndpointsTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _container = null!;
        public string MigratorConnectionString { get; private set; } = null!;
        public string AppConnectionString { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16")
                .WithDatabase("ihostpro_test")
                .WithUsername("ihostpro")
                .WithPassword("ihostpro_dev")
                .Build();

            await _container.StartAsync();

            var adminConnectionString = _container.GetConnectionString();

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
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
                await identityDbContext.Database.MigrateAsync();
            await using (var dashboardDbContext = CreateDashboardDbContext(MigratorConnectionString))
                await dashboardDbContext.Database.MigrateAsync();

            await GrantSchemaAsync("identity");
            await GrantSchemaAsync("dashboard");
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static DashboardDbContext CreateDashboardDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<DashboardDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
                .Options;
            return new DashboardDbContext(options, new TenantContext());
        }

        private async Task GrantSchemaAsync(string schema)
        {
            await using var connection = new NpgsqlConnection(MigratorConnectionString);
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
    }

    // ---- Server ---------------------------------------------------------

    private async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:Dashboard"] = _appConnectionString,
            ["Identity:Jwt:Issuer"] = Issuer,
            ["Identity:Jwt:Audience"] = Audience,
            ["Identity:Jwt:AccessTokenLifetime"] = "00:15:00",
            ["Identity:Jwt:ClockSkew"] = "00:01:00",
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = _signingKeyPem,
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
        }).Build();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureAppConfiguration(cfg => cfg.AddConfiguration(configuration));
                webHost.ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(DashboardController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddDashboardModule(configuration);
                    services.AddDashboardQueryDispatch();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        return await hostBuilder.StartAsync();
    }

    private static async Task<string> GenerateTokenAsync(IHost host, Guid userId, Guid tenantId, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();
        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: tenantId, SessionId: Guid.NewGuid(), Roles: roles);
        return generator.GenerateAccessToken(request).Token;
    }

    // ---- Seeding ------------------------------------------------------------

    private async Task SeedReservationAsync(Guid tenantId, string status, DateTimeOffset checkInAt, DateTimeOffset checkOutAt)
    {
        var entry = new DashboardReservationProjectionEntry(
            tenantId, Guid.NewGuid(), Guid.NewGuid(), status, checkInAt, checkOutAt, checkInAt);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDashboardDbContext(tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.ReservationProjection.Add(entry);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task SetTenantAsync(DashboardDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private DashboardDbContext CreateDashboardDbContext(ITenantContext? tenantContext = null)
    {
        var options = new DbContextOptionsBuilder<DashboardDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "dashboard"))
            .Options;
        return new DashboardDbContext(options, tenantContext ?? new TenantContext());
    }

    // ---- HTTP helpers ---------------------------------------------------

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static string OverviewRoute(DateTimeOffset from, DateTimeOffset to) =>
        $"/api/v1/dashboard/overview?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    // ---- Authentication/authorization -----------------------------------

    [Fact]
    public async Task Overview_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, OverviewRoute(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Overview_with_a_role_holding_neither_DASHBOARD_MANAGE_nor_DASHBOARD_READ_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        // PROPERTY_OWNER only holds DASHBOARD:READ:OWN_OWNER, which this
        // checkpoint deliberately never checks (mandate §30 scope gate).
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["PROPERTY_OWNER"]);

        var response = await GetAsync(client, OverviewRoute(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>DASHBOARD:USE (AI_AGENT) alone must never authorize this administrative endpoint (mandate §30).</summary>
    [Fact]
    public async Task Overview_with_AI_AGENT_holding_only_DASHBOARD_USE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await GetAsync(client, OverviewRoute(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>HOUSEKEEPER holds no DASHBOARD permission at all (mandate §30).</summary>
    [Fact]
    public async Task Overview_with_HOUSEKEEPER_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await GetAsync(client, OverviewRoute(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATOR")]
    public async Task Overview_with_a_role_holding_either_DASHBOARD_MANAGE_or_DASHBOARD_READ_returns_200(string role)
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), [role]);

        var response = await GetAsync(client, OverviewRoute(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Empty / populated overview ----------------------------------

    [Fact]
    public async Task Overview_with_no_data_returns_200_with_zeros_and_empty_lists()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, OverviewRoute(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<DashboardOverviewResponse>(JsonWebDefaults))!;

        body.Reservations.CheckInsInPeriod.Should().Be(0);
        body.Reservations.StatusCounts.Should().BeEmpty();
        body.Housekeeping.Pending.Should().Be(0);
        body.Properties.Active.Should().Be(0);
        body.Occurrences.TotalInPeriod.Should().Be(0);
        body.Occurrences.ByType.Should().BeEmpty();
    }

    [Fact]
    public async Task Overview_with_populated_data_returns_correct_counts_and_the_processed_period()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 5, 31, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(tenantId, "confirmed", checkInAt: from.AddDays(5), checkOutAt: from.AddDays(8));
        await SeedReservationAsync(tenantId, "confirmed", checkInAt: from.AddDays(6), checkOutAt: from.AddDays(9));

        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var response = await GetAsync(client, OverviewRoute(from, to), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<DashboardOverviewResponse>(JsonWebDefaults))!;

        body.Period.From.Should().Be(from);
        body.Period.To.Should().Be(to);
        body.Reservations.CheckInsInPeriod.Should().Be(2);
        body.Reservations.StatusCounts.Should().ContainSingle(s => s.Status == "confirmed" && s.Count == 2);
        body.GeneratedAtUtc.Should().BeOnOrAfter(from).And.BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Overview_never_reflects_another_tenants_rows()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var ownTenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var from = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

        await SeedReservationAsync(otherTenantId, "confirmed", checkInAt: from.AddDays(2), checkOutAt: from.AddDays(4));

        var token = await GenerateTokenAsync(host, Guid.NewGuid(), ownTenantId, ["ADMIN"]);
        var response = await GetAsync(client, OverviewRoute(from, to), token);

        var body = (await response.Content.ReadFromJsonAsync<DashboardOverviewResponse>(JsonWebDefaults))!;

        body.Reservations.CheckInsInPeriod.Should().Be(0, "a different tenant's RLS-scoped connection must never see this tenant's reservations");
    }

    // ---- Validation --------------------------------------------------------

    [Fact]
    public async Task Overview_with_to_not_after_from_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var moment = DateTimeOffset.UtcNow;

        var response = await GetAsync(client, OverviewRoute(moment, moment), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Overview_with_a_window_larger_than_100_days_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(101);

        var response = await GetAsync(client, OverviewRoute(from, to), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Overview_with_a_window_of_exactly_100_days_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(100);

        var response = await GetAsync(client, OverviewRoute(from, to), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
