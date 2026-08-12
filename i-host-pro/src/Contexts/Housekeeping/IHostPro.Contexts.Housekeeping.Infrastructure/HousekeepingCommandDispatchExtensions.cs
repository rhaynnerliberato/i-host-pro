using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Housekeeping.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Housekeeping's
/// Commands/Queries (Fase 6, Incremento 1) — mirrors
/// <c>ReservationsCommandDispatchExtensions</c> exactly. Called ONLY from
/// <c>IHostPro.Api</c>'s composition root, never <c>IHostPro.Worker</c>'s —
/// dispatching a Command/Query is an HTTP-request concern; the Worker's own
/// needs are entirely covered by <see cref="HousekeepingModuleExtensions.AddHousekeepingModule"/>.
///
/// Every write command
/// (Create/Assign/Start/StartInspection/Complete/Cancel/MarkInterrupted/
/// MarkWaitingMaterials/MarkWaitingHelp) deliberately gets NO wrapping
/// pipeline behavior — each handler injects <see cref="IHousekeepingTransactionExecutor"/>
/// directly and opens this context's write transaction itself, mirroring
/// <c>CreateReservationCommand</c>/<c>CancelReservationCommand</c>'s own
/// precedent (a shared generic executor here, rather than one
/// narrowly-typed wrapper interface per command — see
/// <see cref="IHousekeepingTransactionExecutor"/>'s own doc comment for why).
///
/// <see cref="ListCleaningsQuery"/>/<see cref="GetCleaningDetailQuery"/> are
/// plain reads — no event, no outbox — registered with the shared, generic
/// <c>TenantTransactionBehavior&lt;,&gt;</c> directly, closed to
/// <see cref="Persistence.HousekeepingDbContext"/> explicitly.
/// </summary>
public static class HousekeepingCommandDispatchExtensions
{
    public static IServiceCollection AddHousekeepingCommandDispatch(this IServiceCollection services)
    {
        services.AddHousekeepingApplicationMediator();

        services.AddScoped<IValidator<CreateCleaningCommand>, CreateCleaningCommandValidator>();
        services.AddScoped<IValidator<ListCleaningsQuery>, ListCleaningsQueryValidator>();

        // Validation runs first for every command — safe as a single open
        // generic. Assign/Start/StartInspection/Complete/Cancel/MarkInterrupted/
        // MarkWaitingMaterials/MarkWaitingHelp/GetCleaningDetailQuery have no
        // validator registered — nothing meaningful to validate beyond an
        // already-model-bound route guid.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));

        // CreateCleaningCommand/AssignCleaningCommand/StartCleaningCommand/
        // StartCleaningInspectionCommand/CompleteCleaningCommand/
        // CancelCleaningCommand/MarkCleaningInterruptedCommand/
        // MarkCleaningWaitingMaterialsCommand/MarkCleaningWaitingHelpCommand:
        // deliberately no pipeline behavior — see this class's own doc
        // comment.

        services.AddScoped<
            IPipelineBehavior<ListCleaningsQuery, Result<PagedResult<CleaningSummaryResult>>>,
            TenantTransactionBehavior<ListCleaningsQuery, Result<PagedResult<CleaningSummaryResult>>, HousekeepingDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetCleaningDetailQuery, Result<CleaningResult>>,
            TenantTransactionBehavior<GetCleaningDetailQuery, Result<CleaningResult>, HousekeepingDbContext>>();

        return services;
    }
}
