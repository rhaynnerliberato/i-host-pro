namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Provider-neutral synchronous port through which Payments executes a PIX
/// charge creation — the tenth synchronous cross-context exception (ADR-025,
/// Architecture Principles §14). Deliberately provider-neutral: no type,
/// name, or concept specific to a real provider (Asaas, Pagar.me, OpenPix,
/// or any other) may ever appear on this interface or its request/result
/// types — provider-specific DTOs would live exclusively in
/// <c>ExternalIntegrations.Infrastructure</c>, never here. Mirrors
/// <see cref="IMessagingProvider"/> (ADR-021, exception #6) exactly.
///
/// This checkpoint (Fase 10, Checkpoint 5 — PIX/Payment Deterministic
/// Foundation) has exactly one implementation: <c>FakePixProvider</c>,
/// deterministic, no network call, no real money. Choosing/integrating a
/// real PIX provider is explicitly DEFERRED — not decided by this
/// interface's existence.
/// </summary>
public interface IPixProvider
{
    Task<PixChargeCreationResult> CreateChargeAsync(PixChargeRequest request, CancellationToken cancellationToken);
}
