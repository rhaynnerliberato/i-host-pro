using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <inheritdoc cref="GetOwnCleaningChecklistQuery"/>
/// <remarks>
/// Verifies the Cleaning itself belongs to the caller (via
/// <see cref="ICleaningReader.GetByIdForHousekeeperAsync"/> — same fail-closed
/// 404 as every other own-cleaning lookup) before reading its checklist.
/// </remarks>
public sealed class GetOwnCleaningChecklistQueryHandler
    : IQueryHandler<GetOwnCleaningChecklistQuery, IReadOnlyList<CleaningChecklistItemResult>>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);

    private readonly ICleaningReader _cleaningReader;
    private readonly ICleaningChecklistReader _checklistReader;

    public GetOwnCleaningChecklistQueryHandler(ICleaningReader cleaningReader, ICleaningChecklistReader checklistReader)
    {
        _cleaningReader = cleaningReader;
        _checklistReader = checklistReader;
    }

    public async ValueTask<Result<IReadOnlyList<CleaningChecklistItemResult>>> Handle(
        GetOwnCleaningChecklistQuery query, CancellationToken cancellationToken)
    {
        var cleaning = await _cleaningReader.GetByIdForHousekeeperAsync(query.CleaningId, query.HousekeeperUserId, cancellationToken);
        if (cleaning is null)
            return Result.Failure<IReadOnlyList<CleaningChecklistItemResult>>(CleaningNotFoundError);

        var items = await _checklistReader.GetForCleaningAsync(query.CleaningId, cancellationToken);
        return Result.Success(items);
    }
}
