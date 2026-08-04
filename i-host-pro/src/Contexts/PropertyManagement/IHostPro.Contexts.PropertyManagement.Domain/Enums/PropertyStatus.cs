namespace IHostPro.Contexts.PropertyManagement.Domain.Enums;

/// <summary>
/// A Property's lifecycle state (Checkpoint 0 plan, item 7). Persisted as
/// text via <c>HasConversion&lt;string&gt;()</c> — never a bare integer — so
/// the stored value survives enum member reordering. Transition rules
/// (Draft→Active, Active⇄Inactive, Draft/Inactive→Archived, Archived being
/// terminal) are enforced by <see cref="Property"/>'s domain methods,
/// implemented in Checkpoint 4 — this increment only establishes the state
/// every <see cref="Property"/> is born into (<see cref="Draft"/>).
/// </summary>
public enum PropertyStatus
{
    Draft,
    Active,
    Inactive,
    Archived,
}
