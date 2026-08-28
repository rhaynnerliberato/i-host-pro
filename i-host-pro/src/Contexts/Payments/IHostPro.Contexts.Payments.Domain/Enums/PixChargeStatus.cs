namespace IHostPro.Contexts.Payments.Domain.Enums;

/// <summary>
/// A PIX charge's financial lifecycle (Fase 10, Checkpoint 5 — PIX/Payment
/// Deterministic Foundation, mandate item 9). Deliberately no separate
/// <c>Created</c> value — a <see cref="PixCharge"/> exists as
/// <see cref="Pending"/> from the moment it is created locally;
/// <see cref="PixCharge.ProviderChargeId"/> may remain <see langword="null"/>
/// while the provider create call has not yet been accepted.
/// </summary>
public enum PixChargeStatus
{
    Pending = 0,
    Confirmed = 1,
    Failed = 2,
    Expired = 3,
    Cancelled = 4,
}
