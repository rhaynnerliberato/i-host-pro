namespace IHostPro.Contexts.Payments.Application.Errors;

/// <summary>
/// Stable, framework-neutral codes for business-rule rejections raised by
/// Payments queries (Fase 11, Checkpoint 3 — the first Application Query
/// this context ever exposes) — mirrors <c>HousekeepingErrorCodes</c>'s own
/// snake_case convention exactly.
/// </summary>
public static class PaymentsErrorCodes
{
    /// <summary>No <c>PixCharge</c> exists yet for the queried Reservation.</summary>
    public const string PixChargeNotFound = "pix_charge_not_found";
}
