using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.PropertyManagement.Application.GuestAccess;

/// <summary>
/// Reads a single Property's configured guest-access configuration (Fase
/// 10, Checkpoint 6.2). A nonexistent Property and a Property with no
/// configuration yet are two DISTINCT failure codes — mirrors
/// <c>GetFrontDeskContactByCondominiumQuery</c>'s own reasoning exactly.
/// </summary>
public sealed record GetPropertyAccessConfigurationQuery(Guid PropertyId) : IQuery<PropertyAccessConfigurationResult>;
