using IHostPro.BuildingBlocks.Application;

namespace IHostPro.BuildingBlocks.Infrastructure.Email;

/// <summary>
/// Registered outside Development (Self-Service Identity &amp; Onboarding
/// Foundation gate) — no real production transactional email provider has
/// been selected yet (deliberately deferred to a separate future gate, per
/// the approved architecture decision). Fails loudly on first use rather
/// than silently swallowing an email, matching this codebase's existing
/// fail-fast convention for an unconfigured production dependency (e.g.
/// <c>CorsOriginsResolver</c>).
/// </summary>
public sealed class UnconfiguredTransactionalEmailSender : ITransactionalEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "No production transactional email provider is configured. This is a known, deliberate gap " +
            "(Self-Service Identity & Onboarding Foundation gate) — provider selection is a separate future gate.");
}
