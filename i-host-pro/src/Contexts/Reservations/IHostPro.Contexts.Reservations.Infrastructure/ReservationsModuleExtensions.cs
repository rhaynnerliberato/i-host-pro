using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.AirbnbImports;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Application.Schedule;
using IHostPro.Contexts.Reservations.Contracts;
using IHostPro.Contexts.Reservations.Domain;
using IHostPro.Contexts.Reservations.Infrastructure.Communication;
using IHostPro.Contexts.Reservations.Infrastructure.Messaging;
using IHostPro.Contexts.Reservations.Infrastructure.Persistence;
using IHostPro.Contexts.Reservations.Infrastructure.Projections;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Reservations.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Reservations module (Fase 3,
/// Incremento 1 plan) — mirrors <c>PropertyManagementModuleExtensions</c>/
/// <c>IdentityModuleExtensions</c> exactly. The Host (IHostPro.Api) calls
/// this once.
/// </summary>
public static class ReservationsModuleExtensions
{
    public static IServiceCollection AddReservationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors
        // PropertyManagementModuleExtensions/ReservationsDbContextFactory.
        services.AddDbContext<ReservationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Reservations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "reservations")));

        services.AddSingleton(TimeProvider.System);

        // Fase 9, Checkpoint 1 — ADR-019: the single, purpose-limited
        // synchronous query port Communication may use to read a guest's
        // contact data for one Reservation. Registered here (not only in
        // IHostPro.Api) because Communication's own Wolverine consumer runs
        // in IHostPro.Worker, and AddReservationsModule already runs in both
        // processes — mirrors PropertyManagementModuleExtensions' own
        // registration of IPropertyReservationEligibilityReader (ADR-014).
        services.AddScoped<IReservationGuestContactReader, ReservationGuestContactReader>();

        // Fase 10, Checkpoint 3 — ADR-024 amendment, synchronous exception
        // #7: the single, purpose-limited synchronous query port Guest
        // Operations may use to evaluate an Early Check-in/Late Checkout
        // request's schedule eligibility. Same registration placement
        // reasoning as IReservationGuestContactReader above.
        services.AddScoped<IReservationScheduleReader, IHostPro.Contexts.Reservations.Infrastructure.GuestOperations.ReservationScheduleReader>();

        // Fase 11, Checkpoint 1 — ADR-029, synchronous exception #13: the
        // single, purpose-limited synchronous query port Communication may
        // use to resolve which Confirmed Reservation(s) an inbound guest
        // message's phone number could belong to. Same registration
        // placement reasoning as IReservationGuestContactReader above —
        // Communication's own Wolverine consumer runs in IHostPro.Worker.
        services.AddScoped<IReservationByGuestPhoneReader, IHostPro.Contexts.Reservations.Infrastructure.Communication.ReservationByGuestPhoneReader>();

        // Fase 11, Checkpoint 3 — Exception #3 (AI Agent Tools -> Application
        // Services): the AI Agent's own Wolverine consumer runs in
        // IHostPro.Worker and needs to execute GetReservationDetailQuery/
        // ListScheduleQuery in-process via IReservationsRequestDispatcher.
        // Query-only Application Mediator wiring is promoted here (shared
        // Module) so both processes get it; write Commands/validators/
        // pipeline behaviors remain Api-only —
        // see ReservationsCommandDispatchExtensions' own updated doc comment.
        // Deliberately no ValidationBehavior<,>/IValidator<ListScheduleQuery>
        // here — the AI Agent's own GetSchedule Tool is responsible for
        // bounding its own interval before ever calling this query; Api's
        // HTTP path keeps its full validation via AddReservationsCommandDispatch.
        //
        // AddReservationsApplicationMediator()'s own AddMediator() call
        // registers EVERY handler in this assembly, including every write
        // Command handler — whose own dependencies (IReservationConflictGuard/
        // executors/etc.) are deliberately never registered here. This is
        // harmless for IHostPro.Api (which also calls
        // AddReservationsCommandDispatch, registering those dependencies
        // too) but fails IHostPro.Worker's own ValidateOnBuild at startup (a
        // real crash found and fixed during CP3 homologation) — Worker's own
        // Program.cs calls KeepOnlyMediatorHandlers right after this method
        // returns to trim its OWN composition down to the two approved
        // read-only query handlers; this shared method must never do that
        // trimming itself, since IHostPro.Api's real write HTTP endpoints
        // depend on every handler staying registered here.
        services.AddReservationsApplicationMediator();
        services.AddScoped<IReservationReader, ReservationReader>();
        services.AddScoped<IScheduleReader, ScheduleReader>();
        services.AddScoped<
            IPipelineBehavior<GetReservationDetailQuery, Result<ReservationResult>>,
            TenantTransactionBehavior<GetReservationDetailQuery, Result<ReservationResult>, ReservationsDbContext>>();
        services.AddScoped<
            IPipelineBehavior<ListScheduleQuery, Result<IReadOnlyList<ScheduleItemResult>>>,
            TenantTransactionBehavior<ListScheduleQuery, Result<IReadOnlyList<ScheduleItemResult>>, ReservationsDbContext>>();

        return services;
    }

    /// <summary>
    /// The minimal composition root for consuming Housekeeping's own
    /// Cleaning lifecycle events inside <c>IHostPro.Worker</c> (Fase 7,
    /// Incremento 1 — Agenda Foundation, Checkpoint 1) — deliberately
    /// separate from <see cref="ReservationsCommandDispatchExtensions.AddReservationsCommandDispatch"/>,
    /// which that class's own doc comment restricts to <c>IHostPro.Api</c>'s
    /// composition root only (HTTP command/query dispatch — validators,
    /// pipeline behaviors, Mediator — none of which the Worker process
    /// needs). Registers exactly the services
    /// <see cref="Projections.CleaningScheduleProjectionSynchronizer"/> and
    /// its own dependency chain require, so the tenant-safe execution
    /// boundary (<see cref="IReservationsMessageExecutionScope"/>, ADR-016)
    /// can construct it, from its own child DI scope, for every consumed
    /// Cleaning lifecycle message — mirrors
    /// <c>ReservationsOutboxTransactionExecutor</c>'s registration in
    /// <c>AddReservationsCommandDispatch</c>, duplicated here deliberately
    /// rather than reused, since that method is Api-only by design.
    ///
    /// Checkpoint 1 CLOSURE (real defect found and fixed, ADR-016): the
    /// original design let Wolverine's own codegen resolve
    /// <c>CleaningScheduleProjectionSynchronizer</c> (and transitively
    /// <c>ReservationsDbContext</c>) directly — proven, via a real
    /// generated-chain dispatch and real SQL evidence (<c>WHERE FALSE</c> on
    /// <c>CleaningAssigned</c>'s projection lookup), to materialize
    /// <c>ReservationsDbContext</c> with an <c>ITenantContext</c> instance
    /// different from the one <c>TenantResolutionMiddleware</c> resolved for
    /// that message — the same mechanism ADR-015 documented for
    /// Housekeeping. <see cref="IIntegrationEventHandler{TEvent}"/> mappings
    /// below are resolved exclusively from inside
    /// <see cref="ReservationsMessageExecutionScope"/>'s own child scope,
    /// never from Wolverine's per-message DI resolution.
    /// </summary>
    public static IServiceCollection AddReservationsScheduleProjectionConsumer(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IReservationsTransactionExecutor, ReservationsOutboxTransactionExecutor>();
        services.AddScoped<CleaningScheduleProjectionSynchronizer>();

        // Keyed (Fase 7, Incremento 2, Checkpoint 1 — real-Worker regression
        // found and fixed): IIntegrationEventHandler<T> is a shared generic
        // interface, and Dashboard now ALSO registers handlers for these
        // exact ten Cleaning lifecycle event types in the same
        // IHostPro.Worker DI container. GetRequiredService<T>() for a type
        // with multiple registrations silently returns the LAST one
        // registered — an unkeyed registration here would let Dashboard's
        // own module (registered after this one) shadow Reservations' own
        // handler resolution. See ReservationsMessageExecutionScope.
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCreated>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningAssigned>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInTransit>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningStarted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInspectionStarted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCompleted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningInterrupted>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningNeedsHelp>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningNeedsMaterial>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<CleaningCancelled>, CleaningScheduleProjectionSynchronizer>(
            ReservationsMessageExecutionScope.HandlerKey);

        services.AddScoped<IReservationsMessageExecutionScope, ReservationsMessageExecutionScope>();

        return services;
    }

    /// <summary>
    /// The minimal composition root for consuming External Integrations' own
    /// Airbnb reservation events inside <c>IHostPro.Worker</c> (Fase 9,
    /// Checkpoint 3.2 — "Airbnb Deterministic Foundation") — mirrors
    /// <see cref="AddReservationsScheduleProjectionConsumer"/>'s own
    /// structure exactly, a deliberately separate method from
    /// <see cref="ReservationsCommandDispatchExtensions.AddReservationsCommandDispatch"/>
    /// (Api-only, HTTP command/query dispatch). Each processor is resolved
    /// exclusively from <see cref="ReservationsMessageExecutionScope"/>'s own
    /// child DI scope (ADR-016), never from Wolverine's per-message
    /// resolution — same keyed-DI convention as the Cleaning lifecycle
    /// handlers above, even though none of these three event types is shared
    /// with another Bounded Context in this process (no
    /// <c>AddStickyHandler</c> risk, ADR-020's own "single discovered
    /// handler" default) — keyed registration here is required regardless,
    /// because <see cref="ReservationsMessageExecutionScope"/>'s own
    /// implementation always resolves via <c>GetRequiredKeyedService</c>.
    /// </summary>
    public static IServiceCollection AddReservationsAirbnbImportConsumer(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IReservationsTransactionExecutor, ReservationsOutboxTransactionExecutor>();
        services.AddScoped<IReservationReader, ReservationReader>();
        services.AddScoped<IRepository<Reservation, Guid>, ReservationRepository>();

        services.AddKeyedScoped<IIntegrationEventHandler<AirbnbReservationImported>, AirbnbReservationImportedProcessor>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<AirbnbReservationUpdated>, AirbnbReservationUpdatedProcessor>(
            ReservationsMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<AirbnbReservationCancelled>, AirbnbReservationCancelledProcessor>(
            ReservationsMessageExecutionScope.HandlerKey);

        services.AddScoped<IReservationsMessageExecutionScope, ReservationsMessageExecutionScope>();

        return services;
    }

    /// <summary>
    /// The minimal composition root for consuming Workflow Orchestration's
    /// own <see cref="CloseReservation"/> cross-context command inside
    /// <c>IHostPro.Worker</c> (Fase 10, Checkpoint 1 — Guest Operations
    /// Foundation) — mirrors <see cref="AddReservationsAirbnbImportConsumer"/>'s
    /// own structure exactly, a deliberately separate method from
    /// <see cref="ReservationsCommandDispatchExtensions.AddReservationsCommandDispatch"/>
    /// (Api-only, HTTP command/query dispatch). Resolved exclusively from
    /// <see cref="ReservationsMessageExecutionScope"/>'s own child DI scope
    /// (ADR-016), never from Wolverine's per-message resolution.
    /// </summary>
    public static IServiceCollection AddReservationsCloseReservationCommand(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IReservationsTransactionExecutor, ReservationsOutboxTransactionExecutor>();
        services.AddScoped<IRepository<Reservation, Guid>, ReservationRepository>();
        services.AddScoped<ICloseReservationHandler, CloseReservationCommandHandler>();

        services.AddScoped<IReservationsMessageExecutionScope, ReservationsMessageExecutionScope>();

        return services;
    }

    /// <summary>
    /// The minimal composition root for consuming Workflow Orchestration's
    /// own <see cref="RescheduleReservationForEarlyCheckIn"/>/
    /// <see cref="RescheduleReservationForLateCheckout"/> cross-context
    /// commands inside <c>IHostPro.Worker</c> (Fase 10, Checkpoint 3 — Early
    /// Check-in/Late Checkout) — mirrors
    /// <see cref="AddReservationsCloseReservationCommand"/>'s own structure
    /// exactly. <see cref="IReservationConflictGuard"/> is registered here
    /// (not shared with <see cref="ReservationsCommandDispatchExtensions.AddReservationsCommandDispatch"/>,
    /// Api-only by design) because these two handlers re-run Reservations'
    /// own real conflict guard before mutating — Guest Operations' own
    /// <see cref="IReservationScheduleReader"/> read is an eligibility
    /// check, never a substitute for this transactional invariant.
    /// </summary>
    public static IServiceCollection AddReservationsRescheduleCommands(this IServiceCollection services)
    {
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IReservationsTransactionExecutor, ReservationsOutboxTransactionExecutor>();
        services.AddScoped<IRepository<Reservation, Guid>, ReservationRepository>();
        services.AddScoped<IReservationConflictGuard, ReservationConflictGuard>();
        services.AddScoped<IRescheduleReservationForEarlyCheckInHandler, RescheduleReservationForEarlyCheckInCommandHandler>();
        services.AddScoped<IRescheduleReservationForLateCheckoutHandler, RescheduleReservationForLateCheckoutCommandHandler>();

        services.AddScoped<IReservationsMessageExecutionScope, ReservationsMessageExecutionScope>();

        return services;
    }
}
