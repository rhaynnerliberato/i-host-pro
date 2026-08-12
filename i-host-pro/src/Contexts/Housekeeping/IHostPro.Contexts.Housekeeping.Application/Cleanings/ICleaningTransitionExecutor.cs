using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Runs an existing Cleaning's write operation through
/// <see cref="IHousekeepingTransactionExecutor"/>, additionally translating a
/// genuine optimistic concurrency race on the row's <c>xmin</c> token into
/// <see cref="Errors.HousekeepingErrorCodes.CleaningConcurrencyConflict"/> —
/// mirrors <c>Reservations.Application.Reservations.ICancelReservationExecutor</c>/
/// <c>IUpdateReservationExecutor</c>'s own concurrency-translation seam,
/// kept out of <see cref="IHousekeepingTransactionExecutor"/> itself so the
/// Application layer never references an EF Core-specific exception type
/// directly (that stays in the Infrastructure-layer implementation).
///
/// Used by every command that mutates an EXISTING Cleaning (Assign/Start/
/// StartInspection/Complete/Cancel/MarkInterrupted/MarkWaitingMaterials/
/// MarkWaitingHelp) — unlike Reservations (where only Cancel/Update touch an
/// existing row), every Housekeeping lifecycle command after Create does, so
/// a single shared typed executor is used by all eight rather than one
/// per-command interface.
/// <see cref="CreateCleaningCommandHandler"/> inserts a brand-new row and
/// therefore has no existing <c>xmin</c> to conflict on — it keeps using
/// <see cref="IHousekeepingTransactionExecutor"/> directly.
/// </summary>
public interface ICleaningTransitionExecutor
{
    Task<Result<CleaningResult>> ExecuteAsync(
        Func<Task<Result<CleaningResult>>> operation, CancellationToken cancellationToken);
}
