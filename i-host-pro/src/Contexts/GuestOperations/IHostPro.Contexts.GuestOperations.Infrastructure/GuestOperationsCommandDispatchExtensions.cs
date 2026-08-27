using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.GuestOperations.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace IHostPro.Contexts.GuestOperations.Infrastructure;

/// <summary>
/// Single composition-root entry point for dispatching Guest Operations'
/// check-in/checkout Commands (Fase 10, Checkpoint 2 — Check-in/Checkout
/// Core) — mirrors <c>ReservationsCommandDispatchExtensions</c>/
/// <c>HousekeepingCommandDispatchExtensions</c> exactly. Called ONLY from
/// <c>IHostPro.Api</c>'s composition root, never from <c>IHostPro.Worker</c>'s:
/// dispatching a Command is an HTTP-request concern.
///
/// <see cref="RecordGuestCheckedInCommand"/>/<see cref="RecordGuestCheckedOutCommand"/>
/// deliberately get NO wrapping pipeline behavior — mirrors
/// <c>CreateReservationCommand</c>'s own precedent exactly: each handler
/// injects <see cref="IGuestOperationsTransactionExecutor"/> directly and
/// opens this context's write transaction itself at the exact point it
/// needs to (after resolving the existing <c>GuestStayOperation</c> by
/// Reservation id). No FluentValidation either — both commands carry only a
/// route-bound Reservation id and the caller's own tenant id, nothing a
/// validator would meaningfully check.
/// </summary>
public static class GuestOperationsCommandDispatchExtensions
{
    public static IServiceCollection AddGuestOperationsCommandDispatch(this IServiceCollection services)
    {
        services.AddGuestOperationsApplicationMediator();

        services.AddScoped<IIntegrationEventCollector, IntegrationEventCollector>();
        services.AddScoped<IGuestOperationsTransactionExecutor, GuestOperationsOutboxTransactionExecutor>();
        services.AddScoped<IRepository<GuestStayOperation, Guid>, GuestStayOperationRepository>();
        services.AddScoped<IGuestStayOperationReader, GuestStayOperationReader>();

        return services;
    }
}
