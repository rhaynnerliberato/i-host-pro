using System.Text.Json;

namespace IHostPro.Contexts.Configuration.Api.Contracts;

/// <summary>
/// <see cref="ScopeType"/> is <c>"Tenant"</c> or <c>"Property"</c> only —
/// <c>"Global"</c> is rejected by the handler as <c>forbidden</c> (official
/// decision 2.2). <see cref="ExpectedVersion"/> omitted/<c>null</c> means
/// "I expect no current version to exist yet at this scope."
/// </summary>
public sealed record CreatePolicyValueVersionRequest(
    string? ScopeType, Guid? PropertyId, JsonElement Value, string? Reason, int? ExpectedVersion);
