using System.Text.RegularExpressions;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbReservationParser;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbReservationParser;

/// <summary>
/// Parses the real "Lembrete de reserva" (reservation reminder) Airbnb email
/// template — the only reservation-lifecycle template observed with real
/// structural confidence as of this gate (Fase 9 review). The email is
/// notionally a check-in reminder, not the original booking notification, but
/// its reservation card carries exactly the fields a DRY_RUN import needs:
/// confirmation code, check-in/checkout, guest count, and listing name.
///
/// The HTML structure itself was never inspected (only the tenant's own
/// rendered screenshots of their real inbox) — this parser's assumptions
/// about tag/table layout are therefore a best-effort reconstruction from a
/// rendered view, not a verified HTML spec. The real-mailbox validation pass
/// (a disposable smoke harness, never committed) is what actually proves or
/// disproves these assumptions against a live message.
/// </summary>
public sealed class AirbnbReservationReminderParser : IAirbnbReservationReminderParser
{
    public const string ParserVersion = "airbnb-reservation-reminder-v1";

    /// <summary>
    /// Real-mailbox validation (Fase 9 review) proved two distinct real
    /// subject variants: "Lembrete de reserva: [GUEST] chega em [breve|DAY, DATE..]"
    /// (colon separator, first observed via a rendered screenshot) and
    /// "Lembrete de reserva - [GUEST] chega em ..." (dash separator, found on
    /// a different real message in the same mailbox). Both share the same
    /// "Lembrete de reserva" prefix and "chega em" marker around the guest
    /// name - this pattern captures everything between them regardless of
    /// separator or what follows "chega em".
    /// </summary>
    private static readonly Regex SubjectPattern = new(
        @"^Lembrete de reserva\s*[:\-]?\s*(?<guest>.+?)\s+chega em\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] PropertyTypeMarkers =
    [
        "casa/apto inteiro", "apartamento inteiro", "casa inteira",
        "quarto inteiro", "quarto compartilhado", "apto inteiro",
    ];

    private const string CheckInLabel = "check-in";
    private const string CheckOutLabel = "checkout";

    /// <summary>Both observed across real messages in the same mailbox — "reserva" on at least one real template variant, "confirmação" was the label shown in a rendered screenshot of a (possibly different-vintage) variant. Neither is invented.</summary>
    private static readonly string[] ConfirmationCodeLabels = ["código de reserva", "código de confirmação"];

    /// <summary>
    /// "N adultos" and a bare "N hóspede(s)" were both observed across real
    /// messages in the same mailbox. The negative lookbehind on ':' is
    /// required: without it this pattern's "hóspedes?" branch can match the
    /// trailing minutes of a "HH:MM" checkout time immediately followed (on
    /// the next line) by a "Hóspedes" section heading, misreading e.g.
    /// "11:00\nHóspedes" as a guest count of zero — caught by the real-mailbox
    /// validation pass, not by the fixture-based tests alone.
    /// </summary>
    private static readonly Regex GuestCountPattern = new(@"(?<![:\d])(?<count>\d+)\s+(adultos?|hóspedes?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConfirmationCodeShapePattern = new(@"^[A-Z0-9]{9,11}$", RegexOptions.Compiled);
    private static readonly Regex DateTokenPattern = new(
        @"(?<day>\d{1,2})\s+de\s+(?<month>jan|fev|mar|abr|mai|jun|jul|ago|set|out|nov|dez)\.?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TimeTokenPattern = new(@"(?<hour>\d{1,2}):(?<minute>\d{2})", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> MonthAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["fev"] = 2, ["mar"] = 3, ["abr"] = 4, ["mai"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["ago"] = 8, ["set"] = 9, ["out"] = 10, ["nov"] = 11, ["dez"] = 12,
    };

    public AirbnbReservationReminderParseResult Parse(string subject, string body, DateTimeOffset receivedAtUtc)
    {
        var lines = NormalizeToLines(body);
        var fullText = string.Join('\n', lines);
        var fullTextLower = fullText.ToLowerInvariant();

        var propertyTypeLineIndex = FindLineIndexContainingAny(lines, PropertyTypeMarkers);
        var hasCheckIn = fullTextLower.Contains(CheckInLabel);
        var hasCheckOut = fullTextLower.Contains(CheckOutLabel);

        if (propertyTypeLineIndex < 0 || !hasCheckIn || !hasCheckOut)
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.UnsupportedTemplate);

        var listingName = propertyTypeLineIndex > 0 ? lines[propertyTypeLineIndex - 1].Trim() : string.Empty;
        if (listingName.Length == 0)
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.MissingRequiredField, nameof(listingName));

        var guestCountMatch = GuestCountPattern.Match(fullText);
        if (!guestCountMatch.Success)
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.MissingRequiredField, "GuestCount");
        var guestCount = int.Parse(guestCountMatch.Groups["count"].Value);

        var confirmationCodeIndex = FindLineIndexContainingAny(lines, ConfirmationCodeLabels);
        if (confirmationCodeIndex < 0)
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.MissingRequiredField, "ExternalReservationId");

        var codeCandidate = FindNextNonEmptyLine(lines, confirmationCodeIndex);
        if (codeCandidate is null || !ConfirmationCodeShapePattern.IsMatch(codeCandidate))
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.InvalidConfirmationCodeContext);

        // Real-mailbox validation (Fase 9 review) proved the two labels are
        // NOT a shared row-major header ("Check-in\nCheckout\n{date1}\n{date2}\n...")
        // as first assumed - the real layout is column-major PER label
        // ("Check-in\n{date}\n{time}\nCheckout\n{date}\n{time}"). Searching
        // from the EARLIER label (not the later one) captures both sections
        // in the correct chronological order regardless of which of the two
        // shapes a given template variant actually uses.
        var checkInLabelIndex = FindLineIndexContaining(lines, CheckInLabel);
        var checkOutLabelIndex = FindLineIndexContaining(lines, CheckOutLabel);
        var searchStart = Math.Min(checkInLabelIndex, checkOutLabelIndex);
        var searchWindow = string.Join('\n', lines.Skip(searchStart + 1).Take(10));

        var dateMatches = DateTokenPattern.Matches(searchWindow);
        var timeMatches = TimeTokenPattern.Matches(searchWindow);
        if (dateMatches.Count < 2 || timeMatches.Count < 2)
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.MissingRequiredField, "CheckInAt/CheckOutAt");

        var checkInAt = ResolveDate(dateMatches[0], timeMatches[0], receivedAtUtc);
        var checkOutAt = ResolveDate(dateMatches[1], timeMatches[1], receivedAtUtc);
        if (checkOutAt <= checkInAt)
            checkOutAt = checkOutAt.AddYears(1);

        var subjectMatch = SubjectPattern.Match(subject.Trim());
        var guestName = subjectMatch.Success ? subjectMatch.Groups["guest"].Value.Trim() : string.Empty;
        if (guestName.Length == 0)
            return AirbnbReservationReminderParseResult.Failure(AirbnbReservationReminderParseFailureReason.MissingRequiredField, "GuestName");

        return AirbnbReservationReminderParseResult.Success(
            codeCandidate, guestName, checkInAt, checkOutAt, guestCount, listingName);
    }

    private static DateTimeOffset ResolveDate(Match dateMatch, Match timeMatch, DateTimeOffset receivedAtUtc)
    {
        var day = int.Parse(dateMatch.Groups["day"].Value);
        var month = MonthAbbreviations[dateMatch.Groups["month"].Value.ToLowerInvariant()];
        var hour = int.Parse(timeMatch.Groups["hour"].Value);
        var minute = int.Parse(timeMatch.Groups["minute"].Value);

        var year = receivedAtUtc.Year;
        var candidate = new DateTimeOffset(year, month, day, hour, minute, 0, receivedAtUtc.Offset);

        // The rendered template never carries a year. A reminder is sent close
        // to arrival, so if the same-year candidate falls more than ~90 days
        // before the email itself, the stay is almost certainly next year
        // (e.g. a reminder sent in December for an early-January check-in).
        if (candidate < receivedAtUtc.AddDays(-90))
            candidate = candidate.AddYears(1);

        return candidate;
    }

    private static int FindLineIndexContainingAny(IReadOnlyList<string> lines, IReadOnlyList<string> needles)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            if (needles.Any(lower.Contains))
                return i;
        }

        return -1;
    }

    private static int FindLineIndexContaining(IReadOnlyList<string> lines, string needle)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ToLowerInvariant().Contains(needle, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string? FindNextNonEmptyLine(IReadOnlyList<string> lines, int afterIndex)
    {
        for (var i = afterIndex + 1; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return null;
    }

    /// <summary>Strips HTML tags, decodes entities, and inserts line breaks at block-level boundaries so visual line structure survives tag removal. If the input has no tags, it is treated as already-plain text.</summary>
    private static List<string> NormalizeToLines(string html)
    {
        var withBreaks = Regex.Replace(html, "(?i)<(br|/p|/div|/tr|/td|/li|/h[1-6])\\s*/?>", "\n");
        var noTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);

        return decoded
            .Split('\n')
            .Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }
}
