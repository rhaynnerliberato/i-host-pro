namespace IHostPro.Contexts.Payments.Application;

/// <summary>
/// Supports the cardinality rule for PIX charges (Fase 10, Checkpoint 5
/// mandate item 14): at most one ACTIVE (<c>Pending</c>) charge may exist
/// per <c>LateCheckoutRequestId</c> at a time. Also backs the idempotency
/// guard for <c>LateCheckoutPaymentRequiredChargeInitializer</c> — a
/// redelivered <c>LateCheckoutPaymentRequired</c> must never create a
/// second charge (mandate item 15).
/// </summary>
public interface IPixChargeReader
{
    /// <summary>The id of the active (<c>Pending</c>) charge for <paramref name="lateCheckoutRequestId"/>, or <see langword="null"/> if none exists.</summary>
    Task<Guid?> GetActiveIdByLateCheckoutRequestIdAsync(Guid lateCheckoutRequestId, CancellationToken cancellationToken);
}
