using IHostPro.BuildingBlocks.Domain;
using Mediator;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Emits ONE structured, PII-safe log entry per <see cref="RetryAirbnbEmailMessageReceiptCommand"/>
/// attempt — mirrors <c>AuditConnectAirbnbEmailMailboxBehavior</c> exactly.
/// Never logs the receipt's subject/body, tokens, or guest PII (none of
/// which this handler ever holds beyond the retry attempt itself).
/// </summary>
public sealed class AuditRetryAirbnbEmailReceiptBehavior
    : IPipelineBehavior<RetryAirbnbEmailMessageReceiptCommand, Result<AirbnbEmailMessageReceiptResult>>
{
    private const string AuditEvent = "AirbnbEmailReceiptRetryAttempted";

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuditRetryAirbnbEmailReceiptBehavior> _logger;

    public AuditRetryAirbnbEmailReceiptBehavior(TimeProvider timeProvider, ILogger<AuditRetryAirbnbEmailReceiptBehavior> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<Result<AirbnbEmailMessageReceiptResult>> Handle(
        RetryAirbnbEmailMessageReceiptCommand message,
        MessageHandlerDelegate<RetryAirbnbEmailMessageReceiptCommand, Result<AirbnbEmailMessageReceiptResult>> next,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();

        try
        {
            var result = await next(message, cancellationToken);

            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogInformation(
                "{AuditEvent}: tenant {TenantId} by user {ActorUserId} receipt {ReceiptId} at {Timestamp} — result {Result} " +
                "({ErrorCode}) in {DurationMs}ms",
                AuditEvent, message.TenantId, message.ActorUserId, message.ReceiptId, _timeProvider.GetUtcNow(),
                result.IsSuccess ? "Success" : "Rejected", result.IsSuccess ? "-" : result.Error.Code, durationMs);

            return result;
        }
        catch (Exception ex)
        {
            var durationMs = (_timeProvider.GetUtcNow() - startedAtUtc).TotalMilliseconds;

            _logger.LogError(
                ex,
                "{AuditEvent}: tenant {TenantId} by user {ActorUserId} receipt {ReceiptId} at {Timestamp} — result {Result} " +
                "({ErrorType}) in {DurationMs}ms",
                AuditEvent, message.TenantId, message.ActorUserId, message.ReceiptId, _timeProvider.GetUtcNow(), "Failed", ex.GetType().Name, durationMs);

            throw;
        }
    }
}
