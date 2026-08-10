namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// The typed, deserialized shape of the <c>EARLY_CHECKIN</c> policy — exactly
/// the field list approved in the Fase 5, Incremento 1 catalog (§3), nothing
/// more. No default value exists for any field; this type is only ever
/// produced by <see cref="IEarlyCheckInPolicyReader"/> when
/// <see cref="PolicyReadStatus.Resolved"/>.
/// </summary>
public sealed record EarlyCheckInPolicy(
    bool Allowed,
    TimeOnly? EarliestTime,
    bool RequiresCleaningCompleted,
    bool RequiresForm,
    bool NotifyFrontDesk);
