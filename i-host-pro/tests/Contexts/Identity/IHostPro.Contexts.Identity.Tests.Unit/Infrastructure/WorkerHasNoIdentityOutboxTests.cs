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
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// Confirms <c>IHostPro.Worker</c>'s registration (mirrored here exactly —
/// <see cref="PipelineRegistrationExtensions.AddIHostProTenantAwarePipeline"/>
/// + <c>AddIdentityModule</c>, deliberately never
/// <c>AddIdentityCommandDispatch</c>, never <c>EnrollAncillaryPostgresqlOutbox</c>)
/// gives Worker no access to Identity's durable outbox (Incremento 2 plan,
/// Etapa 15A, approved with reservations point 3): neither
/// <see cref="IIdentityTransactionExecutor"/> nor
/// <see cref="IDbContextOutbox{TDbContext}"/> for <see cref="IdentityDbContext"/>
/// resolve from this container — both come only from
/// <c>AddIdentityCommandDispatch</c>/<c>EnrollAncillaryPostgresqlOutbox</c>,
/// which only <c>IHostPro.Api</c>'s Program.cs calls.
/// </summary>
public class WorkerHasNoIdentityOutboxTests
{
    private static ServiceProvider BuildWorkerEquivalentServices()
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

        // Exactly IHostPro.Worker's Program.cs Identity-related registrations
        // — no AddIdentityJwtIssuance, no AddIdentitySessionRevocationCache,
        // no AddIdentityJwtBearerAuthentication, and critically no
        // AddIdentityCommandDispatch (Api-only, per its own doc comment) and
        // no UseWolverine/EnrollAncillaryPostgresqlOutbox call at all (Worker's
        // real UseWolverine only calls UseIHostProRabbitMq + the tenant
        // resolution middleware — neither registers IDbContextOutbox<T>).
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        services.AddIHostProTenantAwarePipeline();
        services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void IIdentityTransactionExecutor_does_not_resolve()
    {
        using var provider = BuildWorkerEquivalentServices();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IIdentityTransactionExecutor>().Should().BeNull();
    }

    [Fact]
    public void IDbContextOutbox_for_IdentityDbContext_does_not_resolve()
    {
        using var provider = BuildWorkerEquivalentServices();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IDbContextOutbox<IdentityDbContext>>().Should().BeNull();
    }

    [Fact]
    public void IRefreshTokenExchangeExecutor_and_ILogoutExecutor_do_not_resolve()
    {
        // Both moved to AddIdentityCommandDispatch in Etapa 15A specifically
        // because they need IIdentityTransactionExecutor — confirming they
        // are absent here is the same guarantee from a different angle.
        using var provider = BuildWorkerEquivalentServices();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IRefreshTokenExchangeExecutor>().Should().BeNull();
        scope.ServiceProvider.GetService<ILogoutExecutor>().Should().BeNull();
    }
}
