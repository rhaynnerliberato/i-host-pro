using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Reads a single cleaning's full administrative detail (Fase 6, Incremento
/// 1) — mirrors <c>GetReservationDetailQuery</c>. Nonexistent/cross-tenant
/// ids are indistinguishable by design.
/// </summary>
public sealed record GetCleaningDetailQuery(Guid CleaningId) : IQuery<CleaningResult>;
