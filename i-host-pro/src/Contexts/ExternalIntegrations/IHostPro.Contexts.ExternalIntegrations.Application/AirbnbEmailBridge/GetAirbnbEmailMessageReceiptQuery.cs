using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Single receipt detail (Airbnb Email Operational Exception Resolution gate) — <c>null</c> when it does not exist or belongs to another tenant, never a distinguishing error.</summary>
public sealed record GetAirbnbEmailMessageReceiptQuery(Guid TenantId, Guid ReceiptId) : IQuery<AirbnbEmailMessageReceiptResult?>;
