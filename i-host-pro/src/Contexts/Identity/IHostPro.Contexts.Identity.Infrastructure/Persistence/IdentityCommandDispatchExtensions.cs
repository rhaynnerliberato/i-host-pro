using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// Registers Mediator, the FluentValidation validators, and every pipeline
/// behavior the three auth commands need (Incremento 2 plan, Etapa 14) —
/// deliberately kept out of <c>AddIdentityModule</c> and called ONLY from
/// <c>IHostPro.Api</c>'s composition root, never from <c>IHostPro.Worker</c>'s:
/// dispatching these commands is an HTTP-request concern, and
/// <see cref="RefreshTokenTenantAwareBehavior"/>/<see cref="LogoutTenantAwareBehavior"/>
/// pull in the same Api-only executors (which themselves need nothing
/// Api-only, but keeping this alongside <c>AddIdentityJwtBearerAuthentication</c>
/// matches where these commands are actually dispatched from).
///
/// Each of the three commands gets its own CLOSED-generic tenant-aware
/// behavior registration — see <see cref="RefreshTokenTenantAwareBehavior"/>'s
/// doc comment for why an open-generic registration would collide for
/// Refresh/Logout specifically. Since Etapa 15A, all three
/// (<see cref="LoginTenantAwareBehavior"/>/<see cref="RefreshTokenTenantAwareBehavior"/>/
/// <see cref="LogoutTenantAwareBehavior"/>) delegate to the same
/// <see cref="IIdentityTransactionExecutor"/> — none of them uses the generic
/// <c>TenantBootstrapBehavior&lt;,&gt;</c>/<c>TenantTransactionBehavior&lt;,&gt;</c>/
/// <c>ITenantAwareUnitOfWork</c> anymore, since only <c>IIdentityTransactionExecutor</c>
/// also flushes Identity's durable outbox atomically with the domain change.
/// </summary>
public static class IdentityCommandDispatchExtensions
{
    public static IServiceCollection AddIdentityCommandDispatch(this IServiceCollection services)
    {
        services.AddIdentityApplicationMediator();

        services.AddScoped<IValidator<LoginCommand>, LoginCommandValidator>();
        services.AddScoped<IValidator<RefreshTokenCommand>, RefreshTokenCommandValidator>();
        services.AddScoped<IValidator<LogoutCommand>, LogoutCommandValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, it has no tenant/transaction side effects to collide with.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // Backs every auth command's transactional step (Etapa 15A) — see
        // IdentityOutboxTransactionExecutor's own doc comment.
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IIdentityTransactionExecutor, IdentityOutboxTransactionExecutor>();

        // Moved here from AddIdentityModule (Etapa 15A): both now depend on
        // IIdentityTransactionExecutor above, and their only callers
        // (RefreshTokenTenantAwareBehavior/LogoutTenantAwareBehavior) already
        // live exclusively in this Api-only dispatch wiring.
        services.AddScoped<IRefreshTokenExchangeExecutor, RefreshTokenExchangeExecutor>();
        services.AddScoped<ILogoutExecutor, LogoutExecutor>();

        services.AddScoped<
            IPipelineBehavior<LoginCommand, Result<AuthTokensResult>>,
            LoginTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<RefreshTokenCommand, Result<AuthTokensResult>>,
            RefreshTokenTenantAwareBehavior>();
        services.AddScoped<IPipelineBehavior<LogoutCommand, Result>, LogoutTenantAwareBehavior>();

        return services;
    }
}
