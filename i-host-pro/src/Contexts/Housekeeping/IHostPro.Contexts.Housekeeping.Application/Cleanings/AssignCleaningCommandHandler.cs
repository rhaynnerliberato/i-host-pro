using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Application.Errors;
using IHostPro.Contexts.Housekeeping.Contracts;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Contracts.Authorization;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// <c>Pending</c> → <c>Assigned</c> (Fase 6, Incremento 1). The
/// Housekeeper's eligibility (exists, active, same tenant, holds
/// <c>HOUSEKEEPER</c>) is checked via the already-public, already-generic
/// <c>IIdentityUserEligibilityReader</c> (Checkpoint 0 gate — no new
/// contract created) BEFORE this context's write transaction opens —
/// mirrors <c>CreateReservationCommandHandler</c>'s own division.
/// </summary>
public sealed class AssignCleaningCommandHandler : ICommandHandler<AssignCleaningCommand, CleaningResult>
{
    private static readonly Error CleaningNotFoundError = new(
        HousekeepingErrorCodes.CleaningNotFound, HousekeepingErrorCodes.CleaningNotFound);
    private static readonly Error HousekeeperNotEligibleError = new(
        HousekeepingErrorCodes.HousekeeperNotEligible, HousekeepingErrorCodes.HousekeeperNotEligible);
    private static readonly Error InvalidTransitionError = new(
        HousekeepingErrorCodes.InvalidCleaningTransition, HousekeepingErrorCodes.InvalidCleaningTransition);

    private readonly IIdentityUserEligibilityReader _eligibilityReader;
    private readonly ICleaningTransitionExecutor _executor;
    private readonly IRepository<Cleaning, Guid> _repository;
    private readonly IHousekeepingAuditWriter _auditWriter;
    private readonly IIntegrationEventCollector _eventCollector;
    private readonly TimeProvider _timeProvider;

    public AssignCleaningCommandHandler(
        IIdentityUserEligibilityReader eligibilityReader,
        ICleaningTransitionExecutor executor,
        IRepository<Cleaning, Guid> repository,
        IHousekeepingAuditWriter auditWriter,
        IIntegrationEventCollector eventCollector,
        TimeProvider timeProvider)
    {
        _eligibilityReader = eligibilityReader;
        _executor = executor;
        _repository = repository;
        _auditWriter = auditWriter;
        _eventCollector = eventCollector;
        _timeProvider = timeProvider;
    }

    public async ValueTask<Result<CleaningResult>> Handle(AssignCleaningCommand command, CancellationToken cancellationToken)
    {
        var eligibility = await _eligibilityReader.GetAsync(
            command.TenantId, command.HousekeeperUserId, IdentityRoleCodes.Housekeeper, cancellationToken);

        if (eligibility is null || !eligibility.IsActive || !eligibility.HasRequiredRole)
            return Result.Failure<CleaningResult>(HousekeeperNotEligibleError);

        return await _executor.ExecuteAsync(async () =>
        {
            var cleaning = await _repository.GetByIdAsync(command.CleaningId, cancellationToken);
            if (cleaning is null)
                return Result.Failure<CleaningResult>(CleaningNotFoundError);

            var now = _timeProvider.GetUtcNow();

            try
            {
                cleaning.Assign(command.HousekeeperUserId, now);
            }
            catch (InvalidOperationException)
            {
                return Result.Failure<CleaningResult>(InvalidTransitionError);
            }

            var correlationId = Guid.NewGuid();

            _auditWriter.Record(CleaningAuditEntry.Create(
                Guid.NewGuid(), command.TenantId, command.ActorId, "Cleaning", command.CleaningId,
                "cleaning_assigned", ["status"], now));

            _eventCollector.Enqueue(new CleaningAssigned
            {
                TenantId = command.TenantId,
                AggregateId = command.CleaningId,
                AggregateType = "Cleaning",
                CorrelationId = correlationId,
                ActorType = "User",
                ActorId = command.ActorId.ToString(),
                CleaningId = command.CleaningId,
                HousekeeperUserId = command.HousekeeperUserId,
            });

            return Result.Success(CreateCleaningCommandHandler.ToResult(cleaning));
        }, cancellationToken);
    }
}
