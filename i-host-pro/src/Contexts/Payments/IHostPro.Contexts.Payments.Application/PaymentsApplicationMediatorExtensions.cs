using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Registers Mediator's generated dispatch (<c>IMediator</c>/<c>ISender</c>)
/// and every handler in this assembly — mirrors
/// <c>HousekeepingApplicationMediatorExtensions</c> exactly, including the
/// <c>ServiceLifetime.Scoped</c> requirement (Mediator's Singleton default
/// would cache each handler/behavior chain from the root provider, turning
/// any Scoped dependency reached from a handler — here,
/// <c>PaymentsDbContext</c> — into a de-facto singleton shared by every
/// concurrent request).
///
/// Fase 11, Checkpoint 3 — Payments never needed Mediator before this
/// checkpoint (no Application Query existed). Called directly from
/// <c>AddPaymentsModule</c> (not from a separate Api-only CommandDispatch
/// extension, since Payments has no Api project and its only current
/// consumer, <c>GetPaymentStatusByReservationQuery</c>, is the AI Agent's own
/// Worker-hosted Read Tool, Exception #3).
/// </summary>
public static class PaymentsApplicationMediatorExtensions
{
    public static IServiceCollection AddPaymentsApplicationMediator(this IServiceCollection services)
    {
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddScoped<IPaymentsRequestDispatcher, PaymentsRequestDispatcher>();
        return services;
    }
}
