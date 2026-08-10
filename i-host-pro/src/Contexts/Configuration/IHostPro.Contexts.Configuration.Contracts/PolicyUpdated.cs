using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Configuration.Contracts;

/// <summary>
/// Published whenever a new <c>PolicyValue</c> version is created (Fase 5,
/// Incremento 1, Checkpoint 6) — the single event for every scope level
/// (official decision 5: "não adotar um evento diferente para cada nível";
/// the scope belongs to the payload, never to the event name). Deliberately
/// minimal: never carries the previous value, the new value, conditions,
/// actions, personal data, or the change reason (official decision 5's own
/// exclusion list) — a consumer that needs the effective value must query
/// the administrative API or the typed readers, never infer it from this
/// event. <see cref="IntegrationEvent.AggregateType"/> is always
/// <c>"PolicyValue"</c>; <see cref="IntegrationEvent.AggregateId"/> is the
/// newly created row's own id. See Documento 07 §28.
/// </summary>
public sealed record PolicyUpdated : IntegrationEvent
{
    public required string PolicyCode { get; init; }

    /// <summary>"Tenant" or "Property" — never "Global": GLOBAL is never editable by a tenant-facing write in this increment (official decision 2.2).</summary>
    public required string ScopeType { get; init; }

    public Guid? ScopeReferenceId { get; init; }

    /// <summary>The new <c>PolicyValue</c> row's own version number — never <see cref="IntegrationEvent.Version"/>, which is the event envelope's schema version, not this business value.</summary>
    public required int PolicyVersion { get; init; }
}
