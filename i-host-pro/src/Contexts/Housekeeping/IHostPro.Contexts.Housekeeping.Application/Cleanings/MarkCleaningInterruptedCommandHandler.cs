using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="MarkCleaningInterruptedCommand"/>
/// <remarks>
/// No Integration Event is published for this transition — Documento 07 §6
/// catalogs no dedicated event for it (only <c>CleaningDelayed</c>/
/// <c>CleaningNeedsHelp</c>/<c>CleaningNeedsMaterial</c>, all deferred to
/// the Portal da Faxineira, Incremento 2, per <c>Housekeeping.Contracts</c>'
/// own csproj doc comment) — the audit trail is this transition's only
/// record this increment.
/// </remarks>
public sealed class MarkCleaningInterruptedCommandHandler : ICommandHandler<MarkCleaningInterruptedCommand, CleaningResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);
    private static readonly Error InvalidTransitionError = new(
        HousekeepingErrorCodes.InvalidCleaningTransition, HousekeepingErrorCodes.InvalidCleaningTransition);

    private readonly ICleaningTransitionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _repository;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly TimeProvider _timeProvider;

    public MarkCleaningInterruptedCommandHandler(
        ICleaningTransitionExecutor executor,
        IRepository<Cleaning, Guid> repository,
        IHousekeepingAuditWriter auditWriter,
        TimeProvider timeProvider)
    {
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<CleaningResult>> Handle(MarkCleaningInterruptedCommand command, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(async () =>
        {
            var cleaning = await _repository.GetByIdAsync(command.CleaningId, cancellationToken);
            if (cleaning is null)
                return Result.Failure<CleaningResult>(CleaningNotFoundError);

            var now = _timeProvider.GetUtcNow();

            try
            {
                cleaning.MarkInterrupted(now);
            }
            catch (InvalidOperationException)
            {
                return Result.Failure<CleaningResult>(InvalidTransitionError);
            }

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Cleaning", command.CleaningId,
                "cleaning_interrupted", ["status"], now));

            return Result.Success(CreateCleaningCommandHandler.ToResult(cleaning));
        }, cancellationToken);
}
