namespace IHostPro.Contexts.Housekeeping.Domain.Enums;

/// <summary>
/// The fixed category catalog for a <see cref="CleaningOccurrence"/> (Fase
/// 6, Incremento 2A) — taken verbatim from Documento 12 §8's "Intercorrência"
/// examples (Furto, Quebra, Objeto esquecido, Dano, Animal, Fumo, Ruído,
/// Falta de material), the only documented source for this catalog. No
/// per-tenant/per-property configuration exists for this list (Checkpoint 0
/// gate — no documentary support for configurability was found).
/// </summary>
public enum OccurrenceType
{
    Theft = 0,
    Breakage = 1,
    ForgottenObject = 2,
    Damage = 3,
    Animal = 4,
    Smoking = 5,
    Noise = 6,
    MaterialShortage = 7,
}
