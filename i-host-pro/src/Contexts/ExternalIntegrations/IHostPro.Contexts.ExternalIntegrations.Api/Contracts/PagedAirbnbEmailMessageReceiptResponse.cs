namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record PagedAirbnbEmailMessageReceiptResponse(
    int Page, int PageSize, int TotalCount, IReadOnlyCollection<AirbnbEmailMessageReceiptResponse> Items);
