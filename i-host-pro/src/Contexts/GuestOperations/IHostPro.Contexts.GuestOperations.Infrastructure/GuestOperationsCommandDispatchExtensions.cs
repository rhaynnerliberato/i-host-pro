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
///
/// Fase 10, Checkpoint 3 — Early Check-in / Late Checkout adds
/// <c>RequestEarlyCheckInCommand</c>/<c>RequestLateCheckoutCommand</c> to the
/// same Mediator scan (this method's own assembly), so only their NEW
/// dependencies are registered here: the two request repositories/readers,
/// plus the cross-context synchronous readers they call
/// (<c>IReservationScheduleReader</c> — already registered by
/// <c>AddReservationsModule</c>, which <c>IHostPro.Api</c> calls
/// unconditionally; <c>ICleaningReadinessReader</c> — already registered by
/// <c>AddHousekeepingModule</c>, same reasoning; <c>IEarlyCheckInPolicyReader</c>/
/// <c>ILateCheckoutPolicyReader</c> — already registered by Configuration's
/// own base module). None of those three modules' registrations are
/// repeated here — this method only adds what Guest Operations itself owns.
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

        services.AddScoped<IRepository<EarlyCheckInRequest, Guid>, EarlyCheckInRequestRepository>();
        services.AddScoped<IEarlyCheckInRequestReader, EarlyCheckInRequestReader>();
        services.AddScoped<IRepository<LateCheckoutRequest, Guid>, LateCheckoutRequestRepository>();
        services.AddScoped<ILateCheckoutRequestReader, LateCheckoutRequestReader>();

        return services;
    }
}
