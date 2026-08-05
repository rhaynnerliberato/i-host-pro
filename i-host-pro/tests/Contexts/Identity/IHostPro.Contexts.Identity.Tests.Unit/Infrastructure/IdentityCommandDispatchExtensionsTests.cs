using System.Security.Cryptography;
using FluentAssertions;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Messaging;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Profile;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Application.Users;
using IHostPro.Contexts.Identity.Infrastructure;
using IHostPro.Contexts.Identity.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Infrastructure.Sessions;
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
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantBootstrapBehavior<LoginCommand, Result<AuthTokensResult>, IdentityDbContext>));
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
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantBootstrapBehavior<RefreshTokenCommand, Result<AuthTokensResult>, IdentityDbContext>));
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
    public void RevokeOwnSessionCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<RevokeOwnSessionCommand, Result>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<RevokeOwnSessionCommand, Result>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(RevokeOwnSessionTenantAwareBehavior));

        // The plain, shared TenantTransactionBehavior<,> must never also be
        // registered for this command (Incremento 3, Checkpoint 4, approved
        // design) — it does not drain ISessionRevocationSignal/write
        // ISessionRevocationCache after commit, unlike RevokeOwnSessionExecutor.
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantTransactionBehavior<RevokeOwnSessionCommand, Result, IdentityDbContext>));
    }

    [Fact]
    public void GetOwnProfileQuery_pipeline_uses_the_shared_tenant_transaction_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<GetOwnProfileQuery, Result<OwnProfileResult>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<GetOwnProfileQuery, Result<OwnProfileResult>>));
        behaviors.Should().ContainSingle(
            b => b.GetType() == typeof(TenantTransactionBehavior<GetOwnProfileQuery, Result<OwnProfileResult>, IdentityDbContext>));
    }

    [Fact]
    public void ListOwnSessionsQuery_pipeline_uses_the_shared_tenant_transaction_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<ListOwnSessionsQuery, Result<IReadOnlyCollection<OwnSessionResult>>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(
            b => b.GetType() == typeof(ValidationBehavior<ListOwnSessionsQuery, Result<IReadOnlyCollection<OwnSessionResult>>>));
        behaviors.Should().ContainSingle(
            b => b.GetType() == typeof(TenantTransactionBehavior<ListOwnSessionsQuery, Result<IReadOnlyCollection<OwnSessionResult>>, IdentityDbContext>));
    }

    [Fact]
    public void CreateUserCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<CreateUserCommand, Result<UserResult>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<CreateUserCommand, Result<UserResult>>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(CreateUserTenantAwareBehavior));

        // The plain, shared TenantTransactionBehavior<,> must never also be
        // registered for this command (Incremento 3, Checkpoint 5, approved
        // design) — it does not translate a unique-email DbUpdateException
        // into Result.Failure after commit, unlike CreateUserExecutor.
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantTransactionBehavior<CreateUserCommand, Result<UserResult>, IdentityDbContext>));
    }

    [Fact]
    public void AssignRoleCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<AssignRoleCommand, Result>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<AssignRoleCommand, Result>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(AssignRoleTenantAwareBehavior));

        // The plain, shared TenantTransactionBehavior<,> must never also be
        // registered for this command (Incremento 3, Checkpoint 6, approved
        // design) — it does not drain ISessionRevocationSignal/write
        // ISessionRevocationCache after commit, unlike AssignRoleExecutor.
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantTransactionBehavior<AssignRoleCommand, Result, IdentityDbContext>));
    }

    [Fact]
    public void RemoveRoleCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<RemoveRoleCommand, Result>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<RemoveRoleCommand, Result>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(RemoveRoleTenantAwareBehavior));

        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantTransactionBehavior<RemoveRoleCommand, Result, IdentityDbContext>));
    }

    [Fact]
    public void BlockUserCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<BlockUserCommand, Result>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<BlockUserCommand, Result>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(BlockUserTenantAwareBehavior));

        // The plain, shared TenantTransactionBehavior<,> must never also be
        // registered for this command (Incremento 3, Checkpoint 7, approved
        // design) — it does not drain ISessionRevocationSignal/write
        // ISessionRevocationCache after commit, unlike BlockUserExecutor.
        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantTransactionBehavior<BlockUserCommand, Result, IdentityDbContext>));
    }

    [Fact]
    public void UnblockUserCommand_pipeline_is_exactly_validation_then_its_own_tenant_aware_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<UnblockUserCommand, Result>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<UnblockUserCommand, Result>));
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(UnblockUserTenantAwareBehavior));

        behaviors.Should().NotContain(b => b.GetType() == typeof(TenantTransactionBehavior<UnblockUserCommand, Result, IdentityDbContext>));
    }

    [Fact]
    public void UnblockUserTenantAwareBehavior_delegates_directly_to_the_shared_transaction_executor_with_no_command_specific_executor()
    {
        // Incremento 3, Checkpoint 7, Section 7: UnblockUser needs no bounded
        // concurrency retry, so — unlike BlockUserTenantAwareBehavior, which
        // wraps IBlockUserExecutor — this behavior's only dependency is the
        // shared IIdentityTransactionExecutor, mirroring
        // LoginTenantAwareBehavior's exact shape. Checked structurally via the
        // constructor signature since, by design, no IUnblockUserExecutor
        // interface exists to resolve.
        var constructor = typeof(UnblockUserTenantAwareBehavior).GetConstructors().Single();
        var parameterTypes = constructor.GetParameters().Select(p => p.ParameterType).ToArray();

        parameterTypes.Should().Equal([typeof(IIdentityTransactionExecutor)]);
    }

    [Fact]
    public void ListUsersQuery_pipeline_uses_the_shared_tenant_transaction_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<ListUsersQuery, Result<PagedResult<UserResult>>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(
            b => b.GetType() == typeof(ValidationBehavior<ListUsersQuery, Result<PagedResult<UserResult>>>));
        behaviors.Should().ContainSingle(
            b => b.GetType() == typeof(TenantTransactionBehavior<ListUsersQuery, Result<PagedResult<UserResult>>, IdentityDbContext>));
    }

    [Fact]
    public void GetUserByIdQuery_pipeline_uses_the_shared_tenant_transaction_behavior()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var behaviors = scope.ServiceProvider
            .GetServices<IPipelineBehavior<GetUserByIdQuery, Result<UserResult>>>()
            .ToList();

        behaviors.Should().HaveCount(2);
        behaviors.Should().ContainSingle(b => b.GetType() == typeof(ValidationBehavior<GetUserByIdQuery, Result<UserResult>>));
        behaviors.Should().ContainSingle(
            b => b.GetType() == typeof(TenantTransactionBehavior<GetUserByIdQuery, Result<UserResult>, IdentityDbContext>));
    }

    [Fact]
    public void ICreateUserExecutor_resolves_from_the_container_to_the_expected_implementation()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICreateUserExecutor>().Should().BeOfType<CreateUserExecutor>();
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
    public void IRevokeOwnSessionExecutor_resolves_from_the_container_to_the_expected_implementation()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRevokeOwnSessionExecutor>().Should().BeOfType<RevokeOwnSessionExecutor>();
    }

    [Fact]
    public void IAssignRoleExecutor_and_IRemoveRoleExecutor_resolve_from_the_container_to_the_expected_implementation()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAssignRoleExecutor>().Should().BeOfType<AssignRoleExecutor>();
        scope.ServiceProvider.GetRequiredService<IRemoveRoleExecutor>().Should().BeOfType<RemoveRoleExecutor>();
    }

    [Fact]
    public void ILastAdministratorGuard_and_IUserSessionRevoker_resolve_from_the_container_to_the_expected_implementation()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ILastAdministratorGuard>().Should().BeOfType<LastAdministratorGuard>();
        scope.ServiceProvider.GetRequiredService<IUserSessionRevoker>().Should().BeOfType<UserSessionRevoker>();
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
