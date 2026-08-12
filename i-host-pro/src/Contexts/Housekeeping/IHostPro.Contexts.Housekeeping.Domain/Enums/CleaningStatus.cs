namespace IHostPro.Contexts.Housekeeping.Domain.Enums;

/// <summary>
/// Mirrors Documento 06 §5 exactly — the document explicitly chosen as the
/// authoritative source for persisted Cleaning states (Fase 6, Incremento 1,
/// approval decision §5), over Documento 05/10's own, slightly different
/// terminology ("Inspecionada" instead of <see cref="InInspection"/>, no
/// "Em Deslocamento"/<see cref="InTransit"/> equivalent, plus an
/// undocumented "Reabrir" action) — see the Fase 6 homologation document for
/// the reconciliation note.
///
/// This increment's administrative lifecycle only ever reaches
/// <see cref="Pending"/> → <see cref="Assigned"/> → <see cref="Started"/> →
/// <see cref="InInspection"/> → <see cref="Completed"/>, plus
/// <see cref="Cancelled"/> (only from <see cref="Pending"/>/<see cref="Assigned"/>
/// — Documento 06's own "Fluxos alternativos" only ever documents
/// <c>Designada → Cancelada</c>, never a direct cancel from
/// <see cref="Started"/>/<see cref="InInspection"/>) and
/// <see cref="Interrupted"/>/<see cref="WaitingMaterials"/>/<see cref="WaitingHelp"/>
/// (documented as reachable from <see cref="Started"/>, but with NO
/// documented transition back — same "não inventar" treatment as Reopen,
/// registered as a gap in the Fase 6 homologation document, not
/// implemented). <see cref="InTransit"/> is modeled for completeness
/// (Documento 06 recognizes it as a valid state) but has no admin-triggered
/// entry point this increment — Documento 06 itself says the step is
/// optional/"Configuração por tenant" and is reported by the Housekeeper
/// (Portal, Incremento 2), not the Administrator.
/// </summary>
public enum CleaningStatus
{
    Pending = 0,
    Assigned = 1,
    InTransit = 2,
    Started = 3,
    InInspection = 4,
    Completed = 5,
    Cancelled = 6,
    Interrupted = 7,
    WaitingMaterials = 8,
    WaitingHelp = 9,
}
