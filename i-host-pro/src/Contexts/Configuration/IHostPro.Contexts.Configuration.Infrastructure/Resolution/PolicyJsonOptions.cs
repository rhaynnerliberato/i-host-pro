using System.Text.Json;
using System.Text.Json.Serialization;

namespace IHostPro.Contexts.Configuration.Infrastructure.Resolution;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance every typed policy
/// reader uses to deserialize a <c>PolicyValue.Value</c>/<c>GlobalPolicyValue.Value</c>
/// JSON payload into its Contracts-level record (e.g. <c>EarlyCheckInPolicy</c>).
/// <see cref="JsonSerializerDefaults.Web"/> already gives camelCase property
/// names and case-insensitive matching, matching the catalog's field
/// spelling (§3: <c>allowed</c>, <c>earliestTime</c>, ...). The enum
/// converter maps <c>LateCheckoutChargeType</c> to/from the catalog's exact
/// lowercase-first string values (<c>none|fixedAmount|percentage</c>), never
/// a numeric index.
/// </summary>
internal static class PolicyJsonOptions
{
    public static readonly JsonSerializerOptions Instance = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
