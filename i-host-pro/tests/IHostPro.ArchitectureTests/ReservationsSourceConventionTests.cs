using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards Reservations conventions that plain reflection/NetArchTest cannot
/// express, since they are about literal source text — mirrors
/// <c>PropertyManagementSourceConventionTests</c> exactly.
/// </summary>
public class ReservationsSourceConventionTests
{
    private static string RepositoryRoot([CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    // Fase 7, Incremento 1 (Agenda Foundation, Checkpoint 1) legitimately
    // introduces a local, read-only PROJECTION of Housekeeping's own
    // Cleaning (never a duplicate aggregate — see
    // CleaningScheduleProjectionEntry's own doc comment) plus the Wolverine
    // adapters that feed it, and mirrors Housekeeping's own
    // CleaningStatusCodeMapper string values (including "Completed") for
    // Cleaning.Status — a different concept from Reservation.Status, which
    // this file's other tests guard separately. Both concerns share the
    // English words "Cleaning"/"Completed", which the plain substring
    // matches below would otherwise also flag as false positives. Excluded
    // explicitly, by exact file, rather than loosening the fragments
    // themselves — a real duplicate Cleaning aggregate, or a genuinely new
    // Reservation.Status value, anywhere else in this Bounded Context must
    // still fail these tests.
    private static readonly string[] Fase7IncrementoUmAllowedFiles =
    [
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningCreatedHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningAssignedHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningStartedHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningInspectionStartedHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningCompletedHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningCancelledHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningInTransitHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningInterruptedHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningNeedsHelpHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CleaningNeedsMaterialHandler.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Projections", "CleaningScheduleProjectionEntry.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Projections", "CleaningScheduleProjectionSynchronizer.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Persistence", "Mappings", "CleaningScheduleProjectionEntryConfiguration.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "ReservationsMessageExecutionScope.cs"),
        Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "ReservationsModuleExtensions.cs"),
    ];

    private static string[] ReservationsSourceFiles()
    {
        var srcDirectory = Path.Combine(RepositoryRoot(), "src", "Contexts", "Reservations");

        Directory.Exists(srcDirectory).Should().BeTrue($"expected {srcDirectory} to exist");

        return Directory.GetFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
    }

    [Fact]
    public void No_source_file_contains_the_literal_RESERVATIONS_colon_MANAGE_string()
    {
        // The only legitimate source of this exact string is
        // IdentityPermissionCodes.ReservationsManage's own declaration, in
        // Identity.Contracts — never re-typed as a literal in Reservations'
        // own source.
        var offendingFiles = ReservationsSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("\"RESERVATIONS:MANAGE\"", StringComparison.Ordinal))
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "RESERVATIONS:MANAGE must be referenced only via IdentityPermissionCodes.ReservationsManage, never as a duplicated string literal — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void Api_source_genuinely_uses_IdentityPermissionCodes_not_merely_declares_the_reference()
    {
        var apiSourceDirectory = Path.Combine(RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Api");
        var apiSourceFiles = Directory.GetFiles(apiSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var usesIdentityPermissionCodes = apiSourceFiles.Any(path => File.ReadAllText(path).Contains("IdentityPermissionCodes.", StringComparison.Ordinal));

        usesIdentityPermissionCodes.Should().BeTrue("Reservations' Api source must genuinely reference IdentityPermissionCodes, not just carry an unused project reference");
    }

    [Fact]
    public void Application_source_genuinely_uses_IPropertyReservationEligibilityReader_not_merely_declares_the_reference()
    {
        var applicationSourceDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Application");
        var applicationSourceFiles = Directory.GetFiles(applicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var usesEligibilityReader = applicationSourceFiles.Any(
            path => File.ReadAllText(path).Contains("IPropertyReservationEligibilityReader", StringComparison.Ordinal));

        usesEligibilityReader.Should().BeTrue("Reservations' Application source must genuinely reference IPropertyReservationEligibilityReader, not just carry an unused project reference");
    }

    [Fact]
    public void No_source_file_references_identity_messaging_or_property_management_messaging()
    {
        // Reservations' own outbox schema is reservations_messaging,
        // provisioned/enrolled independently — must never enroll, query or
        // grant against another context's messaging schema.
        var offendingFiles = ReservationsSourceFiles()
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return content.Contains("identity_messaging", StringComparison.Ordinal) ||
                       content.Contains("property_management_messaging", StringComparison.Ordinal);
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "Reservations' own source must never reference another context's messaging schema — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void No_source_file_implements_an_out_of_scope_capability()
    {
        // Fase 3, Incremento 1 plan, item 14: none of these capabilities
        // exist in this Bounded Context yet. "class Cleaning"/"record
        // Cleaning" guards against Reservations declaring its own DUPLICATE
        // Cleaning aggregate — see Fase7IncrementoUmAllowedFiles' own doc
        // comment for the legitimate, excluded exception.
        //
        // "Airbnb" removed from this list in Fase 9, Checkpoint 3.2
        // ("Airbnb Deterministic Foundation") — CP3.1's Decision Gate
        // approved ReservationSource.Airbnb/ExternalReservationId/the
        // Airbnb import-update-cancel consumers as genuinely in-scope
        // Reservations concepts now, never a temporary exception the way
        // Fase7IncrementoUmAllowedFiles' Cleaning-projection files are.
        //
        // "EarlyCheckIn"/"LateCheckout" removed from this list in Fase 10,
        // Checkpoint 3 ("Early Check-in / Late Checkout") — the mandate
        // approved RescheduleReservationForEarlyCheckIn/
        // RescheduleReservationForLateCheckout/IReservationScheduleReader
        // (ADR-024 amendment, synchronous exception #7) as genuinely
        // in-scope Reservations concepts now, same "no longer a temporary
        // exception" precedent as Airbnb above.
        //
        // Pricing/Commission/iCal/WhatsApp/Payment/PropertyGroup remain out
        // of scope and still forbidden.
        string[] forbiddenFragments =
        [
            "Booking.com", "iCal", "ICalendar", "class Payment", "record Payment",
            "class Pricing", "record Pricing", "Commission", "WhatsApp", "class Cleaning", "record Cleaning",
            "class PropertyGroup", "record PropertyGroup",
        ];

        var offendingFiles = ReservationsSourceFiles()
            .Where(path => !Fase7IncrementoUmAllowedFiles.Contains(path))
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenFragments.Any(fragment => content.Contains(fragment, StringComparison.Ordinal));
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "no out-of-scope capability may be implemented yet — found in: " + string.Join(", ", offendingFiles));
    }

    [Fact]
    public void Only_Confirmed_Cancelled_and_Closed_statuses_are_declared()
    {
        // Fase 3, Incremento 1 plan, item 6: "não implementar Completed,
        // NoShow ou outros estados agora." — about Reservation.Status only;
        // see Fase7IncrementoUmAllowedFiles' own doc comment for why the
        // Cleaning-projection files legitimately use the word "Completed"
        // for an unrelated concept (Cleaning.Status, mirroring
        // Housekeeping's own CleaningStatusCodeMapper) and are excluded here
        // too. Fase 10, Checkpoint 1 (Guest Operations Foundation) adds the
        // third legitimate status, Closed (the guest's real checkout) — this
        // test's name/scope updated accordingly; "Completed"/"NoShow" remain
        // forbidden.
        string[] forbiddenFragments = ["Completed", "NoShow", "\"completed\"", "\"no_show\""];

        var offendingFiles = ReservationsSourceFiles()
            .Where(path => !Fase7IncrementoUmAllowedFiles.Contains(path))
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenFragments.Any(fragment => content.Contains(fragment, StringComparison.Ordinal));
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "only Confirmed/Cancelled/Closed may exist — found in: " + string.Join(", ", offendingFiles));
    }

    [Fact]
    public void CloseReservationHandler_configures_no_custom_retry_policy()
    {
        // Fase 10, Checkpoint 1 mandate's explicit requirement: Cancelled
        // receiving CloseReservation must rely exclusively on Wolverine's
        // own default single-attempt/dead-letter handling — no
        // RetryWithCooldown/custom Configure(...) policy may ever be
        // attached to this handler.
        var handlerFilePath = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations", "IHostPro.Contexts.Reservations.Infrastructure",
            "Messaging", "CloseReservationHandler.cs");

        File.Exists(handlerFilePath).Should().BeTrue($"expected {handlerFilePath} to exist");

        var content = File.ReadAllText(handlerFilePath);
        content.Should().NotContain("RetryWithCooldown")
            .And.NotContain(".Configure(", "no custom Wolverine retry/error-handling policy may be attached to CloseReservationHandler");
    }

    [Fact]
    public void Only_the_known_approved_migrations_exist()
    {
        // Fase 3, Incremento 1's InitialCreate, plus Fase 7, Incremento 1's
        // (Agenda Foundation, Checkpoint 1) AddCleaningScheduleProjection,
        // plus Fase 9, Checkpoint 3.2's AddExternalReservationIdentity
        // (ReservationSource/ExternalReservationId + the partial unique
        // idempotency index) — updated explicitly, by exact expected name,
        // rather than merely relaxed to "any count," so an unapproved future
        // migration still fails this test the same way an unapproved
        // capability would.
        string[] approvedMigrationSuffixes = ["_InitialCreate", "_AddCleaningScheduleProjection", "_AddExternalReservationIdentity"];

        var migrationsDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations",
            "IHostPro.Contexts.Reservations.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected {migrationsDirectory} to exist");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Designer", StringComparison.Ordinal))
            .ToArray();

        migrationFiles.Should().HaveCount(approvedMigrationSuffixes.Length);
        foreach (var suffix in approvedMigrationSuffixes)
            migrationFiles.Should().ContainSingle(name => name!.EndsWith(suffix, StringComparison.Ordinal));
    }
}
