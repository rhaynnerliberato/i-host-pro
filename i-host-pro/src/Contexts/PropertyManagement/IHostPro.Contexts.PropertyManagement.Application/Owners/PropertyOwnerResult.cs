namespace IHostPro.Contexts.PropertyManagement.Application.Owners;

/// <summary>
/// An Owner ↔ Property link as seen by an Administrator (Checkpoint 5 plan,
/// item 9) — deliberately carries no name/email/status/role of the owner
/// (never queries Identity for the listing), and no <c>createdByUserId</c>/
/// <c>tenantId</c>.
/// </summary>
public sealed record PropertyOwnerResult(Guid PropertyId, Guid OwnerUserId, DateTimeOffset CreatedAt);
