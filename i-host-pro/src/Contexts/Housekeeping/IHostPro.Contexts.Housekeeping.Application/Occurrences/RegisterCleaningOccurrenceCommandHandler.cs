using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <inheritdoc cref="RegisterCleaningOccurrenceCommand"/>
/// <remarks>
/// Rejected only when the cleaning is already terminal
/// (<c>Completed</c>/<c>Cancelled</c>) — same minimal sanity boundary as
/// <see cref="Cleanings.ReportOwnCleaningDelayCommandHandler"/>, not an
/// invented business rule (Fase 6, Incremento 2A Checkpoint 0 gate).
///
/// Publishes <see cref="CleaningOccurrenceRegistered"/> (Fase 7, Incremento
/// 2 — Dashboard &amp; Reporting Foundation, Checkpoint 0/1 decision) — a
/// minimal, PII-free payload (no Description, no RegisteredByUserName, no
/// PropertyId), Dashboard's own event source for occurrence indicators.
/// </remarks>
public sealed class RegisterCleaningOccurrenceCommandHandler : ICommandHandler<RegisterCleaningOccurrenceCommand, CleaningOccurrenceResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);
    private static readonly Error InvalidTransitionError = new(
        HousekeepingErrorCodes.InvalidCleaningTransition, HousekeepingErrorCodes.InvalidCleaningTransition);

    private readonly IHousekeepingTransactionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _cleaningRepository;
    private readonly ICleaningOccurrenceWriter _occurrenceWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public RegisterCleaningOccurrenceCommandHandler(
        IHousekeepingTransactionExecutor executor,
        IRepository<Cleaning, Guid> cleaningRepository,
        ICleaningOccurrenceWriter occurrenceWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _executor = executor;
        _cleaningRepository = cleaningRepository;
        _occurrenceWriter = occurrenceWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<CleaningOccurrenceResult>> Handle(
        RegisterCleaningOccurrenceCommand command, CancellationToken cancellationToken) =>
        await _executor.ExecuteAsync(async () =>
        {
            var cleaning = await OwnCleaningLoader.LoadOwnedAsync(
                _cleaningRepository, command.CleaningId, command.ActorId, cancellationToken);
            if (cleaning is null)
                return Result.Failure<CleaningOccurrenceResult>(CleaningNotFoundError);

            if (cleaning.Status is CleaningStatus.Completed or CleaningStatus.Cancelled)
                return Result.Failure<CleaningOccurrenceResult>(InvalidTransitionError);

            var now = _timeProvider.GetUtcNow();
            var occurrence = CleaningOccurrence.Create(
                Guid.NewGuid(), command.TenantId, command.CleaningId, OccurrenceTypeCodeMapper.FromCode(command.Type),
                command.Description, command.ActorId, now);

            _occurrenceWriter.Record(occurrence);

            _eventCollector.Enqueue(new CleaningOccurrenceRegistered
            {
                TenantId = command.TenantId,
                AggregateId = occurrence.Id,
                AggregateType = "CleaningOccurrence",
                CorrelationId = Guid.NewGuid(),
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                OccurrenceId = occurrence.Id,
                CleaningId = command.CleaningId,
                OccurrenceType = OccurrenceTypeCodeMapper.ToCode(occurrence.Type),
                RegisteredAtUtc = occurrence.RegisteredAtUtc,
            });

            return Result.Success(ToResult(occurrence));
        }, cancellationToken);

    internal static CleaningOccurrenceResult ToResult(CleaningOccurrence occurrence) => new(
        occurrence.Id,
        occurrence.CleaningId,
        OccurrenceTypeCodeMapper.ToCode(occurrence.Type),
        occurrence.Description,
        occurrence.RegisteredByUserId,
        occurrence.RegisteredAtUtc);
}
