using IHostPro.BuildingBlocks.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

internal sealed class FakeReservationReader : IReservationReader
{
    private readonly ReservationResult? _detail;
    private readonly IReadOnlyList<ReservationSummaryResult> _summaries;
    private readonly ReservationUpdateSnapshot? _snapshot;
    private readonly uint? _currentXmin;

    private FakeReservationReader(
        ReservationResult? detail, IReadOnlyList<ReservationSummaryResult> summaries,
        ReservationUpdateSnapshot? snapshot, uint? currentXmin)
    {
        _detail = detail;
        _summaries = summaries;
        _snapshot = snapshot;
        _currentXmin = currentXmin;
    }

    public static FakeReservationReader WithDetail(ReservationResult? detail) => new(detail, [], null, null);

    public static FakeReservationReader WithSummaries(IReadOnlyList<ReservationSummaryResult> summaries) => new(null, summaries, null, null);

    /// <summary>
    /// Builds the snapshot <see cref="UpdateReservationCommandHandler"/> reads
    /// BEFORE its write transaction opens, from <paramref name="reservation"/>'s
    /// current field values. <paramref name="currentXmin"/> defaults to the
    /// same value as <paramref name="snapshotXmin"/> — i.e. no concurrent
    /// change occurred — unless a test explicitly wants to simulate the row
    /// changing between the snapshot read and the write transaction's own
    /// re-read (<see cref="IReservationReader.GetCurrentXminAsync"/>).
    /// </summary>
    public static FakeReservationReader WithUpdateSnapshot(Reservation? reservation, uint snapshotXmin = 1, uint? currentXmin = null)
    {
        if (reservation is null)
            return new FakeReservationReader(null, [], null, currentXmin ?? snapshotXmin);

        var snapshot = new ReservationUpdateSnapshot(
            reservation.Id, reservation.PropertyId, reservation.CheckInAt, reservation.CheckOutAt,
            reservation.GuestCount, reservation.Status, snapshotXmin);

        return new FakeReservationReader(null, [], snapshot, currentXmin ?? snapshotXmin);
    }

    public Task<PagedResult<ReservationSummaryResult>> ListAsync(
        Guid? propertyId, string? status, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<ReservationSummaryResult>(page, pageSize, _summaries.Count, _summaries));

    public Task<ReservationResult?> GetByIdAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_detail);

    public Task<ReservationUpdateSnapshot?> GetUpdateSnapshotAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshot);

    public Task<uint?> GetCurrentXminAsync(Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_currentXmin);
}
