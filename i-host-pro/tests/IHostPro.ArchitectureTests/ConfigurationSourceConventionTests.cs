using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards Configuration &amp; Policy conventions that plain reflection/
/// NetArchTest cannot express, since they are about literal source text —
/// mirrors <c>ReservationsSourceConventionTests</c>. Fase 5, Incremento 1
/// (Policy Engine Foundation), Checkpoint 1: only the checks meaningful at
/// this checkpoint (no domain/entities exist yet) are included here —
/// out-of-scope-capability and single-migration guards are added in
/// Checkpoint 2 once there is real implementation to check against, exactly
/// mirroring how Reservations' own equivalent checks were only added once
/// its first real entities/migration existed.
/// </summary>
public class ConfigurationSourceConventionTests
{
    private static string RepositoryRoot([CallerFilePath] string thisFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    private static string[] ConfigurationSourceFiles()
    {
        var srcDirectory = Path.Combine(RepositoryRoot(), "src", "Contexts", "Configuration");

        Directory.Exists(srcDirectory).Should().BeTrue($"expected {srcDirectory} to exist");

        return Directory.GetFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
    }

    [Fact]
    public void No_source_file_contains_the_literal_POLICIES_permission_strings()
    {
        // The only legitimate source of these exact strings is
        // IdentityPermissionCodes.PoliciesRead/.PoliciesManage's own
        // declaration, in Identity.Contracts — never re-typed as a literal
        // in Configuration's own source.
        string[] forbiddenLiterals = ["\"POLICIES:READ\"", "\"POLICIES:MANAGE\""];

        var offendingFiles = ConfigurationSourceFiles()
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenLiterals.Any(literal => content.Contains(literal, StringComparison.Ordinal));
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "POLICIES:READ/POLICIES:MANAGE must be referenced only via IdentityPermissionCodes, never as a duplicated string literal — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void No_source_file_references_another_contexts_messaging_schema()
    {
        // Configuration & Policy's own outbox schema is
        // configuration_messaging, provisioned/enrolled independently — must
        // never enroll, query or grant against another context's messaging
        // schema.
        var offendingFiles = ConfigurationSourceFiles()
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return content.Contains("identity_messaging", StringComparison.Ordinal) ||
                       content.Contains("property_management_messaging", StringComparison.Ordinal) ||
                       content.Contains("reservations_messaging", StringComparison.Ordinal);
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "Configuration & Policy's own source must never reference another context's messaging schema — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void No_source_file_implements_reservations_functional_integration_yet()
    {
        // Fase 5, Incremento 1 official decisions §2.3: "Não alterar
        // Reservations neste incremento" — Configuration's own source must
        // not reach into Reservations at all (also enforced by
        // ConfigurationDependencyTests at the assembly level); this
        // additionally guards against a same-namespace reimplementation of
        // Reservations-owned concepts.
        string[] forbiddenFragments = ["ReservationsDbContext", "IReservationReader", "IReservationsRequestDispatcher"];

        var offendingFiles = ConfigurationSourceFiles()
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenFragments.Any(fragment => content.Contains(fragment, StringComparison.Ordinal));
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "no functional integration with Reservations exists yet — found in: " + string.Join(", ", offendingFiles));
    }

    [Fact]
    public void Exactly_one_migration_exists_and_is_named_InitialCreate()
    {
        // Checkpoint 2 (Domínio e persistência) added the first (and, as of
        // this checkpoint, only) migration — replaces the Checkpoint 1 guard
        // that asserted no migration existed yet, per that guard's own
        // documented instruction to replace (not merely relax) it once a
        // migration was added.
        var migrationsDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "Configuration",
            "IHostPro.Contexts.Configuration.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected {migrationsDirectory} to exist");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*_InitialCreate.cs", SearchOption.TopDirectoryOnly);
        migrationFiles.Should().HaveCount(1, "exactly one InitialCreate migration should exist at this checkpoint");

        var designerFiles = Directory.GetFiles(migrationsDirectory, "*_InitialCreate.Designer.cs", SearchOption.TopDirectoryOnly);
        designerFiles.Should().HaveCount(1);

        File.Exists(Path.Combine(migrationsDirectory, "ConfigurationDbContextModelSnapshot.cs")).Should().BeTrue();
    }
}
