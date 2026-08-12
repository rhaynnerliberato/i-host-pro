using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="GetOwnCleaningDetailQuery"/>
public sealed class GetOwnCleaningDetailQueryHandler : IQueryHandler<GetOwnCleaningDetailQuery, CleaningResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);

    private readonly ICleaningReader _reader;

    public GetOwnCleaningDetailQueryHandler(ICleaningReader reader) => _reader = reader;

    public async ValueTask<Result<CleaningResult>> Handle(GetOwnCleaningDetailQuery query, CancellationToken cancellationToken)
    {
        var cleaning = await _reader.GetByIdForHousekeeperAsync(query.CleaningId, query.HousekeeperUserId, cancellationToken);

        return cleaning is null
            ? Result.Failure<CleaningResult>(CleaningNotFoundError)
            : Result.Success(cleaning);
    }
}
