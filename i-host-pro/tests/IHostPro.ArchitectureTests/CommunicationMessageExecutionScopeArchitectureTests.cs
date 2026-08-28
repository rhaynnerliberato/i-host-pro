using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Communication.Infrastructure.Messaging;
using NetArchTest.Rules;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Enforces ADR-016 (fourth application, Fase 9, Checkpoint 1 — Comunicação
/// e Integrações do MVP), generalizing Housekeeping's/Reservations'/
/// Dashboard's own findings to Communication. <c>CommunicationMessageExecutionScope</c>
/// is the single, deliberately-authorized boundary holding
/// <see cref="IServiceScopeFactory"/> — the single thin Wolverine adapter
/// (<c>ReservationCreatedHandler</c>) must keep depending only on the
/// ordinary constructor-injected graph, never resolving
/// <c>CommunicationDbContext</c>/<c>ICommunicationTransactionExecutor</c>/
/// the processor directly. Mirrors <c>DashboardMessageExecutionScopeArchitectureTests</c>
/// exactly.
/// </summary>
public class CommunicationMessageExecutionScopeArchitectureTests
{
    private static readonly Type[] CommunicationAssemblyAnchors =
    [
        typeof(Message),
        typeof(ICommunicationMessageExecutionScope),
        typeof(CommunicationMessageExecutionScope),
    ];

    [Fact]
    public void Only_CommunicationMessageExecutionScope_May_Depend_On_IServiceScopeFactory()
    {
        var typesDependingOnScopeFactory = CommunicationAssemblyAnchors
            .Select(anchor => anchor.Assembly)
            .Distinct()
            .SelectMany(assembly => Types.InAssembly(assembly)
                .That()
                .HaveDependencyOn("Microsoft.Extensions.DependencyInjection.IServiceScopeFactory")
                .GetTypes())
            .Distinct()
            .ToList();

        typesDependingOnScopeFactory.Should().ContainSingle()
            .Which.Should().Be(typeof(CommunicationMessageExecutionScope),
                "ICommunicationMessageExecutionScope's own implementation is the single, deliberately-authorized " +
                "holder of IServiceScopeFactory in Communication (ADR-016) — any other match means a new class " +
                "started resolving its own child scope outside the approved boundary.");
    }

    [Fact]
    public void Wolverine_Adapter_Never_Depends_On_CommunicationDbContext_Or_TransactionExecutor_Or_Processor_Or_ServiceScopeFactory()
    {
        // The thin Wolverine entrypoints (ReservationCreatedHandler,
        // WhatsAppMessageStatusChangedHandler) may depend only on their own
        // message type, MessageContext, ICommunicationMessageExecutionScope
        // and CancellationToken — never directly on persistence/transaction/
        // scope-factory/processor types Wolverine's own codegen would
        // otherwise try to inline.
        var adapterTypes = Types.InAssembly(typeof(CommunicationMessageExecutionScope).Assembly)
            .That()
            .ResideInNamespace("IHostPro.Contexts.Communication.Infrastructure.Messaging")
            .And()
            .DoNotHaveName(nameof(CommunicationMessageExecutionScope))
            .GetTypes();

        adapterTypes.Should().HaveCount(9,
            "nine types are expected in this namespace: ReservationCreatedHandler and " +
            "WhatsAppMessageStatusChangedHandler (the thin Wolverine adapters, Fase 9, Checkpoint 2.3.3), " +
            "GuestCheckedInHandler/EarlyCheckinApprovedHandler/LateCheckoutApprovedHandler (Fase 10, Checkpoint 4 — " +
            "Portaria Notification Foundation), PixChargeCreatedHandler (Fase 10, Checkpoint 5 — PIX/Payment " +
            "Deterministic Foundation), GuestAccessDeliveryRequestedHandler (Fase 10, Checkpoint 6.2 — Guest " +
            "Access Secure Delivery Corrective Implementation), FakeWhatsAppConnector (the CP1 deterministic " +
            "connector double), and ExternalIntegrationsWhatsAppConnector (Checkpoint 2.2's real " +
            "IOutboundMessageConnector, not wired into this Wolverine-triggered flow — see its own doc comment)");

        var forbiddenDependencies = new[]
        {
            "IHostPro.Contexts.Communication.Infrastructure.Persistence.CommunicationDbContext",
            "IHostPro.Contexts.Communication.Application.ICommunicationTransactionExecutor",
            "IHostPro.Contexts.Communication.Application.ReservationCreatedCommunicationProcessor",
            "IHostPro.Contexts.Communication.Application.WhatsAppMessageStatusCommunicationProcessor",
            "IHostPro.Contexts.Communication.Application.GuestCheckedInFrontDeskNotificationProcessor",
            "IHostPro.Contexts.Communication.Application.EarlyCheckinApprovedFrontDeskNotificationProcessor",
            "IHostPro.Contexts.Communication.Application.LateCheckoutApprovedFrontDeskNotificationProcessor",
            "IHostPro.Contexts.Communication.Application.PixChargeCreatedDeliveryProcessor",
            "IHostPro.Contexts.Communication.Application.GuestAccessDeliveryProcessor",
            "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
            "Wolverine.EntityFrameworkCore.IDbContextOutbox",
        };

        foreach (var adapterName in new[]
                 {
                     nameof(ReservationCreatedHandler), nameof(WhatsAppMessageStatusChangedHandler),
                     nameof(GuestCheckedInHandler), nameof(EarlyCheckinApprovedHandler), nameof(LateCheckoutApprovedHandler),
                     nameof(PixChargeCreatedHandler), nameof(GuestAccessDeliveryRequestedHandler),
                 })
        {
            var result = Types.InAssembly(typeof(CommunicationMessageExecutionScope).Assembly)
                .That()
                .HaveName(adapterName)
                .Should()
                .NotHaveDependencyOnAny(forbiddenDependencies)
                .GetResult();

            result.IsSuccessful.Should().BeTrue(
                $"{adapterName} must depend only on ICommunicationMessageExecutionScope for anything " +
                "Communication-persistence-related (ADR-016)");
        }
    }
}
