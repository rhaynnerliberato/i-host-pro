using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Maps one Airbnb listing TITLE (free text, as it appears in a host
/// notification email) to one internal Property, for one tenant — the Airbnb
/// Email Bridge's own resolution mechanism, deliberately separate from
/// <see cref="AirbnbListingMapping"/> (which maps a stable Airbnb-issued
/// listing id, never observed in these emails). Exact match only: the title
/// is trimmed and internal whitespace is collapsed, nothing else — no accent
/// stripping, no fuzzy/similarity matching, no aliasing. A title that does
/// not match an existing row is an explicit "cannot resolve PropertyId, do
/// not import" outcome, never a guess.
/// <see cref="PropertyId"/> carries no physical foreign key to
/// <c>property_management.properties</c> — same opaque-Guid convention
/// <see cref="AirbnbListingMapping.PropertyId"/> already uses across this
/// exact boundary.
/// </summary>
public sealed class AirbnbListingTitleMapping : AggregateRoot<Guid>, ITenantOwned
{
    public Guid TenantId { get; private set; }
    public string ListingTitle { get; private set; } = null!;
    public Guid PropertyId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    private AirbnbListingTitleMapping()
    {
        // EF Core materialization.
    }

    private AirbnbListingTitleMapping(
        Guid id, Guid tenantId, string listingTitle, Guid propertyId, DateTimeOffset createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        ListingTitle = listingTitle;
        PropertyId = propertyId;
        CreatedAtUtc = createdAtUtc;
    }

    public static AirbnbListingTitleMapping Create(
        Guid id, Guid tenantId, string listingTitle, Guid propertyId, DateTimeOffset createdAtUtc)
    {
        var normalized = Normalize(listingTitle);
        if (normalized.Length == 0)
            throw new ArgumentException("Listing title cannot be empty.", nameof(listingTitle));

        return new AirbnbListingTitleMapping(id, tenantId, normalized, propertyId, createdAtUtc);
    }

    public void ChangePropertyId(Guid newPropertyId, DateTimeOffset updatedAtUtc)
    {
        PropertyId = newPropertyId;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Trims and collapses internal whitespace only — the same normalization a lookup must apply to the title parsed from a real email before querying by it.</summary>
    public static string Normalize(string listingTitle)
    {
        if (string.IsNullOrWhiteSpace(listingTitle))
            return string.Empty;

        return System.Text.RegularExpressions.Regex.Replace(listingTitle.Trim(), @"\s+", " ");
    }
}
