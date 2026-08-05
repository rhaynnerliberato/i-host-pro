using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Reservations.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Reservations'
/// Commands/Queries — mirrors <c>PropertyManagementCommandDispatchExtensions</c>
/// exactly. Called ONLY from <c>IHostPro.Api</c>'s composition root, never
/// from <c>IHostPro.Worker</c>'s: dispatching a Command/Query is an
/// HTTP-request concern.
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
        services.AddReservationsApplicationMediator();

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
        services.AddScoped<IReservationReader, ReservationReader>();
        services.AddScoped<IReservationAuditWriter, ReservationAuditWriter>();
        services.AddScoped<IReservationConflictGuard, ReservationConflictGuard>();

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

        services.AddScoped<
            IPipelineBehavior<ListReservationsQuery, Result<PagedResult<ReservationSummaryResult>>>,
            TenantTransactionBehavior<ListReservationsQuery, Result<PagedResult<ReservationSummaryResult>>>>();
        services.AddScoped<
            IPipelineBehavior<GetReservationDetailQuery, Result<ReservationResult>>,
            TenantTransactionBehavior<GetReservationDetailQuery, Result<ReservationResult>>>();

        return services;
    }
}
