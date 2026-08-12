using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Housekeeping.Application;

/// <summary>
/// Registers Mediator's generated dispatch (<c>IMediator</c>/<c>ISender</c>)
/// and every handler in this assembly — mirrors
/// <c>ReservationsApplicationMediatorExtensions</c> exactly, including the
/// <c>ServiceLifetime.Scoped</c> requirement (Mediator's Singleton default
/// would cache each handler/behavior chain from the root provider, turning
/// any Scoped dependency reached from a handler — here,
/// <c>HousekeepingDbContext</c> — into a de-facto singleton shared by every
/// concurrent request).
/// </summary>
public static class HousekeepingApplicationMediatorExtensions
{
    /// <summary>
    /// Registers this project's own generated <c>Mediator.Mediator</c> AND
    /// <see cref="IHousekeepingRequestDispatcher"/> — the dispatcher
    /// registration must come after <c>AddMediator()</c> so its constructor
    /// can resolve the concrete <c>Mediator.Mediator</c> type <c>AddMediator()</c>
    /// just registered.
    /// </summary>
    public static IServiceCollection AddHousekeepingApplicationMediator(this IServiceCollection services)
    {
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddScoped<IHousekeepingRequestDispatcher, HousekeepingRequestDispatcher>();
        return services;
    }
}
