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
/// permanent move to Wolverine's own terminal error handling — never any
/// retry at all. That default does not realize the approved policy
/// (<c>WhatsAppMessageStatusCommunicationProcessor</c>'s own doc comment): a
/// genuine transient race between CP2.2's Sent+ProviderMessageId commit and
/// this event's arrival should get a real chance to self-heal via retry,
/// not go straight to terminal failure on the very first attempt.
/// <see cref="Configure"/> below closes that gap using Wolverine's own
/// native handler-chain policy API — never a custom retry loop/architecture
/// (mandate §3): a SHORT, bounded schedule (three retries, ~4.25s total),
/// proportional to the real race window (an HTTP round trip plus one
/// single-row commit, not minutes) — long enough to let the commit land,
/// short enough that a permanently-missing Message still reaches terminal
/// failure in seconds, not minutes.
///
/// Checkpoint 2.3.3.1 second correction: originally scoped to the generic
/// <c>InvalidOperationException</c> — too broad, since it would have
/// retried ANY <c>InvalidOperationException</c> this method's own call
/// chain might ever throw, not just the specific missing-Message race.
/// Narrowed to <see cref="WhatsAppMessageNotYetAvailableException"/> — the
/// one, deliberately dedicated exception type the processor throws for
/// exactly this condition (see its own doc comment) — so an unrelated bug
/// elsewhere can never accidentally receive this same retry treatment.
/// </summary>
[NonTransactional]
public static class WhatsAppMessageStatusChangedHandler
{
    public static void Configure(HandlerChain chain) =>
        chain.OnException<WhatsAppMessageNotYetAvailableException>()
            .RetryWithCooldown(250.Milliseconds(), 1.Seconds(), 3.Seconds());

    public static Task Handle(
        WhatsAppMessageStatusChanged message,
        MessageContext context,
        ICommunicationMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteAsync(message, message.TenantId, context.Envelope!.Id, cancellationToken);
}
