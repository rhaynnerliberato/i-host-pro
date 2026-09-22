namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

/// <summary>
/// The result of evaluating one parsed reservation-reminder email as a
/// candidate import — DRY_RUN only. <see cref="WouldImport"/> is <c>true</c>
/// only when every required field parsed AND the listing title resolved to a
/// known <c>PropertyId</c>; this type is never passed to
/// <c>IAirbnbReservationSyncPublisher</c> or any other mutating call.
/// </summary>
public sealed record AirbnbReservationDryRunOutcome
{
    public bool WouldImport { get; }
    public bool ExternalReservationIdPresent { get; }
    public bool DatesParsed { get; }
    public bool GuestCountParsed { get; }
    public bool PropertyResolved { get; }
    public Guid? ResolvedPropertyId { get; }
    public AirbnbReservationReminderParseFailureReason? ParseFailureReason { get; }

    /// <summary>
    /// The parsed listing title that failed to resolve to a mapping — present
    /// only in the <see cref="PropertyNotResolved"/> case (Airbnb Email
    /// Operational Exception Resolution gate). Lets the caller persist it on
    /// the receipt so an operator reviewing a NeedsReview item later knows
    /// which listing title still needs a mapping, without re-parsing the
    /// original email.
    /// </summary>
    public string? UnmatchedListingTitle { get; }

    /// <summary>
    /// The parsed confirmation code, present only when parsing succeeded
    /// (regardless of whether the listing resolved to a Property). This is an
    /// operational identifier meant for storage on the message receipt row -
    /// the same value <c>AirbnbEmailMessageReceipt.ExternalReservationId</c>
    /// already exists to hold - not the guest/body PII this gate's DRY_RUN
    /// evidence must otherwise never expose in logs/reports.
    /// </summary>
    public string? ExternalReservationId { get; }

    /// <summary>
    /// The four fields <c>IAirbnbResolvedReservationSyncPublisher.PublishReservationImportedAsync</c>
    /// needs beyond <see cref="ResolvedPropertyId"/>/<see cref="ExternalReservationId"/>
    /// - present ONLY in the <see cref="Ready"/> case (Automatic Publication
    /// Design + Safety gate). Never logged/reported by any caller - these
    /// exist so the delta sync runner can actually invoke the publisher
    /// without re-parsing the message itself, not for diagnostic output.
    /// </summary>
    public string? GuestName { get; }
    public DateTimeOffset? CheckInAt { get; }
    public DateTimeOffset? CheckOutAt { get; }
    public int? GuestCount { get; }

    private AirbnbReservationDryRunOutcome(
        bool wouldImport, bool externalReservationIdPresent, bool datesParsed, bool guestCountParsed,
        bool propertyResolved, Guid? resolvedPropertyId, AirbnbReservationReminderParseFailureReason? parseFailureReason,
        string? unmatchedListingTitle, string? externalReservationId, string? guestName, DateTimeOffset? checkInAt,
        DateTimeOffset? checkOutAt, int? guestCount)
    {
        WouldImport = wouldImport;
        ExternalReservationIdPresent = externalReservationIdPresent;
        DatesParsed = datesParsed;
        GuestCountParsed = guestCountParsed;
        PropertyResolved = propertyResolved;
        ResolvedPropertyId = resolvedPropertyId;
        ParseFailureReason = parseFailureReason;
        UnmatchedListingTitle = unmatchedListingTitle;
        ExternalReservationId = externalReservationId;
        GuestName = guestName;
        CheckInAt = checkInAt;
        CheckOutAt = checkOutAt;
        GuestCount = guestCount;
    }

    public static AirbnbReservationDryRunOutcome ParseFailed(AirbnbReservationReminderParseFailureReason reason) =>
        new(false, false, false, false, false, null, reason, null, null, null, null, null, null);

    public static AirbnbReservationDryRunOutcome PropertyNotResolved(string externalReservationId, string unmatchedListingTitle) =>
        new(false, true, true, true, false, null, null, unmatchedListingTitle, externalReservationId, null, null, null, null);

    public static AirbnbReservationDryRunOutcome Ready(
        Guid resolvedPropertyId, string externalReservationId, string guestName,
        DateTimeOffset checkInAt, DateTimeOffset checkOutAt, int guestCount) =>
        new(true, true, true, true, true, resolvedPropertyId, null, null, externalReservationId, guestName, checkInAt, checkOutAt, guestCount);
}
