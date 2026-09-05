using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Contracts;
using IHostPro.Contexts.Communication.Infrastructure.AIAgent;
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
    /// <param name="isDevelopmentEnvironment">
    /// CP5.3E corrective fix: selects the <see cref="IOutboundMessageConnector"/>
    /// registered for every Communication outbound-send flow — <see cref="FakeWhatsAppConnector"/>
    /// (Development, unchanged behavior) or <see cref="NotConfiguredOutboundMessageConnector"/>
    /// (every other environment: resolves cleanly via DI but always reports
    /// an explicit, deterministic failure — never a silent fake success,
    /// never a DI resolution exception). Defaults to <see langword="false"/>
    /// so existing test call sites need no change; both real hosts
    /// (<c>IHostPro.Api</c>/<c>IHostPro.Worker</c>) pass their own
    /// <c>IHostEnvironment.IsDevelopment()</c> explicitly, mirroring
    /// <c>AddAIAgentModule</c>/<c>AddPropertyManagementModule</c>'s own
    /// established parameter.
    /// </param>
    public static IServiceCollection AddCommunicationModule(this IServiceCollection services, IConfiguration configuration, bool isDevelopmentEnvironment = false)
    {
        if (isDevelopmentEnvironment)
            services.AddScoped<IOutboundMessageConnector, FakeWhatsAppConnector>();
        else
            services.AddScoped<IOutboundMessageConnector, NotConfiguredOutboundMessageConnector>();

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
        services.AddScoped<ICommunicationTransactionExecutor, CommunicationOutboxTransactionExecutor>();
        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<ICommunicationMessageExecutionScope, CommunicationMessageExecutionScope>();

        // Fase 11, Checkpoint 1 (Inbound Conversation Foundation): every
        // processor that creates a Message (inbound or outbound) needs
        // IConversationResolver, so it is registered here alongside
        // IMessageRepository rather than per-consumer method.
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationResolver, ConversationResolver>();

        // Fase 11, Checkpoint 2 (AI Agent Foundation) — ADR-030, synchronous
        // exception #14. Registered unconditionally like every other reader
        // in this module (no fake/real distinction of its own — it is a
        // plain, always-real read of Communication's own data).
        services.AddScoped<IConversationHistoryReader, ConversationHistoryReader>();

        // Fase 11, Checkpoint 6 (Human Handoff, Safety & Audit) — the
        // recipient for SendHumanHandoffNotificationCommand, and the
        // administrative management surface (Upsert/Get). Registered here,
        // unconditionally, exactly like every other repository in this
        // module — no fake/real distinction of its own (plain persistence).
        services.AddScoped<IAdministratorNotificationContactRepository, AdministratorNotificationContactRepository>();

        // Fase 11, Checkpoint 4 (Write Tools & Response Delivery) —
        // Communication's first Application Command, SendAgentResponseCommand
        // (Documento 13 §30's own synchronous "IA -> Application Service ->
        // Communication" chain). Registered here, unconditionally, rather
        // than a separate Api-only CommandDispatch extension — Communication
        // has no Api project, and this Command's only real consumer is the
        // AI Agent's own Worker-hosted orchestrator (Exception #3). The
        // handler's own IOutboundMessageConnector dependency resolves in
        // every environment via the registration above (CP5.3E corrective
        // fix — previously it resolved only where AddCommunicationReservationConsumer,
        // Development-only, had also been called, so a real, non-Development
        // AI Agent response crashed on DI resolution instead of failing
        // explicitly).
        services.AddCommunicationApplicationMediator();

        return services;
    }

    /// <summary>
    /// Registers <see cref="InboundGuestMessageProcessor"/> (Fase 11,
    /// Checkpoint 1) — unconditional in every environment, same rationale as
    /// <see cref="AddCommunicationWhatsAppStatusConsumer"/>: resolving a
    /// guest phone to a Reservation and persisting an inbound Message has no
    /// fake/real connector distinction of its own (nothing is sent out).
    /// </summary>
    public static IServiceCollection AddCommunicationInboundMessageConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<InboundGuestMessageReceived>, InboundGuestMessageProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }

    /// <summary>
    /// Registers everything <see cref="ReservationCreatedCommunicationProcessor"/>'s
    /// own dependency graph needs beyond <see cref="AddCommunicationModule"/>,
    /// so <see cref="CommunicationMessageExecutionScope"/> (ADR-016) can
    /// construct it from its own child DI scope — mirrors
    /// <c>AddDashboardProjectionConsumer</c>'s own two-call split exactly.
    /// Development-only (CP1 mandate §46-49, Option A): the real Meta
    /// connector is never wired into this automatic flow. The
    /// <see cref="IOutboundMessageConnector"/> itself is registered by
    /// <see cref="AddCommunicationModule"/> (CP5.3E corrective fix — no
    /// longer this method's own responsibility), which already selects
    /// <see cref="FakeWhatsAppConnector"/> for Development.
    /// </summary>
    public static IServiceCollection AddCommunicationReservationConsumer(this IServiceCollection services)
    {
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
    /// exactly: this method must be called alongside it, never
    /// independently, and under the same <c>IsDevelopment()</c> gate at the
    /// call site (the <see cref="IOutboundMessageConnector"/> these
    /// processors depend on is registered by <see cref="AddCommunicationModule"/>,
    /// which selects <see cref="FakeWhatsAppConnector"/> for Development).
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
    /// exactly: this method must be called alongside
    /// <see cref="AddCommunicationReservationConsumer"/>, never
    /// independently, and under the same <c>IsDevelopment()</c> gate at the
    /// call site.
    /// </summary>
    public static IServiceCollection AddCommunicationPixDeliveryConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<PixChargeCreated>, PixChargeCreatedDeliveryProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }

    /// <summary>
    /// Registers <see cref="GuestAccessDeliveryProcessor"/> (Fase 10,
    /// Checkpoint 6.2 — Guest Access Secure Delivery Corrective
    /// Implementation) — mirrors <see cref="AddCommunicationPixDeliveryConsumer"/>'s
    /// own shape and gate exactly: this method must be called alongside
    /// <see cref="AddCommunicationReservationConsumer"/>, never
    /// independently, and under the same <c>IsDevelopment()</c> gate at the
    /// call site.
    /// </summary>
    public static IServiceCollection AddCommunicationGuestAccessDeliveryConsumer(this IServiceCollection services)
    {
        services.AddKeyedScoped<IIntegrationEventHandler<GuestAccessDeliveryRequested>, GuestAccessDeliveryProcessor>(
            CommunicationMessageExecutionScope.HandlerKey);

        return services;
    }
}
