using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Mirrors <see cref="AuditConnectAirbnbEmailMailboxBehavior"/> exactly.
/// Never logs the mailbox address, listing title, or any guest/reservation
/// data - only tenant/actor/result/timestamp, matching the gate's own audit
/// requirement.
/// </summary>
public sealed class AuditEnableAirbnbAutoPublicationBehavior
    : IPipelineBehavior<EnableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>>
{
    private const string AuditEvent = "AirbnbAutoPublicationEnabled";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditEnableAirbnbAutoPublicationBehavior> _logger;

    public AuditEnableAirbnbAutoPublicationBehavior(TimeProvider timeProvider, ILogger<AuditEnableAirbnbAutoPublicationBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<AirbnbAutoPublicationStatusResult>> Handle(
        EnableAirbnbAutoPublicationCommand message,
        MessageHandlerDelegate<EnableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>> next,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await next(message, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "{AuditEvent}: tenant {TenantId} by user {ActorUserId} at {Timestamp} — result {Result} in {DurationMs}ms",
                AuditEvent, message.TenantId, message.ActorUserId, _timeProvider.GetUtcNow(),
                result.IsSuccess ? "Success" : "Rejected", durationMs);

            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(
                ex,
                "{AuditEvent}: tenant {TenantId} by user {ActorUserId} at {Timestamp} — result {Result} " +
                "({ErrorType}) in {DurationMs}ms",
                AuditEvent, message.TenantId, message.ActorUserId, _timeProvider.GetUtcNow(), "Failed", ex.GetType().Name, durationMs);

            throw;
        }
    }
}
