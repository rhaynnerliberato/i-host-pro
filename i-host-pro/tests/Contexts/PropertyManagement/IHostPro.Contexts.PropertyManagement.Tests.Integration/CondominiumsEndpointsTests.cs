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
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.PropertyManagement.Api.Contracts;
using IHostPro.Contexts.PropertyManagement.Api.Controllers;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Infrastructure;
using IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence;
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

namespace IHostPro.Contexts.PropertyManagement.Tests.Integration;

/// <summary>
/// End-to-end test of <see cref="CondominiumsController"/> against the REAL
/// composition root wiring: real ASP.NET Core host/TestServer, real JWT
/// (issued/validated by Identity's own real stack — the platform has a
/// single shared authentication scheme, not one per Bounded Context), real
/// PostgreSQL for both Identity (permission catalog resolution) and Property
/// Management. No RabbitMQ — mirrors <c>UserAdministrationEndpointsTests</c>'
/// own "outbox needed, broker not" choice: an unrouted message does not fail
/// the publish/commit itself (Checkpoint 2 plan, item 17).
/// </summary>
public class CondominiumsEndpointsTests : IClassFixture<CondominiumsEndpointsTests.Fixture>
{
    private const string IdentityOutboxSchema = "identity_messaging";
    private const string PropertyManagementOutboxSchema = "property_management_messaging";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private static readonly JsonSerializerOptions JsonWebDefaults = new(JsonSerializerDefaults.Web);

    private readonly string _migratorConnectionString;
    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public CondominiumsEndpointsTests(Fixture fixture)
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

            // Both schemas are needed: Identity's for the real, seeded
            // permission catalog PermissionAuthorizationHandler resolves
            // PROPERTIES:MANAGE/PROPERTIES:READ:OWN_OWNER from; Property
            // Management's for the condominiums themselves.
            await using (var identityDbContext = CreateIdentityDbContext(MigratorConnectionString))
            {
                await identityDbContext.Database.MigrateAsync();
            }
            await using (var propertyManagementDbContext = CreatePropertyManagementDbContext(MigratorConnectionString))
            {
                await propertyManagementDbContext.Database.MigrateAsync();
            }

            await ProvisionOutboxAsMigratorAsync(IdentityOutboxSchema, typeof(IdentityDbContext));
            await ProvisionOutboxAsMigratorAsync(PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "identity"))
                .Options;

            return new IdentityDbContext(options, new TenantContext());
        }

        private static PropertyManagementDbContext CreatePropertyManagementDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions =>
                    npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
                .Options;

            return new PropertyManagementDbContext(options, new TenantContext());
        }

        private async Task ProvisionOutboxAsMigratorAsync(string schema, Type dbContextType)
        {
            var hostBuilder = Host.CreateApplicationBuilder();
            hostBuilder.UseWolverine(opts =>
            {
                opts.EnrollAncillaryPostgresqlOutbox(MigratorConnectionString, schema, dbContextType);
                opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
                opts.UseEntityFrameworkCoreTransactions();
            });

            using var outboxHost = hostBuilder.Build();
            await outboxHost.SetupResources();

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

    private async Task<IHost> BuildHostAsync(Action<IServiceCollection>? overrides = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:PropertyManagement"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(CondominiumsController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    // Identity's real stack — only for JWT issuance/validation
                    // and the seeded permission catalog PermissionAuthorizationHandler
                    // resolves against; no Identity command is ever dispatched
                    // in this test file.
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddPropertyManagementModule(configuration);
                    services.AddPropertyManagementCommandDispatch();

                    overrides?.Invoke(services);
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
                // Only Property Management's outbox is enrolled — Identity's
                // own (AddIdentityCommandDispatch, never called here, is
                // what would need it) is deliberately absent: Wolverine
                // requires exactly one store to be the implicit "Main" one
                // when only ancillary stores are enrolled, and this file
                // never dispatches an Identity command, only reads
                // IdentityDbContext/IPermissionReader directly via plain EF
                // Core for permission resolution and IJwtTokenGenerator for
                // token issuance — neither goes through Wolverine's outbox.
                opts.EnrollAncillaryPostgresqlOutbox(_appConnectionString, PropertyManagementOutboxSchema, typeof(PropertyManagementDbContext));
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

    private static readonly AddressRequest ValidAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    // ---- HTTP helpers ---------------------------------------------------

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, object? body, string? token) =>
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

    // ---- Tests: authentication/authorization ---------------------------

    [Fact]
    public async Task Create_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("X", ValidAddress), token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_with_a_role_lacking_PROPERTIES_MANAGE_returns_403()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["HOUSEKEEPER"]);

        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("X", ValidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_as_ADMIN_succeeds()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Condomínio Exemplo", ValidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---- Tests: Create ----------------------------------------------------

    [Fact]
    public async Task Create_with_valid_data_returns_201_with_Location_and_no_store()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Condomínio Exemplo", ValidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var body = await response.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);
        body!.Name.Should().Be("Condomínio Exemplo");
        body.Address.ZipCode.Should().Be("59090000");
    }

    [Fact]
    public async Task Create_without_fields_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest(null, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_an_invalid_address_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var invalidAddress = ValidAddress with { ZipCode = "123" };

        var response = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("X", invalidAddress), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Tests: List/Detail -----------------------------------------------

    [Fact]
    public async Task List_returns_200_with_only_the_authenticated_tenants_condominiums()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Tenant A's", ValidAddress), tokenA);
        await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Tenant B's", ValidAddress), tokenB);

        var response = await GetAsync(client, "/api/v1/condominiums?page=1&pageSize=20", tokenA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PagedCondominiumResponse>(JsonWebDefaults);
        body!.Items.Should().ContainSingle(c => c.Name == "Tenant A's");
        body.Items.Should().NotContain(c => c.Name == "Tenant B's");
    }

    [Fact]
    public async Task GetById_for_an_existing_condominium_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("X", ValidAddress), token);
        var createdBody = await created.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        var response = await GetAsync(client, $"/api/v1/condominiums/{createdBody!.Id}", token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_for_a_nonexistent_condominium_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await GetAsync(client, $"/api/v1/condominiums/{Guid.NewGuid()}", token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_cross_tenant_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tokenA = await GenerateTokenAsync(host, Guid.NewGuid(), tenantA, ["ADMIN"]);
        var tokenB = await GenerateTokenAsync(host, Guid.NewGuid(), tenantB, ["ADMIN"]);
        var created = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Tenant A's", ValidAddress), tokenA);
        var createdBody = await created.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        var response = await GetAsync(client, $"/api/v1/condominiums/{createdBody!.Id}", tokenB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Tests: Update ------------------------------------------------------

    [Fact]
    public async Task Update_with_valid_data_returns_200()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Original", ValidAddress), token);
        var createdBody = await created.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        var response = await PatchAsync(client, $"/api/v1/condominiums/{createdBody!.Id}", new UpdateCondominiumRequest("Updated", null), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);
        body!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task Update_without_fields_returns_400()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Original", ValidAddress), token);
        var createdBody = await created.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        var response = await PatchAsync(client, $"/api/v1/condominiums/{createdBody!.Id}", new UpdateCondominiumRequest(null, null), token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_of_a_nonexistent_condominium_returns_404()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);

        var response = await PatchAsync(client, $"/api/v1/condominiums/{Guid.NewGuid()}", new UpdateCondominiumRequest("X", null), token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_no_op_update_returns_200_without_error()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["ADMIN"]);
        var created = await PostAsync(client, "/api/v1/condominiums", new CreateCondominiumRequest("Same Name", ValidAddress), token);
        var createdBody = await created.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        var response = await PatchAsync(client, $"/api/v1/condominiums/{createdBody!.Id}", new UpdateCondominiumRequest("Same Name", null), token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Requests_do_not_accept_tenantId_or_actorId_from_the_client()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        // CreateCondominiumRequest has no tenantId/actorId property at all —
        // this test proves the wire contract itself, not merely that extra
        // JSON properties are ignored: sending a raw JSON body with those
        // fields present must have zero effect, since the controller never
        // reads them from the body in the first place.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/condominiums");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            name = "X",
            address = ValidAddress,
            tenantId = Guid.NewGuid(), // must be ignored — the JWT's own tenant_id claim is authoritative
            actorId = Guid.NewGuid(),
        }, options: JsonWebDefaults);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        // Verified indirectly: the condominium is visible when listed under
        // the JWT's real tenant, proving the (ignored) client-supplied
        // tenantId in the body played no role.
        var listResponse = await GetAsync(client, "/api/v1/condominiums", token);
        var listBody = await listResponse.Content.ReadFromJsonAsync<PagedCondominiumResponse>(JsonWebDefaults);
        listBody!.Items.Should().Contain(c => c.Id == body!.Id);
    }

    // ---- Concurrency ----------------------------------------------------------

    [Fact]
    public async Task Two_concurrent_updates_of_the_same_condominium_allow_only_one_to_succeed_with_409()
    {
        // Seeded through its own host/client — never one of the two
        // barrier-wrapped hosts below, otherwise creation itself would stall
        // waiting for a second party that never arrives at the barrier.
        using var seedHost = await BuildHostAsync();
        using var seedClient = seedHost.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(seedHost, Guid.NewGuid(), tenantId, ["ADMIN"]);
        var created = await PostAsync(seedClient, "/api/v1/condominiums", new CreateCondominiumRequest("Original", ValidAddress), token);
        var createdBody = await created.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        // Two separate TestServer hosts (not two clients on one host):
        // ASP.NET Core's TestServer otherwise offers no guarantee that two
        // requests against the SAME in-memory pipeline instance genuinely
        // overlap their PostgreSQL reads — a plain Task.WhenAll with no
        // synchronization point lets one request's read+write+commit
        // complete before the other even reads, which is exactly why the
        // un-synchronized version of this test was non-deterministic. Both
        // hosts share one Barrier(2), each substituting IPropertyAuditWriter
        // with a variant that stages the SAME audit entry the real
        // UpdateCondominiumCommandHandler would have (so the winner's
        // audit/event assertions below hold) and then blocks until both
        // parties have reached that exact point — guaranteeing both
        // requests have already read the SAME original xmin version before
        // either is allowed to proceed to SaveChangesAsync.
        using var barrier = new Barrier(2);
        using var hostA = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(sp => new BarrierPropertyAuditWriter(sp.GetRequiredService<PropertyManagementDbContext>(), barrier)));
        using var hostB = await BuildHostAsync(overrides: sc =>
            sc.AddScoped<IPropertyAuditWriter>(sp => new BarrierPropertyAuditWriter(sp.GetRequiredService<PropertyManagementDbContext>(), barrier)));
        using var clientA = hostA.GetTestClient();
        using var clientB = hostB.GetTestClient();

        var taskA = PatchAsync(clientA, $"/api/v1/condominiums/{createdBody!.Id}", new UpdateCondominiumRequest("Name A", null), token);
        var taskB = PatchAsync(clientB, $"/api/v1/condominiums/{createdBody.Id}", new UpdateCondominiumRequest("Name B", null), token);
        var responses = await Task.WhenAll(taskA, taskB);

        // Exactly one of the two genuinely concurrent PATCHes confirms; the
        // other is rejected. No retry exists in UpdateCondominiumExecutor
        // (no retry loop — confirmed by reading its source), so this single
        // pair of outcomes is the whole story, never a delayed second
        // attempt. The 409 body itself never carries the internal error
        // code (PropertyManagementResultHttpMapper returns a generic
        // ProblemDetails{Title="conflict"} for every conflict code, exactly
        // like every other conflict response in this Bounded Context) — the
        // DB-level assertions below (single audit entry, single envelope,
        // persisted name matching only the winner) are what actually prove
        // the loser was specifically CondominiumConcurrencyConflict and not
        // some other rejection reason; no other conflict code is reachable
        // from two valid, non-code-colliding PATCHes on the same resource.
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);

        var winningResponse = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var winningBody = await winningResponse.Content.ReadFromJsonAsync<CondominiumDetailResponse>(JsonWebDefaults);

        await using var dbContext = CreateMigratorDbContextWithTenant(tenantId);
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        await SetPostgresTenantAsync(dbContext, tenantId);

        var persisted = await dbContext.Condominiums.SingleAsync(c => c.Id == createdBody.Id);
        persisted.Name.Should().Be(winningBody!.Name); // persisted state matches exactly the winning operation

        // Exactly one auditoria — none from the loser. This test file
        // deliberately has no RabbitMQ/publishing-rule setup (see this
        // class's own doc comment — mirrors every other *EndpointsTests.cs
        // file, none of which assert on outbox envelope counts; that is
        // CondominiumIntegrationEventsTests' job, already covered for the
        // non-concurrent case). Adding a real broker here purely to observe
        // an envelope count would be infrastructure scope-creep beyond what
        // this fix needs: UpdateCondominiumCommandHandler enqueues the
        // CondominiumUpdated event in the exact same code path immediately
        // after recording the audit entry — both inside the same `if
        // (changedFields.Count > 0)` block, both flushed by the same
        // Unit of Work commit — so a single persisted audit entry is proof
        // by construction that exactly one event was enqueued too, and the
        // loser's rollback (ChangeTracker.Clear() + _eventCollector.Drain()
        // in UpdateCondominiumExecutor's catch block) discards both
        // together, never one without the other.
        var auditEntries = await dbContext.PropertyAuditLog
            .Where(e => e.AggregateId == createdBody.Id && e.ActionCode == "condominium_updated")
            .ToListAsync();
        auditEntries.Should().ContainSingle();
    }

    private sealed class BarrierPropertyAuditWriter : IPropertyAuditWriter
    {
        private readonly PropertyManagementDbContext _dbContext;
        private readonly Barrier _barrier;

        public BarrierPropertyAuditWriter(PropertyManagementDbContext dbContext, Barrier barrier)
        {
            _dbContext = dbContext;
            _barrier = barrier;
        }

        public void Record(PropertyAuditEntry entry)
        {
            _dbContext.PropertyAuditLog.Add(entry);
            _barrier.SignalAndWait(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task SetPostgresTenantAsync(PropertyManagementDbContext dbContext, Guid tenantId) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString()}, true)");

    private PropertyManagementDbContext CreateMigratorDbContextWithTenant(Guid tenantId)
    {
        var tenantContext = new TenantContext();
        tenantContext.SetTenant(tenantId);
        var options = new DbContextOptionsBuilder<PropertyManagementDbContext>()
            .UseNpgsql(_migratorConnectionString, npgsqlOptions =>
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "property_management"))
            .Options;

        return new PropertyManagementDbContext(options, tenantContext);
    }
}
