using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.AIAgent.Application;

/// <summary>
/// Registers Mediator's generated dispatch (<c>IMediator</c>/<c>ISender</c>)
/// and every handler in this assembly — mirrors
/// <c>CommunicationApplicationMediatorExtensions</c> exactly, including the
/// <c>ServiceLifetime.Scoped</c> requirement.
///
/// Fase 11, Checkpoint 6 — AIAgent never needed Mediator before this
/// checkpoint (every flow was event-reactive, never a caller-invoked
/// Command). Unlike Communication's own precedent, this IS a separate
/// Api-only extension (<c>AIAgentCommandDispatchExtensions.AddAIAgentCommandDispatch</c>,
/// Infrastructure) rather than folded into the shared <c>AddAIAgentModule</c>
/// — <see cref="ResumeAgentSessionCommand"/>'s only real consumer is
/// <c>IHostPro.Api</c>'s own Resume-session endpoint; <c>IHostPro.Worker</c>
/// never calls it, so it stays out of Worker's composition entirely
/// (mirrors the original, pre-Checkpoint-4 <c>GuestOperationsCommandDispatchExtensions</c>
/// shape, before that context's own write Tools forced its Command surface
/// into the shared module).
/// </summary>
public static class AIAgentApplicationMediatorExtensions
{
    public static IServiceCollection AddAIAgentApplicationMediator(this IServiceCollection services)
    {
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddScoped<IAIAgentRequestDispatcher, AIAgentRequestDispatcher>();
        return services;
    }
}
