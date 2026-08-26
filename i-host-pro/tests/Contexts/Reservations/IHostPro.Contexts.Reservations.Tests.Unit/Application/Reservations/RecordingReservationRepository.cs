using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

/// <summary>
/// Hand-written test double for the Airbnb import/update/cancel processors
/// (Fase 9, Checkpoint 3.2.1) — unlike <see cref="FakeReservationRepository"/>,
/// <see cref="Update"/> records the call instead of throwing: these
/// processors call it explicitly (unlike the manual Update/Cancel command
/// handlers, which rely solely on the tracked EF Core instance), so the
/// "unknown/no-op never touches the repository" assertions in the new tests
/// need a call count, not a hard failure.
/// </summary>
internal sealed class RecordingReservationRepository : IRepository<Reservation, Guid>
{
    private readonly Reservation? _reservation;

    private RecordingReservationRepository(Reservation? reservation) => _reservation = reservation;

    public static RecordingReservationRepository WithReservation(Reservation? reservation) => new(reservation);

    public int UpdateCallCount { get; private set; }
    public int AddCallCount { get; private set; }
    public List<Reservation> AddedReservations { get; } = [];

    public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_reservation);

    public void Add(Reservation aggregate)
    {
        AddCallCount++;
        AddedReservations.Add(aggregate);
    }

    public void Update(Reservation aggregate) => UpdateCallCount++;

    public void Remove(Reservation aggregate) => throw new NotSupportedException("No exclusion endpoint exists in this checkpoint.");
}
