namespace IHostPro.Contexts.Housekeeping.Application;

/// <summary>
/// Runs a write Command's operation inside a tenant-aware, RLS-protected
/// transaction, publishing any collected Integration Event to this
/// context's durable outbox (<c>housekeeping_messaging</c> schema)
/// atomically with the domain change — mirrors
/// <c>Reservations.Application.IReservationsTransactionExecutor</c> exactly.
///
/// A single generic executor, shared by every write command in this
/// context (Create/Assign/Start/StartInspection/Complete/Cancel), rather
/// than one narrowly-typed wrapper interface per command (the pattern
/// <c>ICreateReservationExecutor</c>/<c>ICancelReservationExecutor</c>/
/// <c>IUpdateReservationExecutor</c> follow) — every Housekeeping write
/// command returns the same <c>CleaningResult</c> and needs no
/// command-specific pre/post-transaction step this executor itself would
/// need to know about, so the extra per-command interface would be pure
/// duplication with no behavioral difference from this one.
/// </summary>
public interface IHousekeepingTransactionExecutor
{
    Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken);
}
