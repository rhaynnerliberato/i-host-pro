using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure.Messaging;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.GuestOperations.Infrastructure;

/// <summary>
/// Composition-root entry points for the Guest Operations module (Fase 10,
/// Checkpoint 1 — Guest Operations Foundation; Checkpoint 2 —
/// Check-in/Checkout Core) — mirrors <c>ReservationsModuleExtensions</c>'
/// own split: a base module registration (DbContext, needed everywhere this
/// context's schema is touched) and a Worker-only consumer registration (the
/// new <c>ReservationCreated</c> choreography, Checkpoint 2). The Api-only
/// command-dispatch registration lives separately in
/// <c>GuestOperationsCommandDispatchExtensions</c> (mirrors
/// <c>ReservationsCommandDispatchExtensions</c>'s own separate placement).
/// </summary>
public static class GuestOperationsModuleExtensions
{
    /// <summary>
    /// The base registration every process touching this context's schema
    /// needs — called by both <c>IHostPro.Api</c> (check-in/checkout
    /// commands) and, since Checkpoint 2, <c>IHostPro.Worker</c> (the new
    /// <c>ReservationCreated</c> consumer writes to the same DbContext).
    /// </summary>
    public static IServiceCollection AddGuestOperationsModule(
        this IServiceCollection services, IConfiguration configuration)
    {
        // The migrations history table must live inside the module's own
        // schema, never the default `public` — mirrors every other module's
        // own registration.
        services.AddDbContext<GuestOperationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("GuestOperations"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "guest_operations")));

        services.AddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// The minimal composition root for consuming Reservations' own
    /// <c>ReservationCreated</c> event inside <c>IHostPro.Worker</c> (Fase
    /// 10, Checkpoint 2 — the resolved creation-trigger governance gate:
    /// auto-create <c>GuestStayOperation</c> via choreography). Mirrors
    /// <c>ReservationsModuleExtensions.AddReservationsScheduleProjectionConsumer</c>'s
    /// own structure exactly — deliberately separate from
    /// <c>GuestOperationsCommandDispatchExtensions.AddGuestOperationsCommandDispatch</c>
    /// (Api-only, HTTP command dispatch). Resolved exclusively from
    /// <see cref="GuestOperationsMessageExecutionScope"/>'s own child DI
    /// scope (ADR-015/016), never from Wolverine's per-message resolution.
    /// </summary>
    public static IServiceCollection AddGuestOperationsReservationCreatedConsumer(this IServiceCollection services)
    {
        services.AddScoped<IRepository<GuestStayOperation, Guid>, GuestStayOperationRepository>();
        services.AddScoped<IGuestStayOperationReader, GuestStayOperationReader>();
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IGuestOperationsTransactionExecutor, GuestOperationsOutboxTransactionExecutor>();

        services.AddKeyedScoped<IIntegrationEventHandler<ReservationCreated>, ReservationCreatedGuestStayInitializer>(
            GuestOperationsMessageExecutionScope.HandlerKey);

        services.AddScoped<IGuestOperationsMessageExecutionScope, GuestOperationsMessageExecutionScope>();

        return services;
    }

    /// <summary>
    /// The composition root for consuming Payments' own
    /// <see cref="PixChargeConfirmed"/> event inside <c>IHostPro.Worker</c>
    /// (Fase 10, Checkpoint 5 — PIX/Payment Deterministic Foundation).
    /// Mirrors <see cref="AddGuestOperationsReservationCreatedConsumer"/>'s
    /// own structure exactly — <see cref="IGuestOperationsMessageExecutionScope"/>
    /// is re-registered here too (harmless — the last registration wins for
    /// a given service type) so this method also works if ever called
    /// without <see cref="AddGuestOperationsReservationCreatedConsumer"/> in
    /// the same composition root. <see cref="Configuration.Contracts.ILateCheckoutPolicyReader"/>
    /// is NOT registered here — it is already registered unconditionally by
    /// <c>AddConfigurationModule</c>, which every process consuming this
    /// method already calls.
    /// </summary>
    public static IServiceCollection AddGuestOperationsPixChargeConfirmedConsumer(this IServiceCollection services)
    {
        services.AddScoped<IRepository<LateCheckoutRequest, Guid>, LateCheckoutRequestRepository>();
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IGuestOperationsTransactionExecutor, GuestOperationsOutboxTransactionExecutor>();

        services.AddKeyedScoped<IIntegrationEventHandler<PixChargeConfirmed>, PixChargeConfirmedLateCheckoutApprover>(
            GuestOperationsMessageExecutionScope.HandlerKey);

        services.AddScoped<IGuestOperationsMessageExecutionScope, GuestOperationsMessageExecutionScope>();

        return services;
    }
}
