using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.BuildingBlocks.Infrastructure.Persistence;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.Payments.Domain;
using IHostPro.Contexts.Payments.Infrastructure.Communication;
using IHostPro.Contexts.Payments.Infrastructure.Messaging;
using IHostPro.Contexts.Payments.Infrastructure.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Payments.Infrastructure;

/// <summary>
/// Composition-root entry points for the Payments module (Fase 10,
/// Checkpoint 5 — PIX/Payment Deterministic Foundation) — mirrors
/// <c>GuestOperationsModuleExtensions</c>'s own split exactly: a base module
/// registration (DbContext + the read-only <see cref="IPixChargeDeliveryReader"/>
/// Communication needs — ADR-027, exception #11) and a Worker-only consumer
/// registration (the <see cref="LateCheckoutPaymentRequired"/>/
/// <see cref="PixChargeConfirmationReceived"/> handlers).
/// </summary>
public static class PaymentsModuleExtensions
{
    /// <summary>
    /// The base registration every process touching this context's schema
    /// needs. <see cref="IPixChargeDeliveryReader"/> is registered here
    /// (unconditionally, no secret/network dependency of its own) so
    /// Communication's delivery processor can resolve it wherever it runs —
    /// mirrors <c>AddPropertyManagementModule</c>'s own read-only placement
    /// for <c>IFrontDeskContactReader</c> exactly.
    /// </summary>
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PaymentsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Payments"),
                npgsqlOptions => npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "payments")));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<IPixChargeDeliveryReader, PixChargeDeliveryReader>();

        // Fase 11, Checkpoint 3 — Exception #3 (AI Agent Tools -> Application
        // Services): the AI Agent's own Wolverine consumer runs in
        // IHostPro.Worker and needs to execute GetPaymentStatusByReservationQuery
        // in-process via IPaymentsRequestDispatcher — Payments' first-ever
        // Application Query/Mediator usage. Registered directly here (not a
        // separate Api-only CommandDispatch extension) because Payments has
        // no Api project and its only consumer today is the Worker-hosted AI
        // Agent — see PaymentsApplicationMediatorExtensions' own doc comment.
        // No Payments.Api exists today, so AddPaymentsModule has only ever
        // been called by IHostPro.Worker — but see
        // ReservationsModuleExtensions' own identical comment for why the
        // trimming still belongs in Worker's own Program.cs, not here: this
        // shared method must stay generically safe for any future consumer
        // (an eventual Payments.Api included) to call without silently
        // losing write-Command handler registrations it might need.
        // IHostPro.Worker's own Program.cs calls KeepOnlyMediatorHandlers
        // right after this method returns.
        services.AddPaymentsApplicationMediator();
        services.AddScoped<IPixChargeReader, PixChargeReader>();
        services.AddScoped<
            IPipelineBehavior<GetPaymentStatusByReservationQuery, Result<PaymentStatusResult>>,
            TenantTransactionBehavior<GetPaymentStatusByReservationQuery, Result<PaymentStatusResult>, PaymentsDbContext>>();

        return services;
    }

    /// <summary>
    /// Registers everything <see cref="LateCheckoutPaymentRequiredChargeInitializer"/>/
    /// <see cref="PixChargeConfirmationReceivedCommandHandler"/>'s own
    /// dependency graphs need beyond <see cref="AddPaymentsModule"/>, so
    /// <see cref="PaymentsMessageExecutionScope"/> (ADR-015/016) can
    /// construct them from its own child DI scope — mirrors
    /// <c>AddGuestOperationsReservationCreatedConsumer</c>'s own structure
    /// exactly. Called only by <c>IHostPro.Worker</c>.
    /// </summary>
    public static IServiceCollection AddPaymentsLateCheckoutPaymentRequiredConsumer(this IServiceCollection services)
    {
        services.AddScoped<IRepository<PixCharge, Guid>, PixChargeRepository>();
        // IPixChargeReader is registered by AddPaymentsModule (Fase 11,
        // Checkpoint 3) — no longer duplicated here.
        services.AddScoped<Application.IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IPaymentsTransactionExecutor, PaymentsOutboxTransactionExecutor>();

        services.AddKeyedScoped<IIntegrationEventHandler<LateCheckoutPaymentRequired>, LateCheckoutPaymentRequiredChargeInitializer>(
            PaymentsMessageExecutionScope.HandlerKey);

        services.AddScoped<IPixChargeConfirmationReceivedHandler, PixChargeConfirmationReceivedCommandHandler>();

        // Fase 10, Checkpoint 5.1 (Payment Failure/Expiration Evidence
        // Corrective Gate): same provider-neutral inbound seam as
        // PixChargeConfirmationReceived above — see IPaymentsMessageExecutionScope's
        // own two new dedicated methods.
        services.AddScoped<IPixChargeFailureReceivedHandler, PixChargeFailureReceivedCommandHandler>();
        services.AddScoped<IPixChargeExpirationReceivedHandler, PixChargeExpirationReceivedCommandHandler>();

        services.AddScoped<IPaymentsMessageExecutionScope, PaymentsMessageExecutionScope>();

        return services;
    }
}
