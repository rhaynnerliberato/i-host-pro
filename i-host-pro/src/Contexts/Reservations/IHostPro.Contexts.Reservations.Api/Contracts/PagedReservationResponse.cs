namespace IHostPro.Contexts.Reservations.Api.Contracts;

public sealed record PagedReservationResponse(int Page, int PageSize, int TotalCount, IReadOnlyCollection<ReservationSummaryResponse> Items);
