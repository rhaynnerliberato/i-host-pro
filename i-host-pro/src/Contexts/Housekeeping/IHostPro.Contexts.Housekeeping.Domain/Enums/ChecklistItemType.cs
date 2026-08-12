namespace IHostPro.Contexts.Housekeeping.Domain.Enums;

/// <summary>
/// The fixed checklist item catalog (Fase 6, Incremento 2A) — taken
/// verbatim from Documento 12 §8's "Checklist" examples (Fogão, Geladeira,
/// TV, Ar-condicionado, Banheiro, Enxoval, Lixo, Janela), the only
/// documented source for this catalog. No per-tenant/per-property
/// configuration exists for this list (Checkpoint 0 gate — no documentary
/// support for configurability was found; see Fase 6 doc §21.3).
/// </summary>
public enum ChecklistItemType
{
    Stove = 0,
    Refrigerator = 1,
    Tv = 2,
    AirConditioning = 3,
    Bathroom = 4,
    Linens = 5,
    Trash = 6,
    Window = 7,
}
