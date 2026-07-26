namespace IHostPro.BuildingBlocks.Messaging.Abstractions;

/// <summary>
/// Generic envelope for every Integration Event published across Bounded Context
/// boundaries. Concrete events (e.g. ReservationConfirmed) are defined inside each
/// context's own "<Context>.Contracts" project and inherit from this record — this
/// is the ONLY generic, business-agnostic piece shared by every context, matching
/// the mandatory fields defined in "Documento 07 - Catálogo de Eventos de Domínio", §3.
/// See: documentacao do projeto/Architecture Principles.md, Sections 8 and 13.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    public required Guid TenantId { get; init; }

    public required Guid AggregateId { get; init; }

    public required string AggregateType { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public int Version { get; init; } = 1;

    public required Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    /// <summary>"User" | "AI" | "System" | "Integration" — never invented, always one of these.</summary>
    public required string ActorType { get; init; }

    public string? ActorId { get; init; }
}
