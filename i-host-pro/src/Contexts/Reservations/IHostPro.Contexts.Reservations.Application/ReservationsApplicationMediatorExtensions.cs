using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.Reservations.Application;

/// <summary>
/// Registers Mediator's generated dispatch (<c>IMediator</c>/<c>ISender</c>)
/// and every handler in this assembly — mirrors
/// <c>PropertyManagementApplicationMediatorExtensions</c>/
/// <c>IdentityApplicationMediatorExtensions</c> exactly, including the
/// <c>ServiceLifetime.Scoped</c> requirement (Mediator's Singleton default
/// would cache each handler/behavior chain from the root provider, turning
/// any Scoped dependency reached from a handler — here,
/// <c>ReservationsDbContext</c> — into a de-facto singleton shared by every
/// concurrent request).
/// </summary>
public static class ReservationsApplicationMediatorExtensions
{
    /// <summary>
    /// Registers this project's own generated <c>Mediator.Mediator</c> AND
    /// <see cref="IReservationsRequestDispatcher"/> — the dispatcher
    /// registration must come after <c>AddMediator()</c> so its constructor
    /// can resolve the concrete <c>Mediator.Mediator</c> type <c>AddMediator()</c>
    /// just registered.
    /// </summary>
    public static IServiceCollection AddReservationsApplicationMediator(this IServiceCollection services)
    {
        services.AddMediator(options => options.ServiceLifetime = ServiceLifetime.Scoped);
        services.AddScoped<IReservationsRequestDispatcher, ReservationsRequestDispatcher>();
        return services;
    }
}
