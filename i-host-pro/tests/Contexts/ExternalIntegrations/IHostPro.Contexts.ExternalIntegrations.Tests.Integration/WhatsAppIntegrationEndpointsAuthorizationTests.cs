using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.ExternalIntegrations.Api.Controllers;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure;
using IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IHostPro.Contexts.ExternalIntegrations.Tests.Integration;

/// <summary>
/// Fase 9, Checkpoint 2.3.2.2: proves <see cref="WhatsAppIntegrationController"/>
/// against the REAL ASP.NET Core HTTP + authorization pipeline (real JWT
/// bearer validation, real <c>INTEGRATIONS:MANAGE</c> policy resolution, real
/// <see cref="Microsoft.AspNetCore.Authorization.IAuthorizationService"/>) —
/// the exact gap that let <c>IdentityAuthorizationExtensions</c> ship without
/// registering that policy for months without any test catching it (every
/// prior test of this controller called its C# methods directly, never
/// through real HTTP authorization middleware). Mirrors
/// <c>TemplatesEndpointsTests</c>' Fixture/host-building structure, simplified:
/// <see cref="ConfigureWhatsAppIntegrationCommandHandler"/>'s command goes
/// through the plain EF Core <c>TenantTransactionBehavior</c>, never
/// Wolverine's outbox — this controller's endpoints publish no Integration
/// Event, so no Wolverine/RabbitMQ wiring is needed here at all.
/// </summary>
public class WhatsAppIntegrationEndpointsAuthorizationTests : IClassFixture<WhatsAppIntegrationEndpointsAuthorizationTests.Fixture>
{
    private const string IdentitySchema = "identity";
    private const string ExternalIntegrationsSchema = "external_integrations";
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";

    private readonly string _appConnectionString;
    private readonly string _signingKeyPem;

    public WhatsAppIntegrationEndpointsAuthorizationTests(Fixture fixture)
    {
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
            await using (var externalIntegrationsDbContext = CreateExternalIntegrationsDbContext(MigratorConnectionString))
                await externalIntegrationsDbContext.Database.MigrateAsync();
        }

        public async Task DisposeAsync() => await _container.DisposeAsync();

        private static IdentityDbContext CreateIdentityDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<IdentityDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", IdentitySchema))
                .Options;
            return new IdentityDbContext(options, new TenantContext());
        }

        private static ExternalIntegrationsDbContext CreateExternalIntegrationsDbContext(string connectionString)
        {
            var options = new DbContextOptionsBuilder<ExternalIntegrationsDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", ExternalIntegrationsSchema))
                .Options;
            return new ExternalIntegrationsDbContext(options, new TenantContext());
        }
    }

    // ---- Server ---------------------------------------------------------

    private async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = _appConnectionString,
            ["ConnectionStrings:ExternalIntegrations"] = _appConnectionString,
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
                    services.AddControllers().AddApplicationPart(typeof(WhatsAppIntegrationController).Assembly);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();

                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                    services.AddIdentityAuthorization();

                    services.AddExternalIntegrationsModule(configuration, isDevelopmentEnvironment: false);
                    services.AddExternalIntegrationsCommandDispatch();
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

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string route, object? body, string? token) =>
        SendAsync(client, HttpMethod.Put, route, body, token);

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string route, string? token) =>
        SendAsync(client, HttpMethod.Get, route, null, token);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string route, object? body, string? token)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Configure_without_a_token_returns_401()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await PutAsync(client, "/api/v1/integrations/whatsapp",
            new { WabaId = "waba-1", PhoneNumberId = "phone-1", AccessTokenSecretReference = "ref" }, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Configure_with_a_role_lacking_INTEGRATIONS_MANAGE_returns_403_never_500()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), Guid.NewGuid(), ["AI_AGENT"]);

        var response = await PutAsync(client, "/api/v1/integrations/whatsapp",
            new { WabaId = "waba-1", PhoneNumberId = "phone-1", AccessTokenSecretReference = "ref" }, token);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a role without INTEGRATIONS:MANAGE must be cleanly denied — never the 500 the unregistered policy used to cause for every caller");
    }

    [Fact]
    public async Task Configure_as_ADMIN_succeeds_and_atomically_creates_the_integration_and_its_route()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var token = await GenerateTokenAsync(host, Guid.NewGuid(), tenantId, ["ADMIN"]);

        var response = await PutAsync(client, "/api/v1/integrations/whatsapp", new
        {
            WabaId = "waba-atomic-test",
            PhoneNumberId = "phone-atomic-test",
            AccessTokenSecretReference = "SomeReference",
        }, token);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an ADMIN caller must pass real policy resolution and reach the real command handler");

        var body = await response.Content.ReadFromJsonAsync<ConfigureResponseShape>();
        body!.TenantId.Should().Be(tenantId);
        body.IsEnabled.Should().BeFalse("no endpoint may ever activate real sending — CP2.1 mandate §18");
        body.AccessTokenConfigured.Should().BeTrue();

        var getResponse = await GetAsync(client, "/api/v1/integrations/whatsapp", token);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var getBody = await getResponse.Content.ReadFromJsonAsync<ConfigureResponseShape>();
        getBody!.PhoneNumberId.Should().Be("phone-atomic-test",
            "the same request must have atomically persisted the WhatsAppIntegration");
    }

    private sealed record ConfigureResponseShape(
        Guid TenantId, string? WabaId, string? PhoneNumberId, bool IsEnabled,
        bool AccessTokenConfigured, bool AppSecretConfigured, bool VerifyTokenConfigured,
        DateTimeOffset? CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);
}
