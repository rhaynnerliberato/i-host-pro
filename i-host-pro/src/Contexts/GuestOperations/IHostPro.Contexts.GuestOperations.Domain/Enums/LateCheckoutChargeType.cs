namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// A local, structural mirror of
/// <c>Configuration.Contracts.LateCheckoutChargeType</c> (same three values:
/// none/fixedAmount/percentage) — deliberately NOT a shared reference: this
/// Bounded Context's Domain layer never depends on another context's
/// Contracts (same opaque-boundary convention already used for
/// <c>ReservationId</c>/<c>PropertyId</c> throughout Guest Operations). The
/// Application layer, which DOES reference <c>Configuration.Contracts</c> to
/// call <c>ILateCheckoutPolicyReader</c>, maps the resolved policy's
/// <c>ChargeType</c> into this local value once, at request-creation time —
/// a permanent, historical snapshot of the terms the request was evaluated
/// against, independent of whatever the policy resolves to later.
/// </summary>
public enum LateCheckoutChargeType
{
    None = 0,
    FixedAmount = 1,
    Percentage = 2,
}
