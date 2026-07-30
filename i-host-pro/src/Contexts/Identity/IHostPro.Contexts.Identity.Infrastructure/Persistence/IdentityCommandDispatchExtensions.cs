using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Application.Catalog;
using IHostPro.Contexts.Identity.Application.Profile;
using IHostPro.Contexts.Identity.Application.Sessions;
using IHostPro.Contexts.Identity.Application.Users;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence;

/// <summary>
/// Registers Mediator, the FluentValidation validators, and every pipeline
/// behavior the three auth commands (Incremento 2 plan, Etapa 14) and the two
/// catalog queries (Incremento 3, Checkpoint 3) need — deliberately kept out
/// of <c>AddIdentityModule</c> and called ONLY from <c>IHostPro.Api</c>'s
/// composition root, never from <c>IHostPro.Worker</c>'s: dispatching a
/// Command or Query is an HTTP-request concern, and
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
///
/// <see cref="ListRolesQuery"/>/<see cref="ListPermissionsQuery"/>/
/// <see cref="GetOwnProfileQuery"/>/<see cref="ListOwnSessionsQuery"/> are all
/// plain reads: they publish no event and need no outbox, so each is
/// registered with the plain, shared, generic <c>TenantTransactionBehavior&lt;,&gt;</c>
/// directly (still closed per message type, never as an open generic — see
/// Architecture Principles §6) — the tenant is already resolved from the
/// authenticated JWT claim by <c>ConfigureJwtBearerOptions</c> before the
/// Mediator pipeline runs, and because all four queries implement
/// <c>IReadOnlyRequest</c> (via <c>IQuery&lt;TResponse&gt;</c>), the
/// transaction each opens is automatically <c>READ ONLY</c> — no bespoke
/// behavior, no manual <c>SET LOCAL</c> (Incremento 3, Checkpoints 3-4,
/// approved design).
///
/// <see cref="RevokeOwnSessionCommand"/> (Incremento 3, Checkpoint 4) is
/// different: it is the first NEW command since Etapa 14 that mutates state
/// and publishes an Integration Event (<c>SessionRevoked</c>), so — exactly
/// like Logout — it gets its own closed-generic tenant-aware behavior
/// (<see cref="RevokeOwnSessionTenantAwareBehavior"/>) wrapping a
/// command-specific executor (<see cref="IRevokeOwnSessionExecutor"/>) that
/// itself wraps the shared <see cref="IIdentityTransactionExecutor"/> — never
/// the generic <c>TenantTransactionBehavior&lt;,&gt;</c>, which does not drain
/// <see cref="ISessionRevocationSignal"/>/write <see cref="ISessionRevocationCache"/>
/// after commit.
///
/// <see cref="CreateUserCommand"/> (Incremento 3, Checkpoint 5) follows the
/// exact same shape as <see cref="RevokeOwnSessionCommand"/> — its own
/// closed-generic <see cref="CreateUserTenantAwareBehavior"/> wrapping its
/// own command-specific executor (<see cref="ICreateUserExecutor"/>) — except
/// its post-processing step is translating a caught PostgreSQL
/// unique-violation into <c>Identity.EmailAlreadyInUse</c> instead of a
/// post-commit cache write (no Redis in this flow — see
/// <see cref="ICreateUserExecutor"/>'s own doc comment).
/// <see cref="ListUsersQuery"/>/<see cref="GetUserByIdQuery"/> are plain
/// reads, joining <see cref="ListRolesQuery"/>/<see cref="ListPermissionsQuery"/>/
/// <see cref="GetOwnProfileQuery"/>/<see cref="ListOwnSessionsQuery"/> on the
/// plain, shared <c>TenantTransactionBehavior&lt;,&gt;</c>.
///
/// <see cref="AssignRoleCommand"/>/<see cref="RemoveRoleCommand"/> (Incremento
/// 3, Checkpoint 6) follow the exact same shape as
/// <see cref="RevokeOwnSessionCommand"/> again — each its own closed-generic
/// tenant-aware behavior wrapping its own command-specific executor
/// (<see cref="IAssignRoleExecutor"/>/<see cref="IRemoveRoleExecutor"/>),
/// including the SAME bounded concurrency retry
/// (<see cref="RevokeOwnSessionExecutor"/>'s rationale applies verbatim: both
/// commands' session-revocation cascade can mutate any of the target user's
/// active sessions/refresh tokens) and post-commit
/// <see cref="ISessionRevocationCache"/> write.
///
/// <see cref="BlockUserCommand"/> (Incremento 3, Checkpoint 7) follows the
/// exact same shape again — its own closed-generic
/// <see cref="BlockUserTenantAwareBehavior"/> wrapping
/// <see cref="IBlockUserExecutor"/>, built with the Checkpoint 6
/// retry-safety correction from the start. <see cref="UnblockUserCommand"/>
/// is different: no session/refresh-token cascade and no genuine concurrency
/// risk on the single row it touches (see
/// <c>UnblockUserCommandHandler</c>'s doc comment), so it delegates its
/// closed-generic <see cref="UnblockUserTenantAwareBehavior"/> directly to
/// the shared <see cref="IIdentityTransactionExecutor"/> — no
/// command-specific executor, mirroring <see cref="LoginTenantAwareBehavior"/>'s
/// "outbox needed, retry not" shape.
///
/// <see cref="UpdateUserCommand"/> (Incremento 3, Checkpoint 8) is a THIRD
/// shape: it needs a command-specific executor (<see cref="IUpdateUserExecutor"/>,
/// like CreateUser/AssignRole/Block) to translate two specific caught
/// exceptions, but — like UnblockUser — no bounded retry at all (Section 6:
/// "não introduzir retry automático"). A genuine
/// <c>DbUpdateConcurrencyException</c> IS possible here (two concurrent
/// PATCHes of the SAME user), unlike UnblockUser's row, but retrying with
/// the caller's now-possibly-stale input would risk silently clobbering the
/// other request's change — so it is translated to
/// <c>Identity.UserConcurrencyConflict</c> and returned as a normal
/// rejection instead.
///
/// <see cref="ChangeOwnPasswordCommand"/>/<see cref="AdminResetPasswordCommand"/>
/// (Incremento 3, Checkpoint 9) combine UpdateUser's "no bounded retry"
/// shape with Block/AssignRole/RemoveRole's "drain
/// <see cref="ISessionRevocationSignal"/> into
/// <see cref="ISessionRevocationCache"/> after commit" shape — each gets its
/// own closed-generic tenant-aware behavior
/// (<see cref="ChangeOwnPasswordTenantAwareBehavior"/>/<see cref="AdminResetPasswordTenantAwareBehavior"/>)
/// wrapping its own command-specific executor
/// (<see cref="IChangeOwnPasswordExecutor"/>/<see cref="IAdminResetPasswordExecutor"/>).
/// See <see cref="IChangeOwnPasswordExecutor"/>'s doc comment for why no
/// retry applies here even though both cascade to session revocation.
/// </summary>
public static class IdentityCommandDispatchExtensions
{
    public static IServiceCollection AddIdentityCommandDispatch(this IServiceCollection services)
    {
        services.AddIdentityApplicationMediator();

        services.AddScoped<IValidator<LoginCommand>, LoginCommandValidator>();
        services.AddScoped<IValidator<RefreshTokenCommand>, RefreshTokenCommandValidator>();
        services.AddScoped<IValidator<LogoutCommand>, LogoutCommandValidator>();
        services.AddScoped<IValidator<RevokeOwnSessionCommand>, RevokeOwnSessionCommandValidator>();
        services.AddScoped<IValidator<CreateUserCommand>, CreateUserCommandValidator>();
        services.AddScoped<IValidator<ListUsersQuery>, ListUsersQueryValidator>();
        services.AddScoped<IValidator<GetUserByIdQuery>, GetUserByIdQueryValidator>();
        services.AddScoped<IValidator<AssignRoleCommand>, AssignRoleCommandValidator>();
        services.AddScoped<IValidator<RemoveRoleCommand>, RemoveRoleCommandValidator>();
        services.AddScoped<IValidator<BlockUserCommand>, BlockUserCommandValidator>();
        services.AddScoped<IValidator<UnblockUserCommand>, UnblockUserCommandValidator>();
        services.AddScoped<IValidator<UpdateUserCommand>, UpdateUserCommandValidator>();
        services.AddScoped<IValidator<ChangeOwnPasswordCommand>, ChangeOwnPasswordCommandValidator>();
        services.AddScoped<IValidator<AdminResetPasswordCommand>, AdminResetPasswordCommandValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, it has no tenant/transaction side effects to collide with.
        // ListRolesQuery/ListPermissionsQuery/GetOwnProfileQuery/
        // ListOwnSessionsQuery have no validator registered (nothing
        // meaningful to validate on a query built exclusively from
        // already-validated claims) — FluentValidation's pipeline simply
        // passes them through.
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

        // Incremento 3, Checkpoint 4 — see class remarks above. Same shape as
        // ILogoutExecutor immediately above (a command-specific executor
        // wrapping IIdentityTransactionExecutor plus the post-commit
        // revocation-cache write), never the generic
        // IIdentityTransactionExecutor directly.
        services.AddScoped<IRevokeOwnSessionExecutor, RevokeOwnSessionExecutor>();

        // Incremento 3, Checkpoint 5 — see class remarks above.
        services.AddScoped<ICreateUserExecutor, CreateUserExecutor>();

        // Incremento 3, Checkpoint 6 — see class remarks above.
        services.AddScoped<IAssignRoleExecutor, AssignRoleExecutor>();
        services.AddScoped<IRemoveRoleExecutor, RemoveRoleExecutor>();

        // Incremento 3, Checkpoint 7 — see class remarks above. UnblockUser
        // needs no executor of its own: its behavior below delegates directly
        // to IIdentityTransactionExecutor, already registered above.
        services.AddScoped<IBlockUserExecutor, BlockUserExecutor>();

        // Incremento 3, Checkpoint 8 — see class remarks above.
        services.AddScoped<IUpdateUserExecutor, UpdateUserExecutor>();

        // Incremento 3, Checkpoint 9 — see class remarks above.
        services.AddScoped<IChangeOwnPasswordExecutor, ChangeOwnPasswordExecutor>();
        services.AddScoped<IAdminResetPasswordExecutor, AdminResetPasswordExecutor>();

        services.AddScoped<
            IPipelineBehavior<LoginCommand, Result<AuthTokensResult>>,
            LoginTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<RefreshTokenCommand, Result<AuthTokensResult>>,
            RefreshTokenTenantAwareBehavior>();
        services.AddScoped<IPipelineBehavior<LogoutCommand, Result>, LogoutTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<RevokeOwnSessionCommand, Result>,
            RevokeOwnSessionTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<CreateUserCommand, Result<UserResult>>,
            CreateUserTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<AssignRoleCommand, Result>,
            AssignRoleTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<RemoveRoleCommand, Result>,
            RemoveRoleTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<BlockUserCommand, Result>,
            BlockUserTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<UnblockUserCommand, Result>,
            UnblockUserTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<UpdateUserCommand, Result<UserResult>>,
            UpdateUserTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<ChangeOwnPasswordCommand, Result>,
            ChangeOwnPasswordTenantAwareBehavior>();
        services.AddScoped<
            IPipelineBehavior<AdminResetPasswordCommand, Result>,
            AdminResetPasswordTenantAwareBehavior>();

        // Incremento 3, Checkpoint 3 — see class remarks above.
        services.AddScoped<
            IPipelineBehavior<ListRolesQuery, Result<IReadOnlyCollection<CatalogRole>>>,
            TenantTransactionBehavior<ListRolesQuery, Result<IReadOnlyCollection<CatalogRole>>>>();
        services.AddScoped<
            IPipelineBehavior<ListPermissionsQuery, Result<IReadOnlyCollection<CatalogPermission>>>,
            TenantTransactionBehavior<ListPermissionsQuery, Result<IReadOnlyCollection<CatalogPermission>>>>();

        // Incremento 3, Checkpoint 4 — see class remarks above.
        services.AddScoped<
            IPipelineBehavior<GetOwnProfileQuery, Result<OwnProfileResult>>,
            TenantTransactionBehavior<GetOwnProfileQuery, Result<OwnProfileResult>>>();
        services.AddScoped<
            IPipelineBehavior<ListOwnSessionsQuery, Result<IReadOnlyCollection<OwnSessionResult>>>,
            TenantTransactionBehavior<ListOwnSessionsQuery, Result<IReadOnlyCollection<OwnSessionResult>>>>();

        // Incremento 3, Checkpoint 5 — see class remarks above.
        services.AddScoped<
            IPipelineBehavior<ListUsersQuery, Result<PagedResult<UserResult>>>,
            TenantTransactionBehavior<ListUsersQuery, Result<PagedResult<UserResult>>>>();
        services.AddScoped<
            IPipelineBehavior<GetUserByIdQuery, Result<UserResult>>,
            TenantTransactionBehavior<GetUserByIdQuery, Result<UserResult>>>();

        return services;
    }
}
