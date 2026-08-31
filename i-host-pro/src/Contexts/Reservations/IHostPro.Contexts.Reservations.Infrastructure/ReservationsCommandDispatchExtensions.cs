using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Reservations.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Reservations' write
/// Commands (plus the one read Query — <see cref="ListReservationsQuery"/> —
/// not yet needed anywhere but Api) — mirrors
/// <c>PropertyManagementCommandDispatchExtensions</c> exactly. Called ONLY
/// from <c>IHostPro.Api</c>'s composition root: dispatching a write Command
/// remains an HTTP-request concern.
///
/// Fase 11, Checkpoint 3 update: this is no longer the only place Queries
/// get dispatched. <see cref="GetReservationDetailQuery"/>/<see cref="ListScheduleQuery"/>'s
/// own Application Mediator wiring + <c>TenantTransactionBehavior&lt;,&gt;</c>
/// moved to <see cref="ReservationsModuleExtensions.AddReservationsModule"/>
/// (called by both Api and Worker) so the AI Agent's own Worker-hosted Read
/// Tools can execute them in-process via <see cref="IReservationsRequestDispatcher"/>
/// (Exception #3) — this method no longer calls
/// <c>AddReservationsApplicationMediator</c> itself (Module already did,
/// earlier in the same container) and no longer re-registers those two
/// queries' behaviors. The conceptual boundary going forward: write
/// Commands/validators/write pipeline behaviors stay Api-only; read Queries
/// may be promoted to the shared Module when an approved Architecture
/// Exception authorizes a trusted in-process consumer — see
/// <c>ReservationsModuleExtensions</c>'s own comment for the specifics of
/// this promotion.
///
/// <see cref="CreateReservationCommand"/>/<see cref="UpdateReservationCommand"/>/
/// <see cref="CancelReservationCommand"/> deliberately get NO wrapping
/// pipeline behavior at all — mirrors <c>LinkPropertyOwnerCommand</c>'s own
/// precedent exactly: each handler injects its own executor
/// (<see cref="ICreateReservationExecutor"/>/<see cref="IUpdateReservationExecutor"/>/
/// <see cref="ICancelReservationExecutor"/>) directly and opens this
/// context's write transaction itself, at the precise point it needs to
/// (Create: only after the synchronous Property Management eligibility
/// check has already completed; Update: only after reading the current
/// reservation, since the FINAL property id/eligibility/conflict check all
/// depend on it) — a wrapping behavior cannot express either shape.
/// Registering a dedicated <c>IPipelineBehavior&lt;,&gt;</c> for any of the
/// three in addition would double-wrap the transaction executor and throw
/// <c>NestedUnitOfWorkException</c>.
///
/// <see cref="ListReservationsQuery"/>/<see cref="GetReservationDetailQuery"/>
/// are plain reads — no event, no outbox — registered with the shared,
/// generic <c>TenantTransactionBehavior&lt;,&gt;</c> directly, still closed
/// per message type, like every other query in this codebase.
/// </summary>
public static class ReservationsCommandDispatchExtensions
{
    public static IServiceCollection AddReservationsCommandDispatch(this IServiceCollection services)
    {
        // AddReservationsApplicationMediator() is no longer called here —
        // AddReservationsModule (called earlier, in the same Api container)
        // already registers it (Fase 11, Checkpoint 3 — see this class's own
        // doc comment).

        services.AddScoped<IValidator<CreateReservationCommand>, CreateReservationCommandValidator>();
        services.AddScoped<IValidator<UpdateReservationCommand>, UpdateReservationCommandValidator>();
        services.AddScoped<IValidator<ListReservationsQuery>, ListReservationsQueryValidator>();

        // Validation runs first for every command — safe as a single open
        // generic, it has no tenant/transaction side effects to collide
        // with. CancelReservationCommand/GetReservationDetailQuery have no
        // validator registered — nothing meaningful to validate beyond an
        // already-model-bound route guid.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        services.AddScoped<IRepository<Reservation, Guid>, ReservationRepository>();
        // IReservationReader is registered by AddReservationsModule (Fase 11,
        // Checkpoint 3) — no longer duplicated here.
        services.AddScoped<IReservationAuditWriter, ReservationAuditWriter>();
        services.AddScoped<IReservationConflictGuard, ReservationConflictGuard>();

        // Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1). IScheduleReader
        // is registered by AddReservationsModule (Fase 11, Checkpoint 3) — no
        // longer duplicated here; the validator remains Api-only.
        services.AddScoped<IValidator<ListScheduleQuery>, ListScheduleQueryValidator>();

        // Backs every write command's transactional step — see
        // ReservationsOutboxTransactionExecutor's own doc comment.
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IReservationsTransactionExecutor, ReservationsOutboxTransactionExecutor>();

        services.AddScoped<ICreateReservationExecutor, CreateReservationExecutor>();
        services.AddScoped<IUpdateReservationExecutor, UpdateReservationExecutor>();
        services.AddScoped<ICancelReservationExecutor, CancelReservationExecutor>();

        // CreateReservationCommand/UpdateReservationCommand/CancelReservationCommand:
        // deliberately no pipeline behavior — see this class's own doc
        // comment and each executor's.

        // Closed to ReservationsDbContext explicitly (Fase 4 homologation
        // fix) — never the ambiguous, unparameterized DbContext base type.
        // GetReservationDetailQuery/ListScheduleQuery's own behaviors are
        // registered by AddReservationsModule (Fase 11, Checkpoint 3) — not
        // duplicated here (double-registering the same closed
        // IPipelineBehavior<,> would run it twice).
        services.AddScoped<
            IPipelineBehavior<ListReservationsQuery, Result<PagedResult<ReservationSummaryResult>>>,
            TenantTransactionBehavior<ListReservationsQuery, Result<PagedResult<ReservationSummaryResult>>, ReservationsDbContext>>();

        return services;
    }
}
