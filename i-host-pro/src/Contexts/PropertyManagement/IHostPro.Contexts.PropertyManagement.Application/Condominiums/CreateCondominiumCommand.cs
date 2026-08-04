using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Creates a new condominium (Checkpoint 2 plan, item 4). <see cref="TenantId"/>/
/// <see cref="ActorId"/> come exclusively from the authenticated
/// Administrator's access token claims — a controller builds this from
/// <c>ClaimsPrincipal</c>, never from the request body. Both
/// <see cref="Name"/> and <see cref="Address"/> are required on creation
/// (Checkpoint 2 plan, item 6: "endereço completo obrigatório") — <see cref="Address"/>
/// is nullable only so a request that omits it entirely can be rejected by
/// <see cref="CreateCondominiumCommandValidator"/>'s <c>NotNull</c> rule with
/// a stable error code, rather than the controller having to invent a
/// placeholder value.
/// </summary>
public sealed record CreateCondominiumCommand(
    Guid TenantId, Guid ActorId, string Name, CondominiumAddressInput? Address) : ICommand<CondominiumResult>;
