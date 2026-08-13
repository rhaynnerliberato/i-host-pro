namespace IHostPro.Contexts.Reservations.Application.Schedule;

/// <inheritdoc cref="ListScheduleQuery"/>
public interface IScheduleReader
{
    Task<IReadOnlyList<ScheduleItemResult>> ListAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? propertyId, Guid? housekeeperUserId, string? eventType,
        CancellationToken cancellationToken);
}
