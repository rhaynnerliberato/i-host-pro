using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Paginated exception listing (Airbnb Email Operational Exception
/// Resolution gate) — mirrors <c>ListCleaningsQuery</c>'s own shape exactly.
/// </summary>
public sealed record ListAirbnbEmailMessageReceiptsQuery(
    Guid TenantId, string? Status, string? ReasonCode, int? Page, int? PageSize)
    : IQuery<PagedResult<AirbnbEmailMessageReceiptResult>>;
