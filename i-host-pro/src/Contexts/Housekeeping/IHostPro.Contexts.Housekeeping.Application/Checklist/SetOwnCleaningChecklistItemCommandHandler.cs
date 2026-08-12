using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Checklist;

/// <inheritdoc cref="SetOwnCleaningChecklistItemCommand"/>
/// <remarks>
/// Rejected only when the cleaning is already terminal
/// (<c>Completed</c>/<c>Cancelled</c>) — same minimal sanity boundary as
/// <see cref="Cleanings.ReportOwnCleaningDelayCommandHandler"/>/occurrence
/// registration, not an invented business rule.
/// </remarks>
public sealed class SetOwnCleaningChecklistItemCommandHandler
    : ICommandHandler<SetOwnCleaningChecklistItemCommand, CleaningChecklistItemResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);
    private static readonly Error InvalidTransitionError = new(
        HousekeepingErrorCodes.InvalidCleaningTransition, HousekeepingErrorCodes.InvalidCleaningTransition);

    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _cleaningRepository;
    private readonly ICleaningChecklistItemRepository _checklistRepository;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public SetOwnCleaningChecklistItemCommandHandler(
        IHousekeepingTransactionExecutor executor,
        IRepository<Cleaning, Guid> cleaningRepository,
        ICleaningChecklistItemRepository checklistRepository,
        IHousekeepingAuditWriter auditWriter,
        TimeProvider timeProvider)
    {
        _executor = executor;
        _cleaningRepository = cleaningRepository;
        _checklistRepository = checklistRepository;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<CleaningChecklistItemResult>> Handle(
        SetOwnCleaningChecklistItemCommand command, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(async () =>
        {
            var cleaning = await OwnCleaningLoader.LoadOwnedAsync(
                _cleaningRepository, command.CleaningId, command.ActorId, cancellationToken);
            if (cleaning is null)
                return Result.Failure<CleaningChecklistItemResult>(CleaningNotFoundError);

            if (cleaning.Status is CleaningStatus.Completed or CleaningStatus.Cancelled)
                return Result.Failure<CleaningChecklistItemResult>(InvalidTransitionError);

            var itemType = ChecklistItemTypeCodeMapper.FromCode(command.ItemType);
            var now = _timeProvider.GetUtcNow();

            var item = await _checklistRepository.GetAsync(command.CleaningId, itemType, cancellationToken);
            if (item is null)
            {
                item = CleaningChecklistItem.Create(
                    Guid.NewGuid(), command.TenantId, command.CleaningId, itemType, command.IsChecked, command.ActorId, now);
                _checklistRepository.Add(item);
            }
            else
            {
                item.SetChecked(command.IsChecked, command.ActorId, now);
            }

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Cleaning", command.CleaningId,
                "cleaning_checklist_item_set", ["checklist." + command.ItemType], now));

            return Result.Success(ToResult(item));
        }, cancellationToken);

    internal static CleaningChecklistItemResult ToResult(CleaningChecklistItem item) => new(
        item.CleaningId,
        ChecklistItemTypeCodeMapper.ToCode(item.ItemType),
        item.IsChecked,
        item.UpdatedByUserId,
        item.UpdatedAtUtc);
}
