using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// <c>InInspection</c> → <c>Completed</c>, terminal (Fase 6, Incremento 1).
/// No downstream consumer of <c>CleaningCompleted</c> exists yet (Documento
/// 06's "poderá liberar Early Check-in" remains unrealized this increment —
/// see the Fase 6 homologation document).
/// </summary>
public sealed class CompleteCleaningCommandHandler : ICommandHandler<CompleteCleaningCommand, CleaningResult>
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

    public CompleteCleaningCommandHandler(
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

    public async ValueTask<Result<CleaningResult>> Handle(CompleteCleaningCommand command, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(async () =>
        {
            var cleaning = await _repository.GetByIdAsync(command.CleaningId, cancellationToken);
            if (cleaning is null)
                return Result.Failure<CleaningResult>(CleaningNotFoundError);

            var now = _timeProvider.GetUtcNow();

            try
            {
                cleaning.Complete(now);
            }
            catch (InvalidOperationException)
            {
                return Result.Failure<CleaningResult>(InvalidTransitionError);
            }

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Cleaning", command.CleaningId,
                "cleaning_completed", ["status"], now));

            _eventCollector.Enqueue(new CleaningCompleted
            {
                TenantId = command.TenantId,
                AggregateId = command.CleaningId,
                AggregateType = "Cleaning",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                CleaningId = command.CleaningId,
                PropertyId = cleaning.PropertyId,
            });

            return Result.Success(CreateCleaningCommandHandler.ToResult(cleaning));
        }, cancellationToken);
}
