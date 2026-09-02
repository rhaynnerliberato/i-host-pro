using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.GuestOperations.Application;

/// <summary>
/// Requests that a guest's access credential/instructions be delivered
/// (Fase 10, Checkpoint 6.2 — Guest Access Secure Delivery Corrective
/// Implementation). An explicit operational action — never automatic, never
/// scheduled. Dispatched through Mediator via
/// <see cref="IGuestOperationsRequestDispatcher"/> — mirrors
/// <c>RecordGuestCheckedInCommand</c>'s own established shape.
///
/// Fase 12, Checkpoint 4 (Security/Secrets/LGPD Hardening) — this request
/// genuinely has two possible real triggers (an administrator via the Api,
/// or the AI Agent acting on an explicit guest request); <see cref="ActorType"/>/
/// <see cref="ActorId"/> carry whichever one actually invoked it, using the
/// SAME closed vocabulary <c>IntegrationEvent.ActorType</c> already defines
/// platform-wide ("User"/"AI"/"System"/"Integration") — never a value
/// invented for this command specifically. The caller (controller or AI
/// Tool) is the only one who knows which it was; this command never guesses.
/// </summary>
public sealed record RequestGuestAccessDeliveryCommand : ICommand<GuestStayOperationResult>
{
    public required Guid TenantId { get; init; }

    public required Guid ReservationId { get; init; }

    /// <summary>"User" or "AI" — the two real callers of this command (mandate CP4 §8).</summary>
    public required string ActorType { get; init; }

    /// <summary>The authenticated administrator's id (ActorType "User") or the AI Agent's own session id (ActorType "AI") — never a fabricated human user, always required.</summary>
    public required Guid ActorId { get; init; }
}
