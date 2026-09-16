using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

/// <summary>
/// Explicitly maps one Airbnb listing TITLE (as it appears in a real
/// reservation-reminder email) to a local Property, for the current tenant.
/// Exact match only — see <see cref="Domain.AirbnbListingTitleMapping"/>'s
/// own remarks. Deliberately does NOT verify <paramref name="PropertyId"/>
/// exists in PropertyManagement — mirrors the exact same trust boundary
/// <c>AirbnbListingMapping.PropertyId</c> and <c>Reservation.PropertyId</c>
/// already use everywhere else this Bounded Context references a Property by
/// its opaque Guid (no physical FK across contexts, no existing cross-context
/// existence-check mechanism in this codebase to reuse) — an invalid
/// PropertyId here fails no differently than it already can elsewhere.
/// </summary>
public sealed record CreateAirbnbListingTitleMappingCommand(Guid TenantId, string ListingTitle, Guid PropertyId)
    : ICommand<AirbnbListingTitleMappingResult>;
