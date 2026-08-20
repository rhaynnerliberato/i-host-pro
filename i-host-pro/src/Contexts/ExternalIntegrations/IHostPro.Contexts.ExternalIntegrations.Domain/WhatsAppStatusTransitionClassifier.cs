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
/// §25/§29). Corrected in Checkpoint 2.3.2.1 after CP2.3.2's original
/// closure report flagged its own <c>Delivered/Read → Failed</c> default as
/// a reasoned-but-not-explicitly-approved decision, which the mandate
/// required to gate on explicit approval rather than ship silently. The
/// approved semantics (Checkpoint 2.3.2.1):
/// <list type="bullet">
/// <item><c>Sent → Failed</c> = Forward — a message can be accepted
/// synchronously and fail delivery asynchronously.</item>
/// <item><c>Delivered → Failed</c> = Forward — <c>Delivered</c> is NOT
/// terminal; a later "failed" report from the provider is genuine new
/// information (e.g. a downstream delivery failure detected after the
/// device-level ack), not noise to be discarded.</item>
/// <item><c>Read → Failed</c> = Regression (no-op) — <c>Read</c> IS treated
/// as terminal for this checkpoint's lifecycle; a "failed" report arriving
/// after "read" is a late/regressive callback, never something that should
/// roll a message back from its strongest confirmed state.</item>
/// </list>
/// Anything reported after Failed is likewise Regression — Failed does not
/// advance further once reached (mandate §7/§29).
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
            // Read is terminal for Failed purposes; Sent/Delivered are not —
            // see the class remarks (Checkpoint 2.3.2.1 correction).
            return previous == ProviderMessageStatus.Read
                ? StatusTransitionClassification.Regression
                : StatusTransitionClassification.Forward;
        }

        return Rank[incoming] > Rank[previous.Value]
            ? StatusTransitionClassification.Forward
            : StatusTransitionClassification.Regression;
    }
}
