using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Infrastructure.Seed;

/// <summary>
/// The platform's fixed policy catalog, seeded via EF Core migration data
/// (deterministic, applied exactly once when the migration runs) — mirrors
/// <c>IdentityCatalogSeed</c> exactly. Fase 5, Incremento 1, catálogo
/// oficial (§3): apenas <c>EARLY_CHECKIN</c> e <c>LATE_CHECKOUT</c> — nenhum
/// outro código existe ou deve ser inventado.
///
/// Only <see cref="PolicyDefinition"/> rows are seeded here — never a
/// <see cref="PolicyValue"/> or <see cref="GlobalPolicyValue"/> (official
/// decision 2.2: "não inventar valores padrão"). <c>Category</c> is a purely
/// descriptive/organizational label with no behavioral effect; no formal
/// category taxonomy is documented anywhere in the approved Fase 5 catalog,
/// so <c>CHECK_IN_OUT</c> is used here as a reasonable, low-risk grouping
/// for both entries — not a business rule.
/// </summary>
public static class ConfigurationCatalogSeed
{
    public static IReadOnlyList<PolicyDefinition> PolicyDefinitions { get; } =
    [
        new PolicyDefinition(
            code: "EARLY_CHECKIN",
            name: "Early Check-in",
            description: "Regras operacionais para autorizar a entrada do hóspede antes do horário padrão de check-in.",
            category: "CHECK_IN_OUT",
            valueType: PolicyValueType.Object,
            schemaVersion: 1,
            isActive: true),

        new PolicyDefinition(
            code: "LATE_CHECKOUT",
            name: "Late Checkout",
            description: "Regras operacionais para autorizar a saída do hóspede após o horário padrão de checkout, incluindo cobrança quando aplicável.",
            category: "CHECK_IN_OUT",
            valueType: PolicyValueType.Object,
            schemaVersion: 1,
            isActive: true),
    ];
}
