using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Workflow.Application;
using IHostPro.Contexts.Workflow.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Workflow.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Workflow Orchestration
/// module (Fase 8, Checkpoint 1) — consumed exclusively in
/// <c>IHostPro.Worker</c>, never <c>IHostPro.Api</c> (this context has no
/// HTTP surface this checkpoint — approved minimal-footprint decision: no
/// Domain/Contracts/Api project, only Application/Infrastructure, since CP1
/// has no aggregates, publishes nothing of its own, and exposes no
/// endpoint).
/// </summary>
public static class WorkflowModuleExtensions
{
    /// <summary>Keyed-DI key this context's <c>IIntegrationEventHandler&lt;T&gt;</c> registrations use — see <see cref="ReservationCreatedHandler"/>'s own doc comment.</summary>
    public const string HandlerKey = "workflow";

    public static IServiceCollection AddWorkflowModule(this IServiceCollection services)
    {
        // ReservationCreated already has other consumers (Housekeeping,
        // Dashboard) in the same IHostPro.Worker process — keyed
        // registration from the first commit (Fase 7's own real-Worker
        // regression already proved why an unkeyed registration here would
        // be unsafe). No IWorkflowMessageExecutionScope: this handler never
        // resolves a tenant-scoped DbContext (this context has none), so
        // the ADR-015/016 scope-opening mechanism does not apply — the
        // keyed service is resolved directly from Wolverine's own
        // per-message scope (see ReservationCreatedHandler).
        //
        // Fase 8, Checkpoint 1.1: the use case itself
        // (ReservationCreatedCleaningOrchestrator) now lives in
        // Workflow.Application; this registration is unchanged in shape,
        // only in which assembly the implementation comes from.
        services.AddKeyedScoped<IIntegrationEventHandler<ReservationCreated>, ReservationCreatedCleaningOrchestrator>(HandlerKey);

        // The transport-only implementation of the Application layer's
        // dispatcher abstraction — exclusive to Workflow, no other context
        // registers IWorkflowCommandDispatcher, so no keying is needed here.
        services.AddScoped<IWorkflowCommandDispatcher, WolverineWorkflowCommandDispatcher>();

        return services;
    }
}
