using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Domain;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakeReservationRepository : IRepository<Reservation, Guid>
{
    private readonly Reservation? _reservation;

    private FakeReservationRepository(Reservation? reservation) => _reservation = reservation;

    public static FakeReservationRepository WithReservation(Reservation? reservation) => new(reservation);

    public int GetByIdCallCount { get; private set; }
    public Guid? LastRequestedId { get; private set; }
    public List<Reservation> AddedReservations { get; } = [];

    public Task<Reservation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        LastRequestedId = id;
        return Task.FromResult(_reservation);
    }

    public void Add(Reservation aggregate) => AddedReservations.Add(aggregate);
    public void Update(Reservation aggregate) => throw new NotSupportedException("Not exercised — mutation happens via the tracked instance itself.");
    public void Remove(Reservation aggregate) => throw new NotSupportedException("No exclusion endpoint exists in this checkpoint.");
}
