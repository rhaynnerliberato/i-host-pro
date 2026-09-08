using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Housekeeping.Application.Cleanings;
using IHostPro.Contexts.Housekeeping.Contracts;
using JasperFx.Core;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Messaging;

/// <summary>
/// Wolverine adapter for the cross-context command
/// <see cref="CreateCleaningForReservation"/> (Fase 8, Checkpoint 1 —
/// ADR-018), sent exclusively by Workflow Orchestration. Depends ONLY on
/// <see cref="IHousekeepingMessageExecutionScope"/> and Wolverine's own
/// <see cref="MessageContext"/> — same thin-adapter shape as
/// <c>ReservationCreatedHandler</c> and every other Wolverine adapter in
/// this context; never resolves <c>HousekeepingDbContext</c> or any
/// business processor directly.
///
/// Housekeeping Workflow Command Retry/Redelivery Production Gate: Wolverine's
/// own default (confirmed empirically — zero custom exception policy existed
/// anywhere in this codebase for this endpoint before this gate, and a real
/// captured log showed the exact same envelope failing and being moved to
/// <c>housekeeping_messaging.wolverine_dead_letters</c> within the SAME
/// timestamp, i.e. zero retries) is exactly ONE attempt, then an immediate,
/// permanent move to Wolverine's own dead-letter handling — never any retry
/// at all. That default does not realize
/// <see cref="Application.Cleanings.CreateCleaningForReservationCommandHandler"/>'s
/// own documented assumption: a genuine transient race between Property
/// Management's <c>PropertyActivated</c> propagation and this command's own
/// arrival should get a real chance to self-heal via retry, not go straight
/// to terminal failure on the very first attempt. <see cref="Configure"/>
/// below closes that gap using Wolverine's own native handler-chain policy
/// API — never a custom retry loop — mirroring
/// <c>WhatsAppMessageStatusChangedHandler.Configure</c>'s own already-approved
/// precedent exactly: a SHORT, bounded schedule, proportional to the real
/// race window (an eventually-consistent cross-context projection sync, not
/// minutes) — long enough to let the projection catch up, short enough that
/// a genuinely inactive/nonexistent property still reaches terminal failure
/// (dead-letter) in seconds, not minutes. Scoped to
/// <see cref="PropertyNotYetKnownToHousekeepingException"/> only — never the
/// generic <see cref="InvalidOperationException"/> this handler originally
/// used — so an unrelated bug elsewhere in this chain can never accidentally
/// receive this same retry treatment.
/// </summary>
[NonTransactional]
public static class CreateCleaningForReservationHandler
{
    public static void Configure(HandlerChain chain) =>
        chain.OnException<PropertyNotYetKnownToHousekeepingException>()
            .RetryWithCooldown(250.Milliseconds(), 1.Seconds(), 3.Seconds());

    public static Task Handle(
        CreateCleaningForReservation message,
        MessageContext context,
        IHousekeepingMessageExecutionScope executionScope,
        CancellationToken cancellationToken) =>
        executionScope.ExecuteCreateCleaningForReservationAsync(message, context.Envelope!.Id, cancellationToken);
}
