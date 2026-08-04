using IHostPro.Contexts.PropertyManagement.Domain.Enums;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Maps <see cref="PropertyStatus"/> to the stable lowercase code exposed in
/// <see cref="PropertyResult"/> and in the <c>PropertyCreated</c> integration
/// event's payload (Checkpoint 3 plan, item 11: "Status deve ser um código
/// estável ('draft'), não o nome arbitrário do enum") — an explicit switch,
/// not <c>ToString().ToLowerInvariant()</c>, so the exposed contract never
/// silently changes if the enum's member names are ever refactored.
/// </summary>
public static class PropertyStatusCodeMapper
{
    public static string ToCode(PropertyStatus status) => status switch
    {
        PropertyStatus.Draft => "draft",
        PropertyStatus.Active => "active",
        PropertyStatus.Inactive => "inactive",
        PropertyStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped PropertyStatus."),
    };
}
