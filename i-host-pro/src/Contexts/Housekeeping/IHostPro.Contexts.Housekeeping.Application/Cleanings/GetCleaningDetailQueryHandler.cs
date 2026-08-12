using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="GetCleaningDetailQuery"/>
public sealed class GetCleaningDetailQueryHandler : IQueryHandler<GetCleaningDetailQuery, CleaningResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);

    private readonly ICleaningReader _reader;

    public GetCleaningDetailQueryHandler(ICleaningReader reader) => _reader = reader;

    public async ValueTask<Result<CleaningResult>> Handle(GetCleaningDetailQuery query, CancellationToken cancellationToken)
    {
        var cleaning = await _reader.GetByIdAsync(query.CleaningId, cancellationToken);

        return cleaning is null
            ? Result.Failure<CleaningResult>(CleaningNotFoundError)
            : Result.Success(cleaning);
    }
}
