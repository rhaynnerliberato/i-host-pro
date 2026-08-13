using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <inheritdoc cref="MarkCleaningInterruptedCommand"/>
/// <remarks>
/// Publishes <see cref="CleaningInterrupted"/> (Fase 7, Incremento 1 —
/// Agenda Foundation, Checkpoint 1 closure — new event, approved; this
/// transition previously published nothing at all — see the same doc
/// comment's history). Same transaction/outbox atomicity as every other
/// Cleaning transition — never a side channel.
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
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public MarkCleaningInterruptedCommandHandler(
        ICleaningTransitionExecutor executor,
        IRepository<Cleaning, Guid> repository,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
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

            var correlationId = Guid.NewGuid();

            _eventCollector.Enqueue(new CleaningInterrupted
            {
                TenantId = command.TenantId,
                AggregateId = command.CleaningId,
                AggregateType = "Cleaning",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                CleaningId = command.CleaningId,
            });

            return Result.Success(CreateCleaningCommandHandler.ToResult(cleaning));
        }, cancellationToken);
}
