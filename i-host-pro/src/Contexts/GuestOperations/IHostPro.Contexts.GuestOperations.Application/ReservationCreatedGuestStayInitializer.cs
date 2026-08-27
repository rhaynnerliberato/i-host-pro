using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.GuestOperations.Domain;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// The sole trigger→action use case Guest Operations implements this
/// checkpoint (Fase 10, Checkpoint 2 — Check-in/Checkout Core, resolved
/// governance gate): reacts DIRECTLY to <see cref="ReservationCreated"/> —
/// choreography, same pattern already used by
/// <c>Communication.Application.ReservationCreatedCommunicationProcessor</c>
/// and <c>Workflow.Application.ReservationCreatedCleaningOrchestrator</c> —
/// creating this context's own local <see cref="GuestStayOperation"/>,
/// never a cross-context command, never routed through Workflow.
///
/// Idempotent by construction: looks up an existing operation for
/// (<c>TenantId</c>, <c>ReservationId</c>) before creating — a redelivered
/// <see cref="ReservationCreated"/> never creates a second
/// <see cref="GuestStayOperation"/>. The database's own unique constraint
/// on (tenant_id, reservation_id) remains defense-in-depth, never the
/// primary idempotency mechanism. Publishes no event of its own — creation
/// is a silent, internal fact; only real check-in/checkout transitions
/// publish <see cref="Contracts.GuestCheckedIn"/>/<see cref="Contracts.GuestCheckedOut"/>.
/// </summary>
public sealed class ReservationCreatedGuestStayInitializer : IIntegrationEventHandler<ReservationCreated>
{
    private readonly IGuestStayOperationReader _reader;
    private readonly IRepository<GuestStayOperation, Guid> _repository;
    private readonly IGuestOperationsTransactionExecutor _transactionExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReservationCreatedGuestStayInitializer> _logger;

    public ReservationCreatedGuestStayInitializer(
        IGuestStayOperationReader reader,
        IRepository<GuestStayOperation, Guid> repository,
        IGuestOperationsTransactionExecutor transactionExecutor,
        TimeProvider timeProvider,
        ILogger<ReservationCreatedGuestStayInitializer> logger)
    {
        _reader = reader;
        _repository = repository;
        _transactionExecutor = transactionExecutor;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task HandleAsync(ReservationCreated @event, CancellationToken cancellationToken) =>
        _transactionExecutor.ExecuteAsync(async () =>
        {
            var existingId = await _reader.GetIdByReservationIdAsync(@event.ReservationId, cancellationToken);

            if (existingId is not null)
            {
                _logger.LogInformation(
                    "GuestStayOperation initialization no-op for tenant {TenantId} reservationId {ReservationId}: {Result}",
                    @event.TenantId, @event.ReservationId, "AlreadyExists");
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            var operation = GuestStayOperation.Create(Guid.NewGuid(), @event.TenantId, @event.ReservationId, @event.PropertyId, now);
            _repository.Add(operation);

            _logger.LogInformation(
                "GuestStayOperation created for tenant {TenantId} reservationId {ReservationId}",
                @event.TenantId, @event.ReservationId);

            return true;
        }, cancellationToken);
}
