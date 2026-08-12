using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <summary>Mirrors <c>Cleanings.CleaningStatusCodeMapper</c> exactly — an explicit switch, never <c>ToString()</c>.</summary>
public static class OccurrenceTypeCodeMapper
{
    public static string ToCode(OccurrenceType type) => type switch
    {
        OccurrenceType.Theft => "Theft",
        OccurrenceType.Breakage => "Breakage",
        OccurrenceType.ForgottenObject => "ForgottenObject",
        OccurrenceType.Damage => "Damage",
        OccurrenceType.Animal => "Animal",
        OccurrenceType.Smoking => "Smoking",
        OccurrenceType.Noise => "Noise",
        OccurrenceType.MaterialShortage => "MaterialShortage",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped OccurrenceType."),
    };

    public static OccurrenceType FromCode(string code) => code switch
    {
        "Theft" => OccurrenceType.Theft,
        "Breakage" => OccurrenceType.Breakage,
        "ForgottenObject" => OccurrenceType.ForgottenObject,
        "Damage" => OccurrenceType.Damage,
        "Animal" => OccurrenceType.Animal,
        "Smoking" => OccurrenceType.Smoking,
        "Noise" => OccurrenceType.Noise,
        "MaterialShortage" => OccurrenceType.MaterialShortage,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped occurrence type code."),
    };
}
