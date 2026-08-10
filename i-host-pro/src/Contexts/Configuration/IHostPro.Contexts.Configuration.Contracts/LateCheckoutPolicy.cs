namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The typed, deserialized shape of the <c>LATE_CHECKOUT</c> policy —
/// exactly the field list approved in the Fase 5, Incremento 1 catalog (§3),
/// nothing more. No default value exists for any field; this type is only
/// ever produced by <see cref="ILateCheckoutPolicyReader"/> when
/// <see cref="PolicyReadStatus.Resolved"/>.
/// </summary>
public sealed record LateCheckoutPolicy(
    bool Allowed,
    TimeOnly? LatestTime,
    LateCheckoutChargeType ChargeType,
    decimal? ChargeValue,
    bool RequiresPix,
    bool BlocksCalendar,
    bool UpdatesCleaning);
