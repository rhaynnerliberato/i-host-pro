using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Reads a single condominium's full detail (Checkpoint 2 plan, item 4).
/// Cross-tenant and nonexistent ids are indistinguishable — both return
/// <see cref="Errors.PropertyManagementErrorCodes.CondominiumNotFound"/>, the
/// same way <c>GetUserByIdQuery</c> does for users.
/// </summary>
public sealed record GetCondominiumDetailQuery(Guid CondominiumId) : IQuery<CondominiumResult>;
