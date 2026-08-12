namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

/// <summary>Nullable at the wire level — presence validated by the controller before dispatch (mirrors the rest of this Bounded Context's minimal request DTOs).</summary>
public sealed record AssignCleaningRequest(Guid? HousekeeperUserId);
