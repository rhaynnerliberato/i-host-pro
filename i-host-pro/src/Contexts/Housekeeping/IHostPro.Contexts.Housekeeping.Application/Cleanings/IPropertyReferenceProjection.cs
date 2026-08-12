namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Read port over this context's own local, tenant-aware projection of
/// Property Management's lifecycle events (<c>PropertyCreated</c>/
/// <c>PropertyActivated</c>/<c>PropertyDeactivated</c>/<c>PropertyArchived</c>
/// — Checkpoint 0/3 gate) — never a synchronous cross-context call: Property
/// Management is not one of <c>Architecture Principles.md</c> §14's two
/// general synchronous-query exceptions (Identity &amp; Access, Configuration
/// &amp; Policy), and <c>IPropertyReservationEligibilityReader</c> is
/// explicitly scoped to Reservations only (ADR-014), never reusable here.
/// Answers only "is this a known, active property of this tenant?" — no
/// name/code/address/capacity is projected, since nothing this increment
/// needs it (the admin's own property picker already gets that from
/// Property Management's own existing API, entirely outside this context).
/// </summary>
public interface IPropertyReferenceProjection
{
    Task<bool> IsKnownActivePropertyAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken);
}
