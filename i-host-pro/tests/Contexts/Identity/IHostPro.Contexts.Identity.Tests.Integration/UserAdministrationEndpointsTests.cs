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
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Api.Contracts;
using IHostPro.Contexts.Identity.Api.Controllers;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Domain;
using IHostPro.Contexts.Identity.Domain.ValueObjects;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of the three administrative user endpoints (Incremento 3,
/// Checkpoint 5) — <see cref="UserAdministrationController"/> — against the
/// REAL composition root wiring: real host, real JWT, real PostgreSQL,
/// Identity's real outbox (CreateUser publishes two events) — no Redis
/// (Section 10: "não há Redis neste fluxo"), no RabbitMQ (mirrors
/// <see cref="AuthEndpointsTests"/>'s "outbox needed, broker not" choice —
/// an unrouted message does not fail the publish/commit itself).
/// </summary>
public class UserAdministrationEndpointsTests : IClassFixture<UserAdministrationEndpointsTests.Fixture>
{
    private const string OutboxSchema = "identity_messaging";
    private const string KnownPassword = "Correct-Horse-Battery-Staple-42!";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public UserAdministrationEndpointsTests(Fixture fixture)
    {
        _migratorConnectionString = fixture.MigratorConnectionString;
        _appConnectionString = fixture.AppConnectionString;

        // Deliberately NOT shared via Fixture — see JwtBearerAuthenticationTests'
        // constructor doc comment for the full Windows CNG native-handle-sharing
        // rationale (Etapa 15A stabilization).
        using var signingKey = RSA.Create(2048);
        _signingKeyPem = signingKey.ExportRSAPrivateKeyPem();
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private const string AppRolePassword = "test_app_password";
        private const string MigratorRolePassword = "test_migrator_password";

        private PostgreSqlContainer _postgresContainer = null!;
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
            MigratorConnectionString = builder.ConnectionString;
            builder.Username = "ihostpro_app";
            builder.Password = AppRolePassword;
            AppConnectionString = builder.ConnectionString;

            await using var migratorDbContext = CreateDbContext(MigratorConnectionString, new TenantContext());
            await migratorDbContext.Database.MigrateAsync();

            await ProvisionOutboxAsMigratorAsync();
        }

        public async Task DisposeAsync() => await _postgresContainer.DisposeAsync();

        private async Task ProvisionOutboxAsMigratorAsync()
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

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

    // ---- Server ------------------------------------------------------

    private async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(UserAdministrationController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();
                    services.AddIdentityCommandDispatch();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .UseWolverine((Wolverine.WolverineOptions opts) =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, OutboxSchema, typeof(IdentityDbContext));
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

        return await hostBuilder.StartAsync();
    }

    // ---- Seeding --------------------------------------------------------

    private async Task<Guid> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(
            tenantId, TenantSlug.Create($"tenant-{Guid.NewGuid():N}"[..20]), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return tenant.Id;
    }

    /// <summary>
    /// Creates a real, persisted User in the tenant — <see cref="UserRole.AssignedByUserId"/>
    /// carries a real foreign key to <c>users(tenant_id, id)</c> (confirmed
    /// empirically), so the JWT <c>sub</c> claim used as the actor for
    /// <c>POST /users</c> must correspond to an actually-persisted row. Its
    /// role claim for the token is supplied independently by
    /// <see cref="GenerateTokenAsync"/> — a caller does not need a persisted
    /// <see cref="UserRole"/> row of their own for <c>PermissionAuthorizationHandler</c>
    /// to authorize them; only the JWT's own <c>role</c> claims matter for
    /// that (Incremento 3, Checkpoint 2).
    /// </summary>
    private async Task<Guid> SeedActorUserAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var actor = User.Register(
            Guid.NewGuid(), tenantId, Email.Create($"{Guid.NewGuid():N}@ihostpro.com"), "Admin Actor", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(actor);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return actor.Id;
    }

    private static async Task<string> GenerateTokenAsync(IHost host, Guid userId, Guid tenantId, string[] roles)
    {
        using var scope = host.Services.CreateScope();
        var generator = scope.ServiceProvider.GetRequiredService<IJwtTokenGenerator>();

        var request = new JwtAccessTokenRequest(UserId: userId, TenantId: tenantId, SessionId: Guid.NewGuid(), Roles: roles);

        return generator.GenerateAccessToken(request).Token;
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object body, string? token) =>
        SendAsync(client, HttpMethod.Post, route, body, token);

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string route, object body, string? token) =>
        SendAsync(client, HttpMethod.Patch, route, body, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonWebDefaults);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string tenantSlug, string email, string password = KnownPassword) =>
        PostAsync(client, "/api/v1/auth/login", new { tenantSlug, email, password }, token: null);

    /// <summary>
    /// Only <see cref="Update_after_changing_email_a_new_login_uses_it_the_old_email_stops_working_and_the_existing_session_stays_valid"/>
    /// needs the tenant's slug (for <c>POST /api/v1/auth/login</c>'s body) —
    /// a separate helper rather than changing <see cref="SeedTenantAsync"/>'s
    /// return shape, which every other test in this file already depends on.
    /// </summary>
    private async Task<(Guid TenantId, string Slug)> SeedTenantWithSlugAsync()
    {
        var tenantId = Guid.NewGuid();
        var slug = $"tenant-{Guid.NewGuid():N}"[..20];
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var tenant = Tenant.Provision(tenantId, TenantSlug.Create(slug), "Test Tenant", DateTimeOffset.UtcNow);
        dbContext.Tenants.Add(tenant);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (tenant.Id, slug);
    }

    private async Task<(Guid UserId, string Email)> SeedUserWithEmailAsync(Guid tenantId)
    {
        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var hasher = new Argon2PasswordHasher(new KonsciousArgon2idPrimitive(), Options.Create(new Argon2Options()));
        var hash = PasswordHash.FromEncoded(hasher.HashPassword(null!, KnownPassword));
        var email = $"{Guid.NewGuid():N}@ihostpro.com";
        var user = User.Register(Guid.NewGuid(), tenantId, Email.Create(email), "Test User", hash, DateTimeOffset.UtcNow);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        return (user.Id, email);
    }

    // ---- Tests: 401 without a token -------------------------------------

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "/api/v1/users", new CreateUserRequest("Name", "a@b.com", KnownPassword, "ADMIN"), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task List_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, "/api/v1/users", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetById_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await GetAsync(client, $"/api/v1/users/{Guid.NewGuid()}", token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Tests: authenticated but lacking USERS:MANAGE -> 403 -----------------

    [Fact]
    public async Task Create_with_a_role_lacking_USERS_MANAGE_returns_403()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["HOUSEKEEPER"]);

        var response = await PostAsync(
            client, "/api/v1/users", new CreateUserRequest("Name", "someone@ihostpro.com", KnownPassword, "ADMIN"), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Tests: create -------------------------------------------------------

    [Fact]
    public async Task Create_by_an_Administrator_returns_201_with_correct_Location_and_no_sensitive_data()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        var email = $"{Guid.NewGuid():N}@ihostpro.com";

        var response = await PostAsync(client, "/api/v1/users", new CreateUserRequest("New User", email, KnownPassword, "ADMIN"), token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<UserResponse>(JsonWebDefaults);
        body!.Email.Should().Be(email);
        body.Status.Should().Be("Active");
        body.Roles.Should().Equal("ADMIN");
        response.Headers.Location!.ToString().Should().Contain($"/api/v1/users/{body.Id}");

        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(KnownPassword);
        raw.Should().NotContain("passwordHash", "sensitive internal fields must never reach the wire");
        raw.Should().NotContain("normalizedEmail");
        raw.Should().NotContain(tenantId.ToString(), "tenantId must never appear in the response");
    }

    [Fact]
    public async Task Create_for_a_nonexistent_role_returns_404()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/users",
            new CreateUserRequest("New User", $"{Guid.NewGuid():N}@ihostpro.com", KnownPassword, "NOT_A_REAL_ROLE"), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_for_a_duplicate_email_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        var email = $"{Guid.NewGuid():N}@ihostpro.com";
        var first = await PostAsync(client, "/api/v1/users", new CreateUserRequest("First", email, KnownPassword, "ADMIN"), token);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await PostAsync(client, "/api/v1/users", new CreateUserRequest("Second", email, KnownPassword, "ADMIN"), token);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Responses_carry_no_store_cache_control()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await PostAsync(
            client, "/api/v1/users", new CreateUserRequest("New User", $"{Guid.NewGuid():N}@ihostpro.com", KnownPassword, "ADMIN"), token);

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    // ---- Tests: list -------------------------------------------------------

    [Fact]
    public async Task List_returns_200_with_correct_pagination()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        for (var i = 0; i < 3; i++)
        {
            var create = await PostAsync(
                client, "/api/v1/users", new CreateUserRequest($"User {i}", $"{Guid.NewGuid():N}@ihostpro.com", KnownPassword, "ADMIN"), token);
            create.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var response = await GetAsync(client, "/api/v1/users?page=1&pageSize=2", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedUserResponse>(JsonWebDefaults);
        body!.Page.Should().Be(1);
        body.PageSize.Should().Be(2);
        body.Items.Should().HaveCount(2);
        body.TotalCount.Should().BeGreaterThanOrEqualTo(4); // 3 created here + the seeded actor
    }

    [Fact]
    public async Task List_with_an_invalid_page_size_returns_400()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await GetAsync(client, "/api/v1/users?pageSize=0", token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Tests: get by id -------------------------------------------------

    [Fact]
    public async Task GetById_for_an_existing_user_returns_200()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/users/{actorId}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UserResponse>(JsonWebDefaults);
        body!.Id.Should().Be(actorId);
    }

    [Fact]
    public async Task GetById_for_a_user_belonging_to_a_different_tenant_returns_404()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorA = await SeedActorUserAsync(tenantA);
        var userInTenantB = await SeedActorUserAsync(tenantB);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorA, tenantA, ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/users/{userInTenantB}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: 401/403 for Update ----------------------------------------

    [Fact]
    public async Task Update_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PatchAsync(client, $"/api/v1/users/{Guid.NewGuid()}", new UpdateUserRequest("New Name", null), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_with_a_role_lacking_USERS_MANAGE_returns_403()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["HOUSEKEEPER"]);

        var response = await PatchAsync(client, $"/api/v1/users/{actorId}", new UpdateUserRequest("New Name", null), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Tests: update ------------------------------------------------------

    [Fact]
    public async Task Update_by_an_Administrator_returns_200_with_the_updated_state_and_no_store_header()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await PatchAsync(client, $"/api/v1/users/{actorId}", new UpdateUserRequest("Updated Name", null), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<UserResponse>(JsonWebDefaults);
        body!.FullName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task Update_for_a_user_of_a_different_tenant_returns_404()
    {
        var tenantA = await SeedTenantAsync();
        var tenantB = await SeedTenantAsync();
        var actorA = await SeedActorUserAsync(tenantA);
        var userInTenantB = await SeedActorUserAsync(tenantB);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorA, tenantA, ["ADMIN"]);

        var response = await PatchAsync(client, $"/api/v1/users/{userInTenantB}", new UpdateUserRequest("New Name", null), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_to_an_email_already_used_by_another_user_returns_409()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        var takenEmail = $"{Guid.NewGuid():N}@ihostpro.com";
        (await PostAsync(client, "/api/v1/users", new CreateUserRequest("Other", takenEmail, KnownPassword, "ADMIN"), token))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await PatchAsync(client, $"/api/v1/users/{actorId}", new UpdateUserRequest(null, takenEmail), token);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Update_with_neither_field_returns_400()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await PatchAsync(client, $"/api/v1/users/{actorId}", new UpdateUserRequest(null, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_with_an_invalid_email_returns_400()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);

        var response = await PatchAsync(client, $"/api/v1/users/{actorId}", new UpdateUserRequest(null, "not-an-email"), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_with_no_effective_change_returns_200_without_error()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        var current = await (await GetAsync(client, $"/api/v1/users/{actorId}", token)).Content.ReadFromJsonAsync<UserResponse>(JsonWebDefaults);

        var response = await PatchAsync(client, $"/api/v1/users/{actorId}", new UpdateUserRequest(current!.FullName, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Update_request_body_supplied_tenantId_actorId_roles_status_or_password_are_ignored()
    {
        var tenantId = await SeedTenantAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        // UpdateUserRequest only declares FullName/Email — extra properties in
        // the raw JSON body simply have no corresponding model property to
        // bind to and are ignored.
        var rawBody = JsonContent.Create(new
        {
            fullName = "New Name",
            tenantId = Guid.NewGuid(),
            actorId = Guid.NewGuid(),
            updatedBy = Guid.NewGuid(),
            roles = new[] { "ADMIN" },
            status = "Blocked",
            password = "some-new-password",
        }, options: JsonWebDefaults);
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/users/{actorId}") { Content = rawBody };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UserResponse>(JsonWebDefaults);
        body!.FullName.Should().Be("New Name");
        body.Status.Should().Be("Active"); // untouched — "status" in the body was ignored
        // SeedActorUserAsync persists no UserRole row for this actor (only
        // the JWT's own role CLAIM carries "ADMIN", which is what satisfies
        // the USERS:MANAGE policy check) — so the response's actual Roles is
        // empty, never the body's claimed ["ADMIN"], proving that field was
        // ignored too.
        body.Roles.Should().BeEmpty();
    }

    // ---- Tests: effect on authentication -----------------------------------

    [Fact]
    public async Task Update_after_changing_email_a_new_login_uses_it_the_old_email_stops_working_and_the_existing_session_stays_valid()
    {
        var (tenantId, slug) = await SeedTenantWithSlugAsync();
        var actorId = await SeedActorUserAsync(tenantId);
        var (targetUserId, oldEmail) = await SeedUserWithEmailAsync(tenantId);
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var adminToken = await GenerateTokenAsync(host, actorId, tenantId, ["ADMIN"]);
        // ADMIN, not a lesser role: the endpoint used below to prove the
        // token is "still authenticated" is itself USERS:MANAGE-gated (this
        // file has no self-service, any-authenticated-role endpoint wired
        // in) — the role claim here is only ever used for THAT policy check,
        // never for anything email-related.
        var targetOldToken = await GenerateTokenAsync(host, targetUserId, tenantId, ["ADMIN"]);
        var newEmail = $"{Guid.NewGuid():N}@ihostpro.com";

        var updateResponse = await PatchAsync(
            client, $"/api/v1/users/{targetUserId}", new UpdateUserRequest(null, newEmail), adminToken);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // The access token issued BEFORE the e-mail change is still valid —
        // changing e-mail never rotates SecurityStamp/revokes sessions.
        var stillAuthenticated = await GetAsync(client, $"/api/v1/users/{targetUserId}", targetOldToken);
        stillAuthenticated.StatusCode.Should().Be(HttpStatusCode.OK);

        // The OLD e-mail no longer authenticates a new login.
        var loginWithOldEmail = await LoginAsync(client, slug, oldEmail);
        loginWithOldEmail.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The NEW e-mail authenticates a new login.
        var loginWithNewEmail = await LoginAsync(client, slug, newEmail);
        loginWithNewEmail.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Helpers ----------------------------------------------------------

    private static async Task SetPostgresTenantAsync(IdentityDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private IdentityDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        return CreateDbContext(_migratorConnectionString, tenantContext);
    }

    private static IdentityDbContext CreateDbContext(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
            .Options;

        return new IdentityDbContext(options, tenantContext);
    }
}
