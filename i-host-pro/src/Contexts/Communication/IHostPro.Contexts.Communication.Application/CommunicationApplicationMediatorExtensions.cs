using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Communication.Application;

/// <summary>
/// Registers Mediator's generated dispatch (<c>IMediator</c>/<c>ISender</c>)
/// and every handler in this assembly — mirrors
/// <c>PaymentsApplicationMediatorExtensions</c> exactly, including the
/// <c>ServiceLifetime.Scoped</c> requirement (Mediator's Singleton default
/// would cache each handler/behavior chain from the root provider, turning
/// any Scoped dependency reached from a handler — here,
/// <c>CommunicationDbContext</c> — into a de-facto singleton shared by every
/// concurrent request).
///
/// Fase 11, Checkpoint 4 — Communication never needed Mediator before this
/// checkpoint (every outbound send was event-reactive, never a caller-
/// invoked Command). Called directly from <c>AddCommunicationModule</c> (not
/// a separate Api-only CommandDispatch extension — Communication has no Api
/// project; its only consumer, <see cref="SendAgentResponseCommand"/>, is
/// the AI Agent's own Worker-hosted orchestrator, Exception #3).
/// </summary>
public static class CommunicationApplicationMediatorExtensions
{
    public static IServiceCollection AddCommunicationApplicationMediator(this IServiceCollection services)
    {
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddScoped<ICommunicationRequestDispatcher, CommunicationRequestDispatcher>();
        return services;
    }
}
