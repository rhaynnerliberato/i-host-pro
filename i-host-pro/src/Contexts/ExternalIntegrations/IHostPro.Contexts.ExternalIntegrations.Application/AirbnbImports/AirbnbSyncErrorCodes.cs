namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbImports;

/// <summary>Stable error codes for <see cref="IAirbnbReservationSyncPublisher"/> — mirrors <c>ReservationsErrorCodes</c>' convention.</summary>
public static class AirbnbSyncErrorCodes
{
    /// <summary>
    /// No <c>AirbnbListingMapping</c> exists for the current tenant/external
    /// listing id — the publisher cannot resolve an internal
    /// <c>PropertyId</c>, so it must not publish (CP3.2 mandate §3). The
    /// caller must seed the mapping before importing/updating this listing's
    /// reservations.
    /// </summary>
    public const string ListingMappingNotFound = "airbnb_listing_mapping_not_found";
}
