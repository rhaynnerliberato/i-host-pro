using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Infrastructure.Messaging;
using IHostPro.Contexts.Communication.Infrastructure.Persistence;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Communication.Infrastructure;

/// <summary>
/// Single composition-root entry point for the Communication module (Fase
/// 9, Checkpoint 1) — mirrors <c>DashboardModuleExtensions</c> exactly.
/// Consumed exclusively in <c>IHostPro.Worker</c> this checkpoint —
/// <c>IHostPro.Api</c> never references it (no HTTP surface, CP1 mandate
/// §44/§45).
/// </summary>
public static class CommunicationModuleExtensions
{
    public static IServiceCollection AddCommunicationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<CommunicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Communication"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "communication")));

        services.AddSingleton(TimeProvider.System);

        // Fase 9, Checkpoint 2.3.3: moved here from AddCommunicationReservationConsumer
        // (still below, Development-gated) — these three are plain
        // persistence/execution-scope infrastructure with no secret and no
        // fake/real distinction of their own, and the new WhatsApp status
        // consumer (AddCommunicationWhatsAppStatusConsumer, unconditional)
        // needs them in every environment, not just Development. Moving the
        // registration changes nothing about what gets registered — only
        // which method registers it — so ReservationCreatedCommunicationProcessor's
        // own dependency graph is unaffected.
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<ICommunicationTransactionExecutor, CommunicationTransactionExecutor>();
        services.AddScoped<ICommunicationMessageExecutionScope, CommunicationMessageExecutionScope>();

        return services;
    }

    /// <summary>
    /// Registers everything <see cref="ReservationCreatedCommunicationProcessor"/>'s
    /// own dependency graph needs beyond <see cref="AddCommunicationModule"/>,
    /// so <see cref="CommunicationMessageExecutionScope"/> (ADR-016) can
    /// construct it from its own child DI scope — mirrors
    /// <c>AddDashboardProjectionConsumer</c>'s own two-call split exactly.
    /// Development-only (CP1 mandate §46-49, Option A): the real Meta
    /// connector is never wired into this automatic flow.
    /// </summary>
    public static IServiceCollection AddCommunicationReservationConsumer(this IServiceCollection services)
    {
        // Fase 9, Checkpoint 1 (CP1 mandate §11/§13): the ONLY connector
        // implementation this checkpoint has — a deterministic fake, never a
        // real WhatsApp API client.
        services.AddScoped<IOutboundMessageConnector, FakeWhatsAppConnector>();

        services.AddKeyedScoped<IIntegrationEventHandler<ReservationCreated>, ReservationCreatedCommunicationProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }

    /// <summary>
    /// Registers <see cref="WhatsAppMessageStatusCommunicationProcessor"/>
    /// (Fase 9, Checkpoint 2.3.3, ADR-022 item 14) — unconditional in every
    /// environment, unlike <see cref="AddCommunicationReservationConsumer"/>
    /// above: the inbound webhook status path has no fake/real connector
    /// distinction of its own (the signature-verified webhook itself is
    /// always real), so gating it to Development would silently drop real
    /// status updates outside that environment.
    /// </summary>
    public static IServiceCollection AddCommunicationWhatsAppStatusConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<WhatsAppMessageStatusChanged>, WhatsAppMessageStatusCommunicationProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }

    /// <summary>
    /// Registers the three Front Desk ("Portaria") notification processors
    /// (Fase 10, Checkpoint 4 — Portaria Notification Foundation) — mirrors
    /// <see cref="AddCommunicationReservationConsumer"/>'s own shape and gate
    /// exactly: these reuse the SAME <see cref="IOutboundMessageConnector"/>
    /// registration (<see cref="FakeWhatsAppConnector"/>, Development-only)
    /// as the reservation-confirmation consumer, so this method must be
    /// called alongside it, never independently, and under the same
    /// <c>IsDevelopment()</c> gate at the call site.
    /// </summary>
    public static IServiceCollection AddCommunicationFrontDeskConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<GuestCheckedIn>, GuestCheckedInFrontDeskNotificationProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<EarlyCheckinApproved>, EarlyCheckinApprovedFrontDeskNotificationProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);
        services.AddKeyedScoped<IIntegrationEventHandler<LateCheckoutApproved>, LateCheckoutApprovedFrontDeskNotificationProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }

    /// <summary>
    /// Registers <see cref="PixChargeCreatedDeliveryProcessor"/> (Fase 10,
    /// Checkpoint 5 — PIX/Payment Deterministic Foundation) — mirrors
    /// <see cref="AddCommunicationFrontDeskConsumer"/>'s own shape and gate
    /// exactly: reuses the SAME <see cref="IOutboundMessageConnector"/>
    /// registration (<see cref="FakeWhatsAppConnector"/>, Development-only)
    /// as every other Communication consumer, so this method must be called
    /// alongside it, never independently, and under the same
    /// <c>IsDevelopment()</c> gate at the call site.
    /// </summary>
    public static IServiceCollection AddCommunicationPixDeliveryConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<PixChargeCreated>, PixChargeCreatedDeliveryProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }
}
