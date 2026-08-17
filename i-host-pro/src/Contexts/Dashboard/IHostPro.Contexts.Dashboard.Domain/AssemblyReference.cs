namespace IHostPro.Contexts.Dashboard.Domain;

/// <summary>
/// Anchor type for assembly-level reflection (architecture tests) — Dashboard
/// &amp; Reporting is a Supporting, terminal/read-model Bounded Context
/// (Architecture Principles §3/§14): it owns no genuine aggregate with its
/// own business invariants this increment, only projections mirroring other
/// contexts' Integration Events (which live in
/// <c>IHostPro.Contexts.Dashboard.Infrastructure.Projections</c>, mirroring
/// <c>Reservations.Infrastructure.Projections.CleaningScheduleProjectionEntry</c>'s
/// own precedent — a local read-model mirror is an Infrastructure concern,
/// never a Domain one). This project stays intentionally minimal until a
/// genuine Dashboard-owned domain concept (with its own rules, not merely
/// mirrored state) is required.
/// </summary>
public static class AssemblyReference
{
}
