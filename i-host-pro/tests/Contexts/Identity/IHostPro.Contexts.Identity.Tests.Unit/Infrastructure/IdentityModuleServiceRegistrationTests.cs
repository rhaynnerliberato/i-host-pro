using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// Structural regression test: <c>WebApplication.CreateBuilder</c> enables
/// <c>ValidateScopes</c>/<c>ValidateOnBuild</c> by default in Development, so
/// any Singleton that captively depends on a Scoped service throws at
/// startup. This once applied to <c>DummyPasswordVerifier</c> (Singleton),
/// which depended directly on the Scoped <c>IPasswordHasher&lt;User&gt;</c> —
/// found while confirming it uses the effectively configured Argon2id
/// parameters at runtime (Incremento 2 plan, Etapa 9 -&gt; 10 pendência 2),
/// fixed by resolving the hasher through a short-lived
/// <c>IServiceScopeFactory</c> scope instead. This test builds the module's
/// full DI graph with the same validation the real host applies, without
/// requiring a live PostgreSQL connection — <c>ValidateOnBuild</c> only
/// inspects the dependency graph, it never opens the connection.
/// </summary>
public class IdentityModuleServiceRegistrationTests
{
    private static IServiceCollection BuildRegisteredServices()
    {
        using var signingKey = RSA.Create(2048);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identity"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
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
        }).Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        services.AddIHostProTenantAwarePipeline();
        services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        services.AddIdentityJwtIssuance(configuration);

        return services;
    }

    [Fact]
    public void AddIdentityModule_and_AddIdentityJwtIssuance_produce_a_scope_valid_container()
    {
        var services = BuildRegisteredServices();

        var act = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });

        act.Should().NotThrow();
    }
}
