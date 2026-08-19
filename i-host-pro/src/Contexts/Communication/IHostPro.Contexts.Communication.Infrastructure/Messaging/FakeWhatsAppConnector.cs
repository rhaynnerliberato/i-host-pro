using IHostPro.Contexts.Communication.Application;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <inheritdoc cref="IOutboundMessageConnector"/>
/// <remarks>
/// Fase 9, Checkpoint 1's ONLY implementation of
/// <see cref="IOutboundMessageConnector"/> — a deterministic development/
/// test double, never a real WhatsApp API client (Documento 19 §28: every
/// Connector must be substitutable by a fake; here the "real" one simply
/// does not exist yet — deferred to a future checkpoint per an explicit
/// decision, never built speculatively). Always succeeds — never calls out
/// to any network, never reads real credentials (none exist this
/// checkpoint). The class name and this remark make the Fake/Test/
/// Development nature unmistakable at every call site and in DI
/// registration (CP1 mandate §13) — this must never be mistaken for a
/// production implementation.
/// </remarks>
public sealed class FakeWhatsAppConnector : IOutboundMessageConnector
{
    private readonly ILogger<FakeWhatsAppConnector> _logger;

    public FakeWhatsAppConnector(ILogger<FakeWhatsAppConnector> logger) => _logger = logger;

    public Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken)
    {
        // Never logs dispatch.Destination/dispatch.Content — only the
        // idempotency key, which carries no PII.
        _logger.LogInformation(
            "[FAKE WhatsApp Connector — Development/Test only, no real API called] dispatching idempotencyKey {IdempotencyKey}",
            dispatch.IdempotencyKey);

        return Task.FromResult(new OutboundMessageDispatchResult(Success: true, ProviderMessageId: null, FailureReason: null));
    }
}
