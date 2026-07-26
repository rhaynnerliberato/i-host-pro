namespace IHostPro.BuildingBlocks.Domain;

/// <summary>
/// Marker interface for events raised in-process, within the boundary of a single
/// Bounded Context, and dispatched synchronously as part of the same unit of work.
/// A Domain Event never crosses a Bounded Context boundary. When a fact represented
/// by a Domain Event is relevant to other contexts, it must be translated into an
/// Integration Event (see IHostPro.BuildingBlocks.Messaging.Abstractions) and
/// published through the Outbox Pattern.
/// See: documentacao do projeto/Architecture Principles.md, Section 8.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredOn { get; }
}
