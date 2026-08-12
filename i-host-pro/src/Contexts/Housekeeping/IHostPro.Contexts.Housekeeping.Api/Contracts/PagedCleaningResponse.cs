namespace IHostPro.Contexts.Housekeeping.Api.Contracts;

public sealed record PagedCleaningResponse(int Page, int PageSize, int TotalCount, IReadOnlyCollection<CleaningSummaryResponse> Items);
