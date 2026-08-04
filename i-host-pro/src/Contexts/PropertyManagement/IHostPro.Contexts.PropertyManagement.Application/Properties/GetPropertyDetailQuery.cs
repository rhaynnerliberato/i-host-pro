using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.Properties;

/// <summary>
/// Reads a single property's full detail, including its resolved effective
/// address (Checkpoint 3 plan, item 9). Cross-tenant and nonexistent ids are
/// indistinguishable — both return
/// <see cref="Errors.PropertyManagementErrorCodes.PropertyNotFound"/>, the
/// same way <c>GetCondominiumDetailQuery</c> does for condominiums.
/// </summary>
public sealed record GetPropertyDetailQuery(Guid PropertyId) : IQuery<PropertyResult>;
