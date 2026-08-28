namespace IHostPro.Contexts.Payments.Contracts;

/// <summary>
/// The single, minimal synchronous query port Communication may use to
/// resolve the QR/copy-paste payload needed to deliver a PIX charge to a
/// guest (Fase 10, Checkpoint 5 — ADR-027, synchronous exception #11).
/// Mirrors <c>IFrontDeskContactReader</c> (ADR-026, exception #9) exactly:
/// a NEW, separately named, purpose-limited exception — authorizes exactly
/// one consumer (Communication) and exactly one purpose (resolving the data
/// needed to deliver ONE already-created PIX charge to the guest who owns
/// the underlying Reservation).
///
/// Implemented ONLY in <c>Payments.Infrastructure</c> — Communication may
/// reference this contract, never <c>Payments.Application</c>/
/// <c>Infrastructure</c>, and never <c>PaymentsDbContext</c>/the
/// <c>payments</c> schema directly.
///
/// <see cref="PixChargeDeliveryReadResult.QrCodePayload"/> is sensitive
/// operational payment data — never logged, never re-published in an
/// Integration Event, never placed in a query string. It travels only
/// in-process, synchronously, at the moment Communication is about to render
/// and send the guest message.
/// </summary>
public interface IPixChargeDeliveryReader
{
    /// <summary>
    /// Returns <see langword="null"/> when no <c>PixCharge</c> exists for
    /// <paramref name="pixChargeId"/> under <paramref name="tenantId"/> — an
    /// unknown id and a cross-tenant id are indistinguishable by design,
    /// same convention as every other synchronous cross-context reader in
    /// this platform (ADR-014/ADR-019/ADR-026).
    /// </summary>
    Task<PixChargeDeliveryReadResult?> GetForDeliveryAsync(
        Guid tenantId, Guid pixChargeId, CancellationToken cancellationToken);
}
