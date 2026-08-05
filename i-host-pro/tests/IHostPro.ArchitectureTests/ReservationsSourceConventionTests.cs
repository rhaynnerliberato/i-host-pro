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
        // exist in this Bounded Context yet.
        string[] forbiddenFragments =
        [
            "Airbnb", "Booking.com", "iCal", "ICalendar", "class Payment", "record Payment",
            "class Pricing", "record Pricing", "Commission", "WhatsApp", "class Cleaning", "record Cleaning",
            "EarlyCheckIn", "LateCheckout", "class PropertyGroup", "record PropertyGroup",
        ];

        var offendingFiles = ReservationsSourceFiles()
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
    public void Only_Confirmed_and_Cancelled_statuses_are_declared()
    {
        // Fase 3, Incremento 1 plan, item 6: "não implementar Completed,
        // NoShow ou outros estados agora."
        string[] forbiddenFragments = ["Completed", "NoShow", "\"completed\"", "\"no_show\""];

        var offendingFiles = ReservationsSourceFiles()
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenFragments.Any(fragment => content.Contains(fragment, StringComparison.Ordinal));
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "only Confirmed/Cancelled may exist this increment — found in: " + string.Join(", ", offendingFiles));
    }

    [Fact]
    public void Exactly_one_migration_exists()
    {
        var migrationsDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Reservations",
            "IHostPro.Contexts.Reservations.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected {migrationsDirectory} to exist");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Designer", StringComparison.Ordinal))
            .ToArray();

        migrationFiles.Should().ContainSingle("only the Incremento 1 InitialCreate migration may exist");
        migrationFiles.Single()!.Should().EndWith("_InitialCreate");
    }
}
