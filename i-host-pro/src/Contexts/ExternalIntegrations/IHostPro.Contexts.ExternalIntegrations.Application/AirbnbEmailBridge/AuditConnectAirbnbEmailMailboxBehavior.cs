using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Emits ONE structured, PII-safe log entry per <see cref="ConnectAirbnbEmailMailboxCommand"/>
/// attempt — mirrors <c>AuditEnableWhatsAppIntegrationBehavior</c> exactly.
/// Never logs the mailbox address, account identity, or any token material.
/// </summary>
public sealed class AuditConnectAirbnbEmailMailboxBehavior
    : IPipelineBehavior<ConnectAirbnbEmailMailboxCommand, Result<AirbnbEmailMailboxConnectionResult>>
{
    private const string AuditEvent = "AirbnbEmailBridgeConnectionAttempted";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditConnectAirbnbEmailMailboxBehavior> _logger;

    public AuditConnectAirbnbEmailMailboxBehavior(TimeProvider timeProvider, ILogger<AuditConnectAirbnbEmailMailboxBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<AirbnbEmailMailboxConnectionResult>> Handle(
        ConnectAirbnbEmailMailboxCommand message,
        MessageHandlerDelegate<ConnectAirbnbEmailMailboxCommand, Result<AirbnbEmailMailboxConnectionResult>> next,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await next(message, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "{AuditEvent}: tenant {TenantId} by user {ActorUserId} at {Timestamp} — result {Result} " +
                "({ErrorCode}) in {DurationMs}ms",
                AuditEvent, message.TenantId, message.ActorUserId, _timeProvider.GetUtcNow(),
                result.IsSuccess ? "Success" : "Rejected", result.IsSuccess ? "-" : result.Error.Code, durationMs);

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
