using System.Text.Json;

namespace IHostPro.Contexts.Configuration.Api.Contracts;

/// <summary><see cref="Value"/> is embedded as real JSON (a <see cref="JsonElement"/>), never a JSON string containing escaped JSON.</summary>
public sealed record PolicyValueDetailResponse(
    Guid Id, string PolicyCode, string ScopeType, Guid? PropertyId, int Version,
    JsonElement Value, DateTimeOffset CreatedAtUtc, Guid CreatedByUserId, string Reason, bool IsCurrent);
