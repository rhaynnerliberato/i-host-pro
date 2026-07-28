using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using JasperFx;
using Mediator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace IHostPro.Contexts.Identity.Tests.Unit.Infrastructure;

/// <summary>
/// Structural regression test for the pipeline-registration correction made
/// in Incremento 2 plan, Etapa 14 (see <see cref="PipelineRegistrationExtensions"/>'s
/// doc comment): a previous version registered
/// <c>TenantBootstrapBehavior&lt;,&gt;</c>/<c>TenantTransactionBehavior&lt;,&gt;</c>
/// as open generics in <c>BuildingBlocks.Infrastructure</c>, which matched
/// every command satisfying their constraints — including
/// <see cref="RefreshTokenCommand"/>/<see cref="LogoutCommand"/>, which need
/// their own specialized tenant-aware step
/// (<see cref="RefreshTokenTenantAwareBehavior"/>/<see cref="LogoutTenantAwareBehavior"/>)
/// instead. Both the open-generic and the specialized behavior would then run
/// for the same command, each independently opening a Unit of Work
/// (<c>NestedUnitOfWorkException</c>). These tests resolve the exact pipeline
/// Mediator would build for each of the three auth commands and assert it is
/// always exactly one validation step plus one tenant-aware step — never more.
/// </summary>
public class IdentityCommandDispatchExtensionsTests
{
    private static IHost BuildHost()
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

        // Etapa 15A: IIdentityTransactionExecutor needs IDbContextOutbox<IdentityDbContext>,
        // which only exists once Wolverine's host bootstraps with Identity's
        // ancillary outbox enrolled — mirrors exactly how IHostPro.Api wires it
        // in Program.cs. No connection is ever opened just to register/resolve
        // these services, so a bogus connection string and no running
        // PostgreSQL/RabbitMQ are safe here.
        var hostBuilder = Host.CreateApplicationBuilder();
        hostBuilder.Services.AddScoped<ITenantContext, TenantContext>();
        hostBuilder.Services.AddScoped<ICurrentTenantProvider, TenantContextCurrentTenantProvider>();
        hostBuilder.Services.AddIHostProTenantAwarePipeline();
        hostBuilder.Services.AddIdentityModule(configuration, isDevelopmentEnvironment: false);
        hostBuilder.Services.AddIdentityJwtIssuance(configuration);
        hostBuilder.Services.AddIdentityCommandDispatch();
        hostBuilder.UseWolverine(opts =>
        {
            opts.EnrollAncillaryPostgresqlOutbox(
                configuration.GetConnectionString("Identity")!, "identity_messaging", typeof(IdentityDbContext));
            opts.AutoBuildMessageStorageOnStartup = AutoCreate.None;
            opts.UseEntityFrameworkCoreTransactions();
        });

        return hostBuilder.Build();
    }

    [Fact]
    public void LoginCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<LoginCommand, Result<AuthTokensResult>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<LoginCommand, Result<AuthTokensResult>>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(LoginTenantAwareBehavior));

        // The generic TenantBootstrapBehavior<,> would also match LoginCommand
        // (it implements IBootstrapRequest) if it were ever registered open-generic
        // again — asserting the exact count above already guards against that, this
        // makes the specific regression explicit (Etapa 15A: Login moved off the
        // shared TenantBootstrapBehavior<,> onto IIdentityTransactionExecutor too).
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantBootstrapBehavior<LoginCommand, Result<AuthTokensResult>>));
    }

    [Fact]
    public void RefreshTokenCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<RefreshTokenCommand, Result<AuthTokensResult>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<RefreshTokenCommand, Result<AuthTokensResult>>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(RefreshTokenTenantAwareBehavior));

        // The generic TenantBootstrapBehavior<,> would also match RefreshTokenCommand
        // (it implements IBootstrapRequest) if it were ever registered open-generic
        // again — asserting the exact count above already guards against that, this
        // makes the specific regression explicit.
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantBootstrapBehavior<RefreshTokenCommand, Result<AuthTokensResult>>));
    }

    [Fact]
    public void LogoutCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<LogoutCommand, Result>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<LogoutCommand, Result>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(LogoutTenantAwareBehavior));
    }

    [Fact]
    public void IIdentityTransactionExecutor_resolves_from_the_container_to_the_expected_implementation()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var executor = scope.ServiceProvider.GetRequiredService<IIdentityTransactionExecutor>();

        executor.Should().BeOfType<IdentityOutboxTransactionExecutor>();
    }

    [Fact]
    public void IRefreshTokenExchangeExecutor_and_ILogoutExecutor_resolve_from_the_container()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRefreshTokenExchangeExecutor>().Should().BeOfType<RefreshTokenExchangeExecutor>();
        scope.ServiceProvider.GetRequiredService<ILogoutExecutor>().Should().BeOfType<LogoutExecutor>();
    }

    [Fact]
    public void Mediator_is_registered_as_Scoped()
    {
        // Not cosmetic (see IdentityApplicationMediatorExtensions' doc comment):
        // Mediator's default Singleton lifetime caches each generated
        // RequestHandlerWrapper's resolved handler/behaviors for the process
        // lifetime, silently turning IdentityDbContext (Scoped) into a de-facto
        // singleton shared by every concurrent request.
        var services = new ServiceCollection();
        services.AddIdentityApplicationMediator();

        var mediatorDescriptor = services.Single(d => d.ServiceType == typeof(IMediator));

        mediatorDescriptor.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}
