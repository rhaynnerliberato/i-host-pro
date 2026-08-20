namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <summary>
/// Verifies the two security surfaces of an inbound provider webhook (Fase 9,
/// Checkpoint 2.3.1 — ADR-022): a GET verify-token handshake, and a POST
/// HMAC signature over the exact raw request body. Provider-specific wire
/// details (header name, hash algorithm, hex/prefix format) live entirely in
/// the Infrastructure implementation — never here (mirrors
/// <see cref="IMessagingProvider"/>'s own provider-neutral boundary, ADR-021).
/// Never accepts or exposes a tenant identifier — this runs before any
/// tenant is known.
/// </summary>
public interface IWebhookSignatureVerifier
{
    bool IsValidVerifyToken(string? mode, string? providedToken, string configuredVerifyToken);

    bool IsValidSignature(ReadOnlySpan<byte> rawBody, string? signatureHeaderValue, string appSecret);
}
