using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.Contexts.Identity.Tests.Integration;

/// <summary>
/// Test-only Integration Event used exclusively to validate Identity's
/// durable outbox foundation (Incremento 2 plan, Etapa 15A) — atomicity,
/// rollback, retry-without-duplication, and RabbitMQ outage/recovery.
/// Deliberately NOT in <c>IHostPro.Contexts.Identity.Contracts</c>: it is not
/// one of the six real events (<c>UserLoggedIn</c>, etc.), which are out of
/// scope for Etapa 15A and are never published from here.
/// </summary>
public sealed record CanaryIntegrationEvent : IntegrationEvent
{
    public required string Marker { get; init; }
}
