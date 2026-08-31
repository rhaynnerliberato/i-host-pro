using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="GetCleaningStatusByReservationQuery"/>
public sealed class GetCleaningStatusByReservationQueryHandler
    : IQueryHandler<GetCleaningStatusByReservationQuery, CleaningStatusResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);

    private readonly ICleaningReader _reader;

    public GetCleaningStatusByReservationQueryHandler(ICleaningReader reader) => _reader = reader;

    public async ValueTask<Result<CleaningStatusResult>> Handle(
        GetCleaningStatusByReservationQuery query, CancellationToken cancellationToken)
    {
        var result = await _reader.GetStatusByReservationIdAsync(query.ReservationId, cancellationToken);

        return result is null
            ? Result.Failure<CleaningStatusResult>(CleaningNotFoundError)
            : Result.Success(result);
    }
}
