namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Pure, stateless ordering rules for <see cref="ProviderMessageStatus"/>
/// transitions (Fase 9, Checkpoint 2.3.2 — idempotency/monotonicity
/// foundation, mandate §24-29). Never touches persistence — the webhook
/// ingress in this checkpoint has no stored "previous status" to compare
/// against yet (that requires <c>Communication.Message</c> lookup by
/// <c>ProviderMessageId</c>, CP2.3.3); this class exists so the ordering
/// rules are built and proven now, ready to be wired to real state later.
///
/// Ordering: Sent &lt; Delivered &lt; Read (mandate §25). <c>Sent → Read</c>
/// directly is a valid Forward transition — Meta's own documentation
/// confirms "delivered" can be omitted entirely when "read" arrives almost
/// simultaneously (Checkpoint 2.3.0 research), so skipping Delivered must
/// never be treated as out-of-order.
///
/// <c>Failed</c> is a terminal branch, never just a higher ordinal (mandate
/// §25/§29): <c>Sent → Failed</c> is the one pre-approved forward transition
/// into Failed (a message can be accepted synchronously and fail delivery
/// asynchronously). <c>Delivered → Failed</c> and <c>Read → Failed</c> are
/// classified as Regression, not Forward — this is a reasoned default, not
/// an explicitly pre-approved rule: a message already confirmed delivered or
/// read has a strictly stronger positive confirmation than "sent", so a
/// later "failed" report contradicts already-established ground truth
/// rather than genuinely advancing it. Flagged for review, not decided
/// silently — see the Fase 9 checkpoint report for this checkpoint.
/// Anything reported after Failed is likewise Regression — Failed does not
/// advance further once reached.
/// </summary>
public static class WhatsAppStatusTransitionClassifier
{
    private static readonly Dictionary<ProviderMessageStatus, int> Rank = new()
    {
        [ProviderMessageStatus.Sent] = 1,
        [ProviderMessageStatus.Delivered] = 2,
        [ProviderMessageStatus.Read] = 3,
    };

    public static StatusTransitionClassification Classify(ProviderMessageStatus? previous, ProviderMessageStatus incoming)
    {
        if (previous is null)
            return StatusTransitionClassification.Forward;

        if (previous == incoming)
            return StatusTransitionClassification.Duplicate;

        if (previous == ProviderMessageStatus.Failed)
            return StatusTransitionClassification.Regression; // Failed is terminal — nothing advances past it.

        if (incoming == ProviderMessageStatus.Failed)
        {
            return previous == ProviderMessageStatus.Sent
                ? StatusTransitionClassification.Forward
                : StatusTransitionClassification.Regression; // Delivered/Read -> Failed contradicts a stronger prior confirmation.
        }

        return Rank[incoming] > Rank[previous.Value]
            ? StatusTransitionClassification.Forward
            : StatusTransitionClassification.Regression;
    }
}
