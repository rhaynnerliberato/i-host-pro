using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.ExternalIntegrations.Contracts;

/// <summary>
/// Marks the start of an Airbnb reservation sync execution (Fase 9,
/// Checkpoint 3.2 — "Airbnb Deterministic Foundation"). Formalized now
/// because the CP3.1 Decision Gate catalogued it (Documento 07 §16), but
/// deliberately unpublished/unconsumed this checkpoint — no real sync
/// orchestration exists yet (CP3.2 mandate §26: "NÃO implementar initial sync
/// real ainda"), so <see cref="SyncExecutionId"/> has no owning aggregate to
/// reference until that future checkpoint defines one.
/// </summary>
public sealed record AirbnbSyncStarted : IntegrationEvent
{
    public required Guid SyncExecutionId { get; init; }

    public required DateTimeOffset StartedAtUtc { get; init; }
}
