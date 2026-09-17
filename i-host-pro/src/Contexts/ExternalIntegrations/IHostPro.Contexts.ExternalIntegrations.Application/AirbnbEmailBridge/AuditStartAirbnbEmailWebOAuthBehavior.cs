using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Mirrors <see cref="AuditEnableAirbnbAutoPublicationBehavior"/> exactly
/// (Web OAuth architecture gate, item 39: "reuse existing Connect audit
/// semantics"). Never logs the authorization URL, state, or PKCE material -
/// only tenant/actor/result/timestamp.
/// </summary>
public sealed class AuditStartAirbnbEmailWebOAuthBehavior
    : IPipelineBehavior<StartAirbnbEmailWebOAuthCommand, Result<AirbnbEmailWebOAuthStartResult>>
{
    private const string AuditEvent = "AirbnbEmailWebOAuthStarted";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditStartAirbnbEmailWebOAuthBehavior> _logger;

    public AuditStartAirbnbEmailWebOAuthBehavior(TimeProvider timeProvider, ILogger<AuditStartAirbnbEmailWebOAuthBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<AirbnbEmailWebOAuthStartResult>> Handle(
        StartAirbnbEmailWebOAuthCommand message,
        MessageHandlerDelegate<StartAirbnbEmailWebOAuthCommand, Result<AirbnbEmailWebOAuthStartResult>> next,
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
