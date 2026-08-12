using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Housekeeping.Domain;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Shared ABAC-enforcing load used by every self-service "own cleaning"
/// write command (Fase 6, Incremento 2A) — returns <c>null</c> both when the
/// cleaning does not exist AND when it exists but is not assigned to
/// <paramref name="housekeeperUserId"/>, so every caller's "not found" error
/// path already fails closed without distinguishing the two cases (mirrors
/// <c>ICleaningReader.GetByIdForHousekeeperAsync</c>'s own reasoning, kept
/// here too since write commands load through <see cref="IRepository{Cleaning,Guid}"/>,
/// never through <c>ICleaningReader</c>).
/// </summary>
internal static class OwnCleaningLoader
{
    public static async Task<Cleaning?> LoadOwnedAsync(
        IRepository<Cleaning, Guid> repository, Guid cleaningId, Guid housekeeperUserId, CancellationToken cancellationToken)
    {
        var cleaning = await repository.GetByIdAsync(cleaningId, cancellationToken);
        return cleaning is not null && cleaning.AssignedHousekeeperUserId == housekeeperUserId ? cleaning : null;
    }
}
