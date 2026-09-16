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
    /// The parsed confirmation code, present only when parsing succeeded
    /// (regardless of whether the listing resolved to a Property). This is an
    /// operational identifier meant for storage on the message receipt row -
    /// the same value <c>AirbnbEmailMessageReceipt.ExternalReservationId</c>
    /// already exists to hold - not the guest/body PII this gate's DRY_RUN
    /// evidence must otherwise never expose in logs/reports.
    /// </summary>
    public string? ExternalReservationId { get; }

    private AirbnbReservationDryRunOutcome(
        bool wouldImport, bool externalReservationIdPresent, bool datesParsed, bool guestCountParsed,
        bool propertyResolved, Guid? resolvedPropertyId, AirbnbReservationReminderParseFailureReason? parseFailureReason,
        string? externalReservationId)
    {
        WouldImport = wouldImport;
        ExternalReservationIdPresent = externalReservationIdPresent;
        DatesParsed = datesParsed;
        GuestCountParsed = guestCountParsed;
        PropertyResolved = propertyResolved;
        ResolvedPropertyId = resolvedPropertyId;
        ParseFailureReason = parseFailureReason;
        ExternalReservationId = externalReservationId;
    }

    public static AirbnbReservationDryRunOutcome ParseFailed(AirbnbReservationReminderParseFailureReason reason) =>
        new(false, false, false, false, false, null, reason, null);

    public static AirbnbReservationDryRunOutcome PropertyNotResolved(string externalReservationId) =>
        new(false, true, true, true, false, null, null, externalReservationId);

    public static AirbnbReservationDryRunOutcome Ready(Guid resolvedPropertyId, string externalReservationId) =>
        new(true, true, true, true, true, resolvedPropertyId, null, externalReservationId);
}
