using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using JasperFx.Core;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace IHostPro.Contexts.Communication.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for <c>WhatsAppMessageStatusChanged</c> (Fase 9,
/// Checkpoint 2.3.3, ADR-022 item 14). Depends ONLY on
/// <see cref="ICommunicationMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — never on <c>CommunicationDbContext</c> or
/// the message processor directly. Mirrors <see cref="ReservationCreatedHandler"/>
/// exactly.
///
/// Checkpoint 2.3.3 corrective mandate (missing-Message governance gate):
/// Wolverine's own default (confirmed empirically — zero custom exception
/// policy exists anywhere in this codebase, confirmed by repo-wide search —
/// two direct reproductions, LocalQueue and the same PersistMessagesWithPostgresql
/// configuration production uses) is exactly ONE attempt, then an immediate,
/// permanent move to <c>wolverine_dead_letters</c> — never any retry at all.
/// That default does not realize the approved policy (WhatsAppMessageStatusCommunicationProcessor's
/// own doc comment): a genuine transient race between CP2.2's Sent+ProviderMessageId
/// commit and this event's arrival should get a real chance to self-heal via
/// retry, not go straight to the dead-letter table on the very first
/// attempt. <see cref="Configure"/> below closes that gap using Wolverine's
/// own native handler-chain policy API — never a custom retry loop/architecture
/// (mandate §3): a SHORT, bounded schedule (three retries, ~4.25s total),
/// proportional to the real race window (an HTTP round trip plus one
/// single-row commit, not minutes) — long enough to let the commit land,
/// short enough that a permanently-missing Message still reaches the dead
/// letter table in seconds, not minutes. Scoped to
/// <see cref="InvalidOperationException"/> only — the exact exception the
/// processor already throws for this case, no new exception type invented —
/// and to this one handler chain only, never a global policy.
/// </summary>
[NonTransactional]
public static class WhatsAppMessageStatusChangedHandler
{
    public static void Configure(HandlerChain chain) =>
        chain.OnException<InvalidOperationException>()
            .RetryWithCooldown(250.Milliseconds(), 1.Seconds(), 3.Seconds());

    public static Task Handle(
        WhatsAppMessageStatusChanged message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
