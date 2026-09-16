using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Mirrors <see cref="AuditEnableAirbnbAutoPublicationBehavior"/> exactly, for the Disable command.</summary>
public sealed class AuditDisableAirbnbAutoPublicationBehavior
    : IPipelineBehavior<DisableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>>
{
    private const string AuditEvent = "AirbnbAutoPublicationDisabled";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditDisableAirbnbAutoPublicationBehavior> _logger;

    public AuditDisableAirbnbAutoPublicationBehavior(TimeProvider timeProvider, ILogger<AuditDisableAirbnbAutoPublicationBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<AirbnbAutoPublicationStatusResult>> Handle(
        DisableAirbnbAutoPublicationCommand message,
        MessageHandlerDelegate<DisableAirbnbAutoPublicationCommand, Result<AirbnbAutoPublicationStatusResult>> next,
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
