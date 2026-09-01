using IHostPro.Contexts.AIAgent.Application;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.AIAgent.Infrastructure.Context;

/// <inheritdoc cref="IPropertyLocalTimeContextReader"/>
/// <remarks>
/// Two sequential dispatcher calls — Reservations' own <see cref="GetReservationDetailQuery"/>
/// to resolve <c>PropertyId</c>, then PropertyManagement's own
/// <see cref="GetPropertyDetailQuery"/> to resolve <c>TimeZoneId</c> — the
/// exact same two calls, same dispatchers, <see cref="GetPropertyInformationTool"/>
/// already makes (Exceção 3, no new synchronous exception). Returns
/// <see langword="null"/> only when the Reservation itself cannot be
/// resolved (never expected in practice — the session already carries a real
/// <c>ReservationId</c>); a Property lookup failure is not expected either,
/// but is treated the same way rather than throwing, since a missing local
/// time context must never fail the whole interaction on its own.
/// </remarks>
public sealed class PropertyLocalTimeContextReader : IPropertyLocalTimeContextReader
{
    private readonly IReservationsRequestDispatcher _reservationsDispatcher;
    private readonly IPropertyManagementRequestDispatcher _propertyManagementDispatcher;

    public PropertyLocalTimeContextReader(
        IReservationsRequestDispatcher reservationsDispatcher, IPropertyManagementRequestDispatcher propertyManagementDispatcher)
    {
        _reservationsDispatcher = reservationsDispatcher;
        _propertyManagementDispatcher = propertyManagementDispatcher;
    }

    public async Task<PropertyLocalTimeContext?> GetByReservationIdAsync(Guid reservationId, CancellationToken cancellationToken)
    {
        var reservationResult = await _reservationsDispatcher.Send(new GetReservationDetailQuery(reservationId), cancellationToken);
        if (reservationResult.IsFailure)
            return null;

        var propertyResult = await _propertyManagementDispatcher.Send(
            new GetPropertyDetailQuery(reservationResult.Value.PropertyId), cancellationToken);
        if (propertyResult.IsFailure)
            return null;

        return new PropertyLocalTimeContext(reservationResult.Value.PropertyId, propertyResult.Value.TimeZoneId);
    }
}
