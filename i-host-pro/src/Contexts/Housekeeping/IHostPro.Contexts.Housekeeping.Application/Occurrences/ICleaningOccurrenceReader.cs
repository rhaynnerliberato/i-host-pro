namespace IHostPro.Contexts.Housekeeping.Application.Occurrences;

/// <summary>
/// Self-service occurrence listing (Fase 6, Incremento 2A) — same ABAC
/// convention as <c>ICleaningReader.ListForHousekeeperAsync</c>:
/// <paramref name="housekeeperUserId"/>-scoped in the implementation by
/// joining against the owning Cleaning's <c>AssignedHousekeeperUserId</c>,
/// never trusting a client-supplied filter.
/// </summary>
public interface ICleaningOccurrenceReader
{
    Task<IReadOnlyList<CleaningOccurrenceResult>> ListForOwnCleaningAsync(
        Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken);
}
