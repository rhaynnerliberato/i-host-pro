namespace IHostPro.Contexts.GuestOperations.Domain.Enums;

/// <summary>
/// A <see cref="GuestOperations.GuestStayOperation"/>'s minimal lifecycle
/// state (Fase 10, Checkpoint 1 — Guest Operations Foundation). Only two
/// states exist this checkpoint: every <see cref="GuestOperations.GuestStayOperation"/>
/// is born <see cref="Active"/>; <see cref="CheckedOut"/> is terminal — no
/// restoration exists. The fuller check-in granularity Documento 10
/// describes (awaiting form, access delivered, instructions sent, front desk
/// notified, entry granted) is deliberately NOT modeled yet — this
/// checkpoint implements no check-in behavior at all, only the foundation
/// aggregate and the checkout trigger; materializing unused states now would
/// anticipate behavior this checkpoint does not have.
/// </summary>
public enum GuestStayOperationStatus
{
    Active = 0,
    CheckedOut = 1,
}
