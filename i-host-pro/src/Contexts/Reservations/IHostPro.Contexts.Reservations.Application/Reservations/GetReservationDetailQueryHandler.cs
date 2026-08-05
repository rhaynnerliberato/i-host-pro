using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Application.Errors;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <inheritdoc cref="GetReservationDetailQuery"/>
public sealed class GetReservationDetailQueryHandler : IQueryHandler<GetReservationDetailQuery, ReservationResult>
{
    private static readonly Error ReservationNotFoundError = new(
        ReservationsErrorCodes.ReservationNotFound, ReservationsErrorCodes.ReservationNotFound);

    private readonly IReservationReader _reader;

    public GetReservationDetailQueryHandler(IReservationReader reader) => _reader = reader;

    public async ValueTask<Result<ReservationResult>> Handle(GetReservationDetailQuery query, CancellationToken cancellationToken)
    {
        var reservation = await _reader.GetByIdAsync(query.ReservationId, cancellationToken);

        return reservation is null
            ? Result.Failure<ReservationResult>(ReservationNotFoundError)
            : Result.Success(reservation);
    }
}
