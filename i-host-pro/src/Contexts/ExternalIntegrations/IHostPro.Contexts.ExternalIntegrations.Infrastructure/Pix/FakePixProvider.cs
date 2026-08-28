using IHostPro.Contexts.ExternalIntegrations.Contracts;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Pix;

/// <inheritdoc cref="IPixProvider"/>
/// <remarks>
/// Fase 10, Checkpoint 5's ONLY implementation of <see cref="IPixProvider"/>
/// — a deterministic development/test double, never a real PIX provider
/// client (mirrors <c>FakeWhatsAppConnector</c>'s own precedent exactly:
/// Documento 19 §28, "every Connector must be substitutable by a fake";
/// here the "real" one simply does not exist yet — choosing/integrating a
/// real provider is explicitly DEFERRED, never built speculatively).
///
/// Always accepts, deterministically — never calls out to any network, never
/// reads real credentials (none exist this checkpoint). The QR/copy-paste
/// payload and provider charge id are both derived purely from the request's
/// own fields, so the SAME request always produces the SAME output — useful
/// as a test sentinel without ever needing to persist/compare a hardcoded
/// magic string. Registered UNCONDITIONALLY (not Development-gated, unlike
/// <c>MetaWhatsAppMessagingProvider</c>): unlike WhatsApp, there is no real
/// alternative implementation this checkpoint could conflict with — this
/// fake stands in for the entire PIX provider integration.
///
/// The class name and this remark make the Fake/Test/Development nature
/// unmistakable at every call site and in DI registration — this must never
/// be mistaken for a production implementation.
/// </remarks>
public sealed class FakePixProvider : IPixProvider
{
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FakePixProvider> _logger;

    public FakePixProvider(TimeProvider timeProvider, ILogger<FakePixProvider> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<PixChargeCreationResult> CreateChargeAsync(PixChargeRequest request, CancellationToken cancellationToken)
    {
        var providerChargeId = $"fake-{request.PixChargeId:N}";
        var qrCodePayload = $"00020126FAKE-PIX-NO-REAL-MONEY-{request.PixChargeId:N}-{request.Amount:F2}-{request.CurrencyCode}6304FAKE";
        var expiresAtUtc = _timeProvider.GetUtcNow().AddMinutes(30);

        // Never logs qrCodePayload — only the (non-sensitive) provider charge
        // id and idempotency key, mirroring FakeWhatsAppConnector's own
        // "never log Destination/Content" discipline.
        _logger.LogInformation(
            "[FAKE PIX Provider — Development/Test only, no real money, no real provider] " +
            "accepted charge for tenant {TenantId} pixChargeId {PixChargeId} idempotencyKey {IdempotencyKey}",
            request.TenantId, request.PixChargeId, request.IdempotencyKey);

        return Task.FromResult(new PixChargeCreationResult(
            Accepted: true,
            ProviderChargeId: providerChargeId,
            QrCodePayload: qrCodePayload,
            ExpiresAtUtc: expiresAtUtc,
            FailureCode: null));
    }
}
