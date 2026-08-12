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
using IHostPro.Contexts.Housekeeping.Api.Contracts;
using IHostPro.Contexts.Housekeeping.Api.Controllers;
using IHostPro.Contexts.Housekeeping.Infrastructure;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Infrastructure.Projections;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;

namespace IHostPro.Contexts.Housekeeping.Tests.Integration;

/// <summary>
/// End-to-end test of <see cref="CleaningsController"/> against the REAL
/// composition root wiring: real ASP.NET Core host/TestServer, real JWT
/// (issued by Identity's own real stack), real PostgreSQL for Identity
/// (permission catalog + the real <c>HOUSEKEEPER</c> eligibility lookup) and
/// Housekeeping — mirrors <c>ReservationsEndpointsTests</c>'s structure.
/// Never seeds Property Management/Reservations schemas: <c>PropertyId</c>
/// eligibility is Housekeeping's own local projection (Checkpoint 0 gate),
/// seeded directly here.
/// </summary>
public class HousekeepingEndpointsTests : IClassFixture<HousekeepingEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string HousekeepingOutboxSchema = "housekeeping_messaging";
    private const string MainSchema = "platform_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public HousekeepingEndpointsTests(Fixture fixture)
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
            await using (var housekeepingDbContext = CreateHousekeepingDbContext(MigratorConnectionString))
                await housekeepingDbContext.Database.MigrateAsync();

            await ProvisionMainStoreAsMigratorAsync();
            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionOutboxAsMigratorAsync(HousekeepingOutboxSchema, typeof(HousekeepingDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static HousekeepingDbContext CreateHousekeepingDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
                .Options;
            return new HousekeepingDbContext(options, new TenantContext());
        }

        private async Task ProvisionMainStoreAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(MigratorConnectionString, MainSchema);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var mainHost = hostBuilder.Build();
            await mainHost.SetupResources();

            await GrantSchemaAsync(MainSchema);
        }

        private async Task ProvisionOutboxAsMigratorAsync(string schema, Type dbContextMarkerType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextMarkerType);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

            await GrantSchemaAsync(schema);
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
            ["ConnectionStrings:Housekeeping"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(CleaningsController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddHousekeepingModule(configuration);
                    services.AddHousekeepingCommandDispatch();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(_appConnectionString, MainSchema);
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, HousekeepingOutboxSchema, typeof(HousekeepingDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
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

    private async Task SeedActivePropertyProjectionAsync(Guid tenantId, Guid propertyId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetTenantAsync(dbContext, tenantId);

        dbContext.PropertyProjection.Add(new PropertyProjectionEntry(tenantId, propertyId, isActive: true));
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>Real Identity user + role membership — proves the real cross-context <c>IIdentityUserEligibilityReader</c> wiring, not a fake.</summary>
    /// <summary>
    /// <see cref="Tenant"/> is not <see cref="ITenantOwned"/> (never RLS-restricted
    /// — Incremento 1 plan, "Tenant e RLS") but <c>users.tenant_id</c> carries a
    /// real foreign key to it, so a row must exist here before any
    /// tenant-owned Identity entity can be inserted for the same id.
    /// Idempotent (Fase 6, Incremento 2A) — <see cref="SeedHousekeeperUserAsync"/>
    /// is now called more than once for the SAME tenant in tests that seed
    /// two housekeepers sharing a tenant.
    /// </summary>
    private async Task EnsureTenantExistsAsync(Guid tenantId)
    {
        await using var dbContext = CreateIdentityDbContext(_migratorConnectionString, new TenantContext());

        if (await dbContext.Tenants.AnyAsync(t => t.Id == tenantId))
            return;

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create($"test-{tenantId:N}"), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedHousekeeperUserAsync(Guid tenantId, string? roleCode = "HOUSEKEEPER")
    {
        await EnsureTenantExistsAsync(tenantId);

        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);

        await using var dbContext = CreateIdentityDbContext(_migratorConnectionString, tenantContext);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetIdentityTenantAsync(dbContext, tenantId);

        var email = Email.Create($"housekeeper-{Guid.NewGuid():N}@example.com");
        var user = User.Register(Guid.NewGuid(), tenantId, email, "Test Housekeeper", PasswordHash.FromEncoded("$argon2id$v=19$test"), DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        if (roleCode is not null)
            dbContext.UserRoles.Add(new UserRole(tenantId, user.Id, roleCode, DateTimeOffset.UtcNow, assignedByUserId: null));

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return user.Id;
    }

    // ---- HTTP helpers ---------------------------------------------------

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Put, route, body, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    // ---- Authentication/authorization ----

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(Guid.NewGuid(), null), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_with_a_role_lacking_CLEANINGS_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(Guid.NewGuid(), null), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATOR")]
    public async Task Create_as_ADMIN_or_OPERATOR_succeeds(string role)
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, [role]);

        var response = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);
        body!.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Create_for_an_unknown_property_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(Guid.NewGuid(), null), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonWebDefaults);
        problem.GetProperty("code").GetString().Should().Be("property_not_found");
    }

    [Fact]
    public async Task Create_without_a_propertyId_returns_400_validation_error()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(null, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Golden-path lifecycle ----

    [Fact]
    public async Task Full_administrative_lifecycle_create_assign_start_start_inspection_complete_succeeds()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);

        var assignResponse = await PostAsync(client, $"/api/v1/cleanings/{created!.Id}/assign", new AssignCleaningRequest(housekeeperUserId), token);
        assignResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await assignResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Assigned");

        var startResponse = await PostAsync(client, $"/api/v1/cleanings/{created.Id}/start", null, token);
        startResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await startResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Started");

        var startInspectionResponse = await PostAsync(client, $"/api/v1/cleanings/{created.Id}/start-inspection", null, token);
        startInspectionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await startInspectionResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("InInspection");

        var completeResponse = await PostAsync(client, $"/api/v1/cleanings/{created.Id}/complete", null, token);
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await completeResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Completed");

        var getResponse = await GetAsync(client, $"/api/v1/cleanings/{created.Id}", token);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await getResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task Assign_to_a_user_without_the_HOUSEKEEPER_role_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var nonHousekeeperUserId = await SeedHousekeeperUserAsync(tenantId, roleCode: "ADMIN");
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);

        var assignResponse = await PostAsync(client, $"/api/v1/cleanings/{created!.Id}/assign", new AssignCleaningRequest(nonHousekeeperUserId), token);

        assignResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await assignResponse.Content.ReadFromJsonAsync<JsonElement>(JsonWebDefaults);
        problem.GetProperty("code").GetString().Should().Be("housekeeper_not_eligible");
    }

    [Fact]
    public async Task Assign_to_a_nonexistent_user_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);

        var assignResponse = await PostAsync(client, $"/api/v1/cleanings/{created!.Id}/assign", new AssignCleaningRequest(Guid.NewGuid()), token);

        assignResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Completing_a_Pending_cleaning_returns_409_invalid_transition()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);

        var completeResponse = await PostAsync(client, $"/api/v1/cleanings/{created!.Id}/complete", null, token);

        completeResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await completeResponse.Content.ReadFromJsonAsync<JsonElement>(JsonWebDefaults);
        problem.GetProperty("code").GetString().Should().Be("invalid_cleaning_transition");
    }

    [Fact]
    public async Task Cancel_a_Pending_cleaning_succeeds()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);

        var cancelResponse = await PostAsync(client, $"/api/v1/cleanings/{created!.Id}/cancel", null, token);

        cancelResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cancelResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Cancelled");
    }

    [Fact]
    public async Task A_nonexistent_cleaning_id_returns_404_for_GetById()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/cleanings/{Guid.NewGuid()}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Real concurrency conflict (validates ICleaningTransitionExecutor's DbUpdateConcurrencyException translation) ----

    [Fact]
    public async Task Two_concurrent_start_requests_on_the_same_Assigned_cleaning_produce_exactly_one_success_and_one_conflict()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);
        await PostAsync(client, $"/api/v1/cleanings/{created!.Id}/assign", new AssignCleaningRequest(housekeeperUserId), token);

        // Two independent HttpClients (each its own connection/DbContext scope
        // per request) racing the SAME Start transition on the SAME row —
        // exactly the scenario the xmin-based optimistic concurrency token
        // exists to guard against.
        using var client2 = host.GetTestClient();
        var startTasks = new[]
        {
            PostAsync(client, $"/api/v1/cleanings/{created.Id}/start", null, token),
            PostAsync(client2, $"/api/v1/cleanings/{created.Id}/start", null, token),
        };
        var responses = await Task.WhenAll(startTasks);

        var statusCodes = responses.Select(r => r.StatusCode).OrderBy(c => c).ToArray();
        statusCodes.Should().Contain(HttpStatusCode.OK);
        // Either the second request loses the race on the domain guard
        // (InvalidOperationException -> invalid_cleaning_transition, if it
        // reads the row AFTER the first commit) or on the xmin token itself
        // (DbUpdateConcurrencyException -> cleaning_concurrency_conflict, if
        // it reads the row BEFORE the first commit but loses at SaveChanges)
        // — both are correct, mutually-exclusive-with-double-success outcomes
        // of the same real race; never two 200s.
        statusCodes.Count(c => c == HttpStatusCode.OK).Should().Be(1);
        statusCodes.Should().Contain(HttpStatusCode.Conflict);

        // Exactly one cleaning_started audit entry for this cleaning,
        // regardless of which of the two legitimate error codes the loser
        // hit (see the comment above) — proof by construction (mirrors
        // ReservationCommandHandlerTests' own reasoning) that the losing
        // attempt never audited: the handler stages the audit entry and the
        // domain mutation in the SAME transaction, so a single audit row is
        // only possible if the winning attempt's SaveChanges ran exactly
        // once and the loser's never committed.
        var auditCount = await CountCleaningStartedAuditEntriesAsync(tenantId, created.Id);
        auditCount.Should().Be(1, "exactly one cleaning_started audit entry must exist — the losing attempt must never audit");
    }

    private async Task<long> CountCleaningStartedAuditEntriesAsync(Guid tenantId, Guid cleaningId)
    {
        await using var connection = new NpgsqlConnection(_migratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setCommand = connection.CreateCommand())
        {
            setCommand.CommandText = $"SET LOCAL app.tenant_id = '{tenantId:D}'";
            await setCommand.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM housekeeping.cleaning_audit_log WHERE action_code = 'cleaning_started' AND aggregate_id = @id";
        command.Parameters.AddWithValue("id", cleaningId);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return count;
    }

    // ---- Listing ----

    [Fact]
    public async Task List_supports_status_filter_and_pagination()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        for (var i = 0; i < 3; i++)
            await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), token);

        var listResponse = await GetAsync(client, "/api/v1/cleanings?status=Pending&page=1&pageSize=2", token);

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await listResponse.Content.ReadFromJsonAsync<PagedCleaningResponse>(JsonWebDefaults);
        page!.TotalCount.Should().Be(3);
        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(i => i.Status == "Pending");
    }

    [Fact]
    public async Task List_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/cleanings", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Self-service (Portal da Faxineira, Fase 6 Incremento 2A) ----

    [Fact]
    public async Task MyCleanings_list_returns_only_cleanings_assigned_to_the_caller()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperA = await SeedHousekeeperUserAsync(tenantId);
        var housekeeperB = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createdForA = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperA, adminToken);
        await CreateAndAssignCleaningAsync(client, propertyId, housekeeperB, adminToken);

        var housekeeperAToken = await GenerateTokenAsync(host, housekeeperA, tenantId, ["HOUSEKEEPER"]);
        var listResponse = await GetAsync(client, "/api/v1/my-cleanings", housekeeperAToken);

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await listResponse.Content.ReadFromJsonAsync<PagedCleaningResponse>(JsonWebDefaults);
        page!.TotalCount.Should().Be(1);
        page.Items.Single().Id.Should().Be(createdForA.Id);
        page.Items.Single().AssignedHousekeeperUserId.Should().Be(housekeeperA);
    }

    [Fact]
    public async Task MyCleanings_getById_for_own_cleaning_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);

        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var getResponse = await GetAsync(client, $"/api/v1/my-cleanings/{created.Id}", housekeeperToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await getResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);
        detail!.Id.Should().Be(created.Id);
        detail.AssignedHousekeeperUserId.Should().Be(housekeeperUserId);
    }

    [Fact]
    public async Task MyCleanings_getById_for_a_cleaning_assigned_to_someone_else_returns_404_never_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperA = await SeedHousekeeperUserAsync(tenantId);
        var housekeeperB = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var createdForA = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperA, adminToken);

        var housekeeperBToken = await GenerateTokenAsync(host, housekeeperB, tenantId, ["HOUSEKEEPER"]);
        var getResponse = await GetAsync(client, $"/api/v1/my-cleanings/{createdForA.Id}", housekeeperBToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MyCleanings_getById_across_tenants_returns_404_never_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);

        var otherTenantId = Guid.NewGuid();
        var otherHousekeeperUserId = await SeedHousekeeperUserAsync(otherTenantId);
        var otherTenantToken = await GenerateTokenAsync(host, otherHousekeeperUserId, otherTenantId, ["HOUSEKEEPER"]);

        var getResponse = await GetAsync(client, $"/api/v1/my-cleanings/{created.Id}", otherTenantToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MyCleanings_list_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/my-cleanings", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MyCleanings_list_with_ADMIN_role_lacking_CLEANINGS_MANAGE_OWN_CLEANING_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await GetAsync(client, "/api/v1/my-cleanings", adminToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Self-service lifecycle (Portal da Faxineira, Fase 6 Incremento 2A) ----

    [Fact]
    public async Task Full_self_service_lifecycle_via_InTransit_start_start_inspection_complete_succeeds()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);

        var inTransitResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/in-transit", null, housekeeperToken);
        inTransitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await inTransitResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("InTransit");

        var startResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperToken);
        startResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await startResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Started");

        var startInspectionResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start-inspection", null, housekeeperToken);
        startInspectionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await startInspectionResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("InInspection");

        var completeResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/complete", null, housekeeperToken);
        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await completeResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task Self_service_waiting_materials_waiting_help_and_delay_all_succeed_for_the_owning_housekeeper()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);

        var delayCleaning = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        var delayResponse = await PostAsync(client, $"/api/v1/my-cleanings/{delayCleaning.Id}/delay", null, housekeeperToken);
        delayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delayResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Assigned");

        var materialsCleaning = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{materialsCleaning.Id}/start", null, housekeeperToken);
        var materialsResponse = await PostAsync(client, $"/api/v1/my-cleanings/{materialsCleaning.Id}/waiting-materials", null, housekeeperToken);
        materialsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await materialsResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("WaitingMaterials");

        var helpCleaning = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{helpCleaning.Id}/start", null, housekeeperToken);
        var helpResponse = await PostAsync(client, $"/api/v1/my-cleanings/{helpCleaning.Id}/waiting-help", null, housekeeperToken);
        helpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await helpResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("WaitingHelp");
    }

    [Fact]
    public async Task Self_service_start_by_a_housekeeper_not_assigned_to_the_cleaning_returns_404_never_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperA = await SeedHousekeeperUserAsync(tenantId);
        var housekeeperB = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperA, adminToken);

        var housekeeperBToken = await GenerateTokenAsync(host, housekeeperB, tenantId, ["HOUSEKEEPER"]);
        var startResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperBToken);

        startResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The same fail-closed guarantee as
    /// <see cref="MyCleanings_getById_across_tenants_returns_404_never_403"/>,
    /// but for a mutating self-service endpoint rather than a read — proves
    /// RLS/OwnCleaningLoader reject a cross-tenant write attempt identically
    /// to a cross-tenant read, never merely relying on the read path having
    /// been checked once.
    /// </summary>
    [Fact]
    public async Task Self_service_start_across_tenants_returns_404_never_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);

        var otherTenantId = Guid.NewGuid();
        var otherHousekeeperUserId = await SeedHousekeeperUserAsync(otherTenantId);
        var otherTenantToken = await GenerateTokenAsync(host, otherHousekeeperUserId, otherTenantId, ["HOUSEKEEPER"]);

        var startResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, otherTenantToken);

        startResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Self_service_delay_on_a_Completed_cleaning_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);

        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start-inspection", null, housekeeperToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/complete", null, housekeeperToken);

        var delayResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/delay", null, housekeeperToken);

        delayResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Occurrences (Fase 6, Incremento 2A) ----

    [Fact]
    public async Task Register_and_list_occurrence_for_own_cleaning_succeeds()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperToken);

        var registerResponse = await PostAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/occurrences",
            new RegisterCleaningOccurrenceRequest("Damage", "Broken lamp in the living room"), housekeeperToken);

        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var registered = await registerResponse.Content.ReadFromJsonAsync<CleaningOccurrenceResponse>(JsonWebDefaults);
        registered!.Type.Should().Be("Damage");
        registered.Description.Should().Be("Broken lamp in the living room");
        registered.RegisteredByUserId.Should().Be(housekeeperUserId);

        var listResponse = await GetAsync(client, $"/api/v1/my-cleanings/{created.Id}/occurrences", housekeeperToken);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var occurrences = await listResponse.Content.ReadFromJsonAsync<CleaningOccurrenceResponse[]>(JsonWebDefaults);
        occurrences.Should().ContainSingle(o => o.Id == registered.Id);
    }

    [Fact]
    public async Task Register_occurrence_by_a_housekeeper_not_assigned_to_the_cleaning_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperA = await SeedHousekeeperUserAsync(tenantId);
        var housekeeperB = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperA, adminToken);

        var housekeeperBToken = await GenerateTokenAsync(host, housekeeperB, tenantId, ["HOUSEKEEPER"]);
        var registerResponse = await PostAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/occurrences",
            new RegisterCleaningOccurrenceRequest("Noise", null), housekeeperBToken);

        registerResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Register_occurrence_with_an_invalid_type_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);

        var registerResponse = await PostAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/occurrences",
            new RegisterCleaningOccurrenceRequest("NotARealType", null), housekeeperToken);

        registerResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_occurrence_on_a_Completed_cleaning_returns_409()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start-inspection", null, housekeeperToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/complete", null, housekeeperToken);

        var registerResponse = await PostAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/occurrences",
            new RegisterCleaningOccurrenceRequest("Theft", null), housekeeperToken);

        registerResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Checklist (Fase 6, Incremento 2A) ----

    [Fact]
    public async Task Checklist_starts_with_all_8_items_unchecked_and_toggling_one_persists_it()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperToken);

        var initialResponse = await GetAsync(client, $"/api/v1/my-cleanings/{created.Id}/checklist", housekeeperToken);
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var initialItems = await initialResponse.Content.ReadFromJsonAsync<CleaningChecklistItemResponse[]>(JsonWebDefaults);
        initialItems.Should().HaveCount(8);
        initialItems.Should().OnlyContain(i => !i.IsChecked && i.UpdatedByUserId == null);

        var setResponse = await PutAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/checklist/Stove", new SetChecklistItemRequest(true), housekeeperToken);
        setResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var setItem = await setResponse.Content.ReadFromJsonAsync<CleaningChecklistItemResponse>(JsonWebDefaults);
        setItem!.IsChecked.Should().BeTrue();
        setItem.UpdatedByUserId.Should().Be(housekeeperUserId);

        var afterResponse = await GetAsync(client, $"/api/v1/my-cleanings/{created.Id}/checklist", housekeeperToken);
        var afterItems = await afterResponse.Content.ReadFromJsonAsync<CleaningChecklistItemResponse[]>(JsonWebDefaults);
        afterItems.Should().ContainSingle(i => i.ItemType == "Stove" && i.IsChecked);
        afterItems.Should().Contain(i => i.ItemType != "Stove" && !i.IsChecked);
    }

    [Fact]
    public async Task Checklist_does_not_block_Complete_when_no_item_is_checked()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start", null, housekeeperToken);
        await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/start-inspection", null, housekeeperToken);

        var completeResponse = await PostAsync(client, $"/api/v1/my-cleanings/{created.Id}/complete", null, housekeeperToken);

        completeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await completeResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task Set_checklist_item_by_a_housekeeper_not_assigned_to_the_cleaning_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperA = await SeedHousekeeperUserAsync(tenantId);
        var housekeeperB = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperA, adminToken);

        var housekeeperBToken = await GenerateTokenAsync(host, housekeeperB, tenantId, ["HOUSEKEEPER"]);
        var setResponse = await PutAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/checklist/Window", new SetChecklistItemRequest(true), housekeeperBToken);

        setResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Set_checklist_item_with_an_invalid_item_type_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        await SeedActivePropertyProjectionAsync(tenantId, propertyId);
        var housekeeperUserId = await SeedHousekeeperUserAsync(tenantId);
        var adminToken = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var housekeeperToken = await GenerateTokenAsync(host, housekeeperUserId, tenantId, ["HOUSEKEEPER"]);
        var created = await CreateAndAssignCleaningAsync(client, propertyId, housekeeperUserId, adminToken);

        var setResponse = await PutAsync(
            client, $"/api/v1/my-cleanings/{created.Id}/checklist/NotARealItem", new SetChecklistItemRequest(true), housekeeperToken);

        setResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<CleaningDetailResponse> CreateAndAssignCleaningAsync(
        HttpClient client, Guid propertyId, Guid housekeeperUserId, string adminToken)
    {
        var createResponse = await PostAsync(client, "/api/v1/cleanings", new CreateCleaningRequest(propertyId, null), adminToken);
        var created = await createResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults);

        var assignResponse = await PostAsync(
            client, $"/api/v1/cleanings/{created!.Id}/assign", new AssignCleaningRequest(housekeeperUserId), adminToken);
        return (await assignResponse.Content.ReadFromJsonAsync<CleaningDetailResponse>(JsonWebDefaults))!;
    }

    // ---- Helpers ----

    private static async Task SetTenantAsync(HousekeepingDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static async Task SetIdentityTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private static HousekeepingDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<HousekeepingDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "housekeeping"))
            .Options;

        return new HousekeepingDbContext(options, tenantContext);
    }

    private static IdentityDbContext CreateIdentityDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }
}
