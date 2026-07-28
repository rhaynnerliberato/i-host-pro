using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Authentication;
using IHostPro.Contexts.Identity.Infrastructure.Caching;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// End-to-end test of JWT Bearer authentication (Incremento 2 plan, Etapa
/// 13; ADR-012), exercising the real <c>AddAuthentication</c> +
/// <c>AddJwtBearer</c> + <c>AddAuthorization</c> pipeline (in the exact
/// <c>UseAuthentication</c> -&gt; <c>UseAuthorization</c> order
/// <c>IHostPro.Api</c>'s Program.cs uses) via <c>Microsoft.AspNetCore.TestHost</c>
/// — test infrastructure only, no controllers/endpoints are added to any
/// production project. No PostgreSQL container is needed: JWT Bearer
/// validation never touches it (only <see cref="ConfigurationJwtSigningKeyProvider"/>
/// and, for revocation, Redis).
/// </summary>
public class JwtBearerAuthenticationTests : IClassFixture<JwtBearerAuthenticationTests.Fixture>, IDisposable
{
    private const string Issuer = "https://identity.ihostpro.test";
    private const string Audience = "ihostpro-api-test";
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly RedisContainer _redisContainer;
    private readonly RSA _signingKey;
    private readonly string _signingKeyPem;
    private readonly string _kid;

    public JwtBearerAuthenticationTests(Fixture fixture)
    {
        _redisContainer = fixture.RedisContainer;

        // Deliberately NOT shared via Fixture, unlike the container — a fresh
        // key per test method (xUnit creates a new test class instance per
        // [Fact] even under IClassFixture, so the constructor still runs once
        // per test). Confirmed by this exact regression during Etapa 15A's
        // fixture-sharing stabilization: sharing one RSA instance's PEM across
        // many independently-built/disposed IHost instances (each importing
        // it into its own ConfigurationJwtSigningKeyProvider) reintroduced the
        // documented Windows CNG native-handle-sharing bug (see
        // LoginCommandHandlerTests' BuildServices doc comment) — tokens
        // stopped validating once an earlier test's host disposed its copy of
        // the "same" key.
        _signingKey = RSA.Create(2048);
        _signingKeyPem = _signingKey.ExportRSAPrivateKeyPem();
        _kid = ComputeKid(_signingKey);
    }

    public void Dispose() => _signingKey.Dispose();

    /// <summary>
    /// Started once per test class, not once per test method — see
    /// <see cref="IdentityRowLevelSecurityTests.Fixture"/>'s doc comment for
    /// the full rationale (Etapa 15A stabilization of Docker daemon load).
    /// </summary>
    public sealed class Fixture : IAsyncLifetime
    {
        public RedisContainer RedisContainer { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            RedisContainer = new RedisBuilder().WithImage("redis:7-alpine").Build();
            await RedisContainer.StartAsync();
        }

        public async Task DisposeAsync() => await RedisContainer.DisposeAsync();
    }

    /// <summary>Mirrors ConfigurationJwtSigningKeyProvider's private ComputeKeyId exactly — same deterministic, publicly-documented algorithm.</summary>
    private static string ComputeKid(RSA rsa)
    {
        var publicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(publicKeyInfo);
        return Base64UrlEncoder.Encode(hash);
    }

    // ---- Server ------------------------------------------------------

    private async Task<IHost> BuildHostAsync(string? redisConnectionStringOverride = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["Identity:Jwt:Issuer"] = Issuer,
            ["Identity:Jwt:Audience"] = Audience,
            ["Identity:Jwt:AccessTokenLifetime"] = AccessTokenLifetime.ToString(),
            ["Identity:Jwt:ClockSkew"] = ClockSkew.ToString(),
            ["Identity:Jwt:SigningKey:PrivateKeyPem"] = _signingKeyPem,
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
            ["Identity:SessionRevocationCache:ConnectionString"] =
                redisConnectionStringOverride ?? _redisContainer.GetConnectionString(),
        }).Build();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureAppConfiguration(cfg => cfg.AddConfiguration(configuration));
                webHost.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
                    services.AddIHostProTenantAwarePipeline();
                    services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
                    services.AddIdentityJwtIssuance(configuration);
                    services.AddIdentitySessionRevocationCache(configuration);
                    services.AddIdentityJwtBearerAuthentication();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        // Never gated by RequireAuthorization(): reachable
                        // regardless of authentication outcome, so tests can
                        // inspect ITenantContext/claims even for a rejected
                        // token — test infrastructure only, not a product
                        // endpoint (Incremento 2 plan, Etapa 13: "não
                        // implemente ainda controllers/endpoints").
                        endpoints.MapGet("/inspect", async context =>
                        {
                            var tenantContext = context.RequestServices.GetRequiredService<ITenantContext>();
                            var payload = new InspectResult(
                                context.User.Identity?.IsAuthenticated ?? false,
                                context.User.Identity?.Name,
                                context.User.Claims.Where(c => c.Type == "role").Select(c => c.Value).ToArray(),
                                context.User.Claims.Any(c => c.Type == "permissions"),
                                tenantContext.IsResolved,
                                tenantContext.IsResolved ? tenantContext.TenantId!.Value : null);
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
                        });

                        // Gated: exercises AddAuthorization()/UseAuthorization()
                        // actually enforcing the authenticated-user requirement,
                        // not just AddAuthentication().
                        endpoints.MapGet("/protected", () => "ok").RequireAuthorization();
                    });
                });
            });

        return await hostBuilder.StartAsync();
    }

    private sealed record InspectResult(
        bool IsAuthenticated,
        string? Sub,
        string[] Roles,
        bool HasPermissionsClaim,
        bool TenantContextResolved,
        Guid? TenantId);

    private static async Task<InspectResult> InspectAsync(HttpClient client, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/inspect");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<InspectResult>(body, JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- Token construction --------------------------------------------

    private string BuildToken(
        RSA? signingKey = null,
        string? kid = null,
        bool includeKid = true,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string issuer = Issuer,
        string audience = Audience,
        DateTime? notBefore = null,
        DateTime? expires = null,
        Guid? userId = null,
        Guid? tenantId = null,
        Guid? sessionId = null,
        string? jti = null,
        string[]? roles = null,
        Action<Dictionary<string, object>>? mutateClaims = null)
    {
        var key = signingKey ?? _signingKey;
        var securityKey = new RsaSecurityKey(key);
        if (includeKid)
            securityKey.KeyId = kid ?? ComputeKid(key);

        var credentials = new SigningCredentials(securityKey, algorithm);
        var now = DateTime.UtcNow;

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = (userId ?? Guid.NewGuid()).ToString(),
            ["tenant_id"] = (tenantId ?? Guid.NewGuid()).ToString(),
            ["session_id"] = (sessionId ?? Guid.NewGuid()).ToString(),
            [JwtRegisteredClaimNames.Jti] = jti ?? Guid.NewGuid().ToString(),
            ["role"] = roles ?? ["ADMIN"],
        };
        mutateClaims?.Invoke(claims);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = notBefore ?? now,
            Expires = expires ?? now.Add(AccessTokenLifetime),
            Claims = claims,
            SigningCredentials = credentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    // ---- Tests: valid token ------------------------------------------------

    [Fact]
    public async Task A_valid_token_authenticates_the_request_and_sets_the_tenant_context()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var token = BuildToken(userId: userId, tenantId: tenantId, roles: ["ADMIN", "OPERATOR"]);

        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeTrue();
        result.Sub.Should().Be(userId.ToString());
        result.TenantContextResolved.Should().BeTrue();
        result.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task A_valid_token_is_accepted_by_an_authorization_gated_endpoint()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken();
        var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_missing_token_is_rejected_by_an_authorization_gated_endpoint()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/protected");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- Tests: signature / key / algorithm --------------------------------

    [Fact]
    public async Task A_token_signed_with_a_different_key_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        using var otherKey = RSA.Create(2048);
        var token = BuildToken(signingKey: otherKey);

        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeFalse();
        result.TenantContextResolved.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_no_kid_header_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(includeKid: false);

        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_an_unknown_kid_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(kid: "not-a-registered-key-id");

        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_signed_with_a_non_RS256_algorithm_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        // Same registered key/kid — only the algorithm differs.
        var token = BuildToken(algorithm: SecurityAlgorithms.RsaSha384);

        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeFalse();
    }

    // ---- Tests: issuer / audience / lifetime -------------------------------

    [Fact]
    public async Task A_token_with_the_wrong_issuer_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(issuer: "https://not-the-real-issuer.test");

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_the_wrong_audience_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(audience: "not-the-real-audience");

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var now = DateTime.UtcNow;
        var token = BuildToken(notBefore: now.AddHours(-2), expires: now.AddHours(-1));

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_not_yet_valid_nbf_in_the_future_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var now = DateTime.UtcNow;
        var token = BuildToken(notBefore: now.AddHours(1), expires: now.AddHours(2));

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    // ---- Tests: required claims ---------------------------------------------

    [Fact]
    public async Task A_token_missing_the_tenant_id_claim_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(mutateClaims: claims => claims.Remove("tenant_id"));

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_a_duplicated_sub_claim_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        // A JSON array value for "sub" is encoded by the token handler as two
        // separate Claim entries of the same type once the principal is built
        // — exactly the "duplicated claim" shape TryGetExactlyOneCanonicalGuidClaim guards against.
        var token = BuildToken(mutateClaims: claims => claims[JwtRegisteredClaimNames.Sub] = new[]
        {
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
        });

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_a_malformed_session_id_claim_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        // "N" format (no hyphens) is a valid Guid representation but not the
        // canonical "D" format this system always emits — must be rejected,
        // not leniently re-parsed.
        var token = BuildToken(mutateClaims: claims => claims["session_id"] = Guid.NewGuid().ToString("N"));

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_with_a_non_guid_jti_claim_is_rejected()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(mutateClaims: claims => claims[JwtRegisteredClaimNames.Jti] = "not-a-guid-at-all");

        (await InspectAsync(client, token)).IsAuthenticated.Should().BeFalse();
    }

    // ---- Tests: roles / permissions -----------------------------------------

    [Fact]
    public async Task A_token_with_multiple_roles_exposes_all_of_them_via_IsInRole()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken(roles: ["ADMIN", "OPERATOR", "BILLING"]);

        var result = await InspectAsync(client, token);

        result.Roles.Should().BeEquivalentTo(["ADMIN", "OPERATOR", "BILLING"]);
    }

    [Fact]
    public async Task A_valid_token_never_carries_a_permissions_claim()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var token = BuildToken();

        var result = await InspectAsync(client, token);

        result.HasPermissionsClaim.Should().BeFalse();
    }

    // ---- Tests: session revocation (real Redis) ------------------------------

    [Fact]
    public async Task A_token_for_a_revoked_session_is_rejected_with_real_Redis()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var redis = ConnectionMultiplexer.Connect(_redisContainer.GetConnectionString()).GetDatabase();
        await redis.StringSetAsync($"ihostpro:{tenantId:N}:session-revoked:{sessionId:N}", "1");

        var token = BuildToken(tenantId: tenantId, sessionId: sessionId);
        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeFalse();
        result.TenantContextResolved.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_for_a_non_revoked_session_is_accepted_with_real_Redis()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();

        var token = BuildToken();
        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task A_cryptographically_valid_token_is_accepted_when_Redis_is_unreachable_fail_open()
    {
        using var host = await BuildHostAsync(redisConnectionStringOverride: "127.0.0.1:1,connectTimeout=1000,connectRetry=1");
        using var client = host.GetTestClient();
        var token = BuildToken();

        var result = await InspectAsync(client, token);

        result.IsAuthenticated.Should().BeTrue();
        result.TenantContextResolved.Should().BeTrue();
    }

    // ---- Tests: no leakage across requests -----------------------------------

    [Fact]
    public async Task Tenant_context_never_leaks_between_sequential_requests()
    {
        using var host = await BuildHostAsync();
        using var client = host.GetTestClient();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var resultA = await InspectAsync(client, BuildToken(tenantId: tenantA));
        var resultB = await InspectAsync(client, BuildToken(tenantId: tenantB));
        var resultAnonymous = await InspectAsync(client, token: null);

        resultA.TenantId.Should().Be(tenantA);
        resultB.TenantId.Should().Be(tenantB);
        resultAnonymous.IsAuthenticated.Should().BeFalse();
        resultAnonymous.TenantContextResolved.Should().BeFalse();
    }

    // ---- Tests: Worker never gets JWT Bearer / signing key / Redis ------------

    [Fact]
    public void IHostPro_Worker_configuration_never_registers_JwtBearer_signing_key_or_real_Redis()
    {
        // Mirrors exactly what IHostPro.Worker's Program.cs calls: only
        // AddIdentityModule — never AddIdentityJwtIssuance,
        // AddIdentitySessionRevocationCache, or AddIdentityJwtBearerAuthentication
        // (Incremento 2 plan, Etapa 6/12/13).
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
            ["Identity:AccountLockout:MaxFailedAccessAttempts"] = "5",
            ["Identity:AccountLockout:DefaultLockoutDuration"] = "00:05:00",
            ["Identity:AccountLockout:AllowedForNewUsers"] = "true",
            ["Identity:RefreshToken:Lifetime"] = "30.00:00:00",
            ["Identity:RefreshToken:SecretSizeBytes"] = "32",
            ["Identity:RefreshToken:ConcurrentRotationGraceWindow"] = "00:00:10",
        }).Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        services.AddIHostProTenantAwarePipeline();
        services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IJwtSigningKeyProvider>().Should().BeNull();
        scope.ServiceProvider.GetService<IJwtTokenGenerator>().Should().BeNull();
        scope.ServiceProvider.GetService<IAuthenticationSchemeProvider>().Should().BeNull();
        // The harmless no-op default — never the Redis-backed implementation.
        scope.ServiceProvider.GetRequiredService<ISessionRevocationCache>().Should().BeOfType<NullSessionRevocationCache>();
    }
}
