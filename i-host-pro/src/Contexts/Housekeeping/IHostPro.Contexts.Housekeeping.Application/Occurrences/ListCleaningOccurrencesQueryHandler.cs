using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <inheritdoc cref="ListCleaningOccurrencesQuery"/>
/// <remarks>
/// Verifies the parent Cleaning itself belongs to the caller (via
/// <see cref="ICleaningReader.GetByIdForHousekeeperAsync"/> — same fail-closed
/// 404 as every other own-cleaning lookup) before listing its occurrences,
/// rather than relying solely on the reader's own join to produce an empty
/// list for a non-owned id.
/// </remarks>
public sealed class ListCleaningOccurrencesQueryHandler : IQueryHandler<ListCleaningOccurrencesQuery, IReadOnlyList<CleaningOccurrenceResult>>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);

    private readonly ICleaningReader _cleaningReader;
    private readonly ICleaningOccurrenceReader _occurrenceReader;

    public ListCleaningOccurrencesQueryHandler(ICleaningReader cleaningReader, ICleaningOccurrenceReader occurrenceReader)
    {
        _cleaningReader = cleaningReader;
        _occurrenceReader = occurrenceReader;
    }

    public async ValueTask<Result<IReadOnlyList<CleaningOccurrenceResult>>> Handle(
        ListCleaningOccurrencesQuery query, CancellationToken cancellationToken)
    {
        var cleaning = await _cleaningReader.GetByIdForHousekeeperAsync(query.CleaningId, query.HousekeeperUserId, cancellationToken);
        if (cleaning is null)
            return Result.Failure<IReadOnlyList<CleaningOccurrenceResult>>(CleaningNotFoundError);

        var occurrences = await _occurrenceReader.ListForOwnCleaningAsync(query.CleaningId, query.HousekeeperUserId, cancellationToken);
        return Result.Success(occurrences);
    }
}
