using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Maps <see cref="CleaningStatus"/> to the stable, explicit code exposed in
/// <see cref="CleaningResult"/> and in this context's Integration Events'
/// payloads — mirrors <c>ReservationStatusCodeMapper</c> exactly: an
/// explicit switch, not <c>ToString()</c>, so the exposed contract never
/// silently changes if the enum's member names are ever refactored.
/// </summary>
public static class CleaningStatusCodeMapper
{
    public static string ToCode(CleaningStatus status) => status switch
    {
        CleaningStatus.Pending => "Pending",
        CleaningStatus.Assigned => "Assigned",
        CleaningStatus.InTransit => "InTransit",
        CleaningStatus.Started => "Started",
        CleaningStatus.InInspection => "InInspection",
        CleaningStatus.Completed => "Completed",
        CleaningStatus.Cancelled => "Cancelled",
        CleaningStatus.Interrupted => "Interrupted",
        CleaningStatus.WaitingMaterials => "WaitingMaterials",
        CleaningStatus.WaitingHelp => "WaitingHelp",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped CleaningStatus."),
    };

    /// <summary>
    /// The inverse of <see cref="ToCode"/> — used by <c>ICleaningReader</c>'s
    /// listing filter to translate <c>ListCleaningsQuery.Status</c> (already
    /// validated by <c>ListCleaningsQueryValidator</c> to be one of the
    /// stable codes) back into the enum EF Core queries against.
    /// </summary>
    public static CleaningStatus FromCode(string code) => code switch
    {
        "Pending" => CleaningStatus.Pending,
        "Assigned" => CleaningStatus.Assigned,
        "InTransit" => CleaningStatus.InTransit,
        "Started" => CleaningStatus.Started,
        "InInspection" => CleaningStatus.InInspection,
        "Completed" => CleaningStatus.Completed,
        "Cancelled" => CleaningStatus.Cancelled,
        "Interrupted" => CleaningStatus.Interrupted,
        "WaitingMaterials" => CleaningStatus.WaitingMaterials,
        "WaitingHelp" => CleaningStatus.WaitingHelp,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unmapped cleaning status code."),
    };
}
