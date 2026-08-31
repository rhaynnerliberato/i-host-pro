using FluentValidation;
using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Checklist;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Occurrences;
using IHostPro.Contexts.Housekeeping.Infrastructure.Persistence;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Housekeeping.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Housekeeping's write
/// Commands (plus most read Queries, not yet needed anywhere but Api) (Fase
/// 6, Incremento 1) — mirrors <c>ReservationsCommandDispatchExtensions</c>
/// exactly. Called ONLY from <c>IHostPro.Api</c>'s composition root:
/// dispatching a write Command remains an HTTP-request concern.
///
/// Fase 11, Checkpoint 3 update: <see cref="GetCleaningStatusByReservationQuery"/>'s
/// own Application Mediator wiring + <c>TenantTransactionBehavior&lt;,&gt;</c>
/// live in <see cref="HousekeepingModuleExtensions.AddHousekeepingModule"/>
/// (called by both Api and Worker) so the AI Agent's own Worker-hosted Read
/// Tool can execute it in-process via <c>IHousekeepingRequestDispatcher</c>
/// (Exception #3) — this method no longer calls
/// <c>AddHousekeepingApplicationMediator</c> itself (Module already did,
/// earlier in the same container). Every other Command/Query here stays
/// exactly as it was — write Commands/validators/write pipeline behaviors
/// remain Api-only.
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
        // AddHousekeepingApplicationMediator() is no longer called here —
        // AddHousekeepingModule (called earlier, in the same Api container)
        // already registers it (Fase 11, Checkpoint 3 — see this class's own
        // doc comment).

        services.AddScoped<IValidator<CreateCleaningCommand>, CreateCleaningCommandValidator>();
        services.AddScoped<IValidator<ListCleaningsQuery>, ListCleaningsQueryValidator>();
        services.AddScoped<IValidator<ListOwnCleaningsQuery>, ListOwnCleaningsQueryValidator>();
        services.AddScoped<IValidator<RegisterCleaningOccurrenceCommand>, RegisterCleaningOccurrenceCommandValidator>();
        services.AddScoped<IValidator<SetOwnCleaningChecklistItemCommand>, SetOwnCleaningChecklistItemCommandValidator>();

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
        services.AddScoped<
            IPipelineBehavior<ListOwnCleaningsQuery, Result<PagedResult<CleaningSummaryResult>>>,
            TenantTransactionBehavior<ListOwnCleaningsQuery, Result<PagedResult<CleaningSummaryResult>>, HousekeepingDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetOwnCleaningDetailQuery, Result<CleaningResult>>,
            TenantTransactionBehavior<GetOwnCleaningDetailQuery, Result<CleaningResult>, HousekeepingDbContext>>();
        services.AddScoped<
            IPipelineBehavior<ListCleaningOccurrencesQuery, Result<IReadOnlyList<CleaningOccurrenceResult>>>,
            TenantTransactionBehavior<ListCleaningOccurrencesQuery, Result<IReadOnlyList<CleaningOccurrenceResult>>, HousekeepingDbContext>>();
        services.AddScoped<
            IPipelineBehavior<GetOwnCleaningChecklistQuery, Result<IReadOnlyList<CleaningChecklistItemResult>>>,
            TenantTransactionBehavior<GetOwnCleaningChecklistQuery, Result<IReadOnlyList<CleaningChecklistItemResult>>, HousekeepingDbContext>>();

        // RegisterCleaningOccurrenceCommand/SetOwnCleaningChecklistItemCommand:
        // deliberately no pipeline behavior, same reasoning as this class's
        // own doc comment — the handler injects IHousekeepingTransactionExecutor
        // directly.

        return services;
    }
}
