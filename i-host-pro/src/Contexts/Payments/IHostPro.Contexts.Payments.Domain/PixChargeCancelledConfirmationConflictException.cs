namespace IHostPro.Contexts.Payments.Domain;

/// <summary>
/// Thrown when <see cref="PixCharge.Confirm"/> is invoked on a
/// <see cref="Enums.PixChargeStatus.Cancelled"/> charge — an explicitly
/// undecided transition (Fase 10, Checkpoint 5 mandate item 10: "PARE e
/// reporte" if this scenario surfaces). Nothing in this checkpoint's own
/// code paths ever sets a charge to <see cref="Enums.PixChargeStatus.Cancelled"/>,
/// so this exception is unreachable today — it exists purely as an explicit
/// guard against a future caller silently deciding either outcome.
/// </summary>
public sealed class PixChargeCancelledConfirmationConflictException(Guid pixChargeId)
    : InvalidOperationException(
        $"PixCharge '{pixChargeId}' is Cancelled — confirming a cancelled charge is not an approved " +
        "transition (Fase 10, Checkpoint 5 mandate). This requires explicit product/architecture " +
        "escalation, never a silent decision.")
{
    public Guid PixChargeId { get; } = pixChargeId;
}
