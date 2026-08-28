using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace IHostPro.ArchitectureTests;

/// <summary>
/// Guards two Checkpoint 2 conventions that plain reflection/NetArchTest
/// cannot express, since they are about literal source text rather than
/// assembly-level type dependencies: no duplicated permission-code string
/// literal, and no access to Identity's messaging schema (Checkpoint 2 plan,
/// item 18: "nenhuma string literal duplicada PROPERTIES:MANAGE existe em
/// Property Management"; "nenhum acesso a identity_messaging"). Locates the
/// repository root via <see cref="CallerFilePathAttribute"/> (resolved at
/// compile time to this file's own absolute path), which is more reliable
/// across local/CI working directories than assuming a fixed relative path
/// from the test runner's current directory.
/// </summary>
public class PropertyManagementSourceConventionTests
{
    private static string RepositoryRoot([CallerFilePath] string thisFilePath = "") =>
        // This file lives at <root>/tests/IHostPro.ArchitectureTests/PropertyManagementSourceConventionTests.cs
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFilePath)!, "..", ".."));

    private static string[] PropertyManagementSourceFiles()
    {
        var srcDirectory = Path.Combine(RepositoryRoot(), "src", "Contexts", "PropertyManagement");

        Directory.Exists(srcDirectory).Should().BeTrue($"expected {srcDirectory} to exist");

        return Directory.GetFiles(srcDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
    }

    [Fact]
    public void No_source_file_contains_the_literal_PROPERTIES_colon_MANAGE_string()
    {
        // The only legitimate source of this exact string is the
        // IdentityPermissionCodes.PropertiesManage constant's own
        // declaration, in Identity.Contracts — never re-typed as a literal
        // anywhere in Property Management's own source.
        var offendingFiles = PropertyManagementSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("\"PROPERTIES:MANAGE\"", StringComparison.Ordinal))
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "PROPERTIES:MANAGE must be referenced only via IdentityPermissionCodes.PropertiesManage, never as a duplicated string literal — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void No_source_file_contains_the_literal_PROPERTIES_colon_READ_colon_OWN_OWNER_string()
    {
        var offendingFiles = PropertyManagementSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("\"PROPERTIES:READ:OWN_OWNER\"", StringComparison.Ordinal))
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "PROPERTIES:READ:OWN_OWNER must be referenced only via IdentityPermissionCodes.PropertiesReadOwnOwner, never as a duplicated string literal — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void No_source_file_contains_the_literal_PROPERTY_OWNER_string()
    {
        // Checkpoint 5 plan, item 4/20: "nenhum literal duplicado de
        // PROPERTY_OWNER" — must be referenced only via
        // IdentityRoleCodes.PropertyOwner.
        var offendingFiles = PropertyManagementSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("\"PROPERTY_OWNER\"", StringComparison.Ordinal))
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "PROPERTY_OWNER must be referenced only via IdentityRoleCodes.PropertyOwner, never as a duplicated string literal — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void Application_source_genuinely_uses_IdentityRoleCodes_not_merely_declares_the_reference()
    {
        // Complements PropertyManagementDependencyTests.
        // Application_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api
        // — mirrors Api_source_genuinely_uses_IdentityPermissionCodes below,
        // confirming Application's new Identity.Contracts reference
        // (Checkpoint 5) is genuinely exercised, not merely an unused
        // project reference.
        var applicationSourceDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "PropertyManagement", "IHostPro.Contexts.PropertyManagement.Application");
        var applicationSourceFiles = Directory.GetFiles(applicationSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var usesIdentityRoleCodes = applicationSourceFiles.Any(path => File.ReadAllText(path).Contains("IdentityRoleCodes.", StringComparison.Ordinal));

        usesIdentityRoleCodes.Should().BeTrue("Property Management's Application source must genuinely reference IdentityRoleCodes, not just carry an unused project reference");
    }

    [Fact]
    public void Api_source_genuinely_uses_IdentityPermissionCodes_not_merely_declares_the_reference()
    {
        // Complements PropertyManagementDependencyTests.
        // Api_Depends_On_Identity_Contracts_Only_Never_Application_Infrastructure_Or_Api
        // — that test cannot observe this via reflection, since
        // IdentityPermissionCodes.PropertiesManage is a `const string` and
        // [Authorize(Policy = ...)] requires a compile-time constant, so the
        // compiler inlines the literal value and erases the metadata
        // reference. Source text still shows the real symbol.
        var apiSourceDirectory = Path.Combine(RepositoryRoot(), "src", "Contexts", "PropertyManagement", "IHostPro.Contexts.PropertyManagement.Api");
        var apiSourceFiles = Directory.GetFiles(apiSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var usesIdentityPermissionCodes = apiSourceFiles.Any(path => File.ReadAllText(path).Contains("IdentityPermissionCodes.", StringComparison.Ordinal));

        usesIdentityPermissionCodes.Should().BeTrue("Property Management's Api source must genuinely reference IdentityPermissionCodes, not just carry an unused project reference");
    }

    [Fact]
    public void No_source_file_references_identity_messaging()
    {
        // Checkpoint 2 plan, item 18: Property Management's own outbox schema
        // is property_management_messaging, provisioned/enrolled
        // independently — it must never enroll, query or grant against
        // identity_messaging.
        var offendingFiles = PropertyManagementSourceFiles()
            .Where(path => File.ReadAllText(path).Contains("identity_messaging", StringComparison.Ordinal))
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "Property Management's own source must never reference identity_messaging — found in: " +
            string.Join(", ", offendingFiles));
    }

    [Fact]
    public void No_source_file_implements_a_Group_or_Portaria_capability()
    {
        // Checkpoint 4 plan, item 19: "nenhuma referência a Grupo ou
        // Portaria" — neither capability exists in this Bounded Context yet.
        // Deliberately specific, code-shaped fragments only — Condominium.cs'
        // own (already-approved, Checkpoint 0) doc comment legitimately
        // mentions "Portarias" in prose to explain it is a deferred,
        // out-of-scope capability, which must not trip this check.
        string[] forbiddenFragments = ["PropertyGroup", "GroupController", "PortariaController", "class Portaria", "record Portaria"];

        var offendingFiles = PropertyManagementSourceFiles()
            .Where(path =>
            {
                var content = File.ReadAllText(path);
                return forbiddenFragments.Any(fragment => content.Contains(fragment, StringComparison.Ordinal));
            })
            .ToArray();

        offendingFiles.Should().BeEmpty(
            "no Group or Portaria capability may be implemented yet — found in: " + string.Join(", ", offendingFiles));
    }

    [Fact]
    public void Exactly_the_known_approved_migrations_exist()
    {
        // Checkpoint 4 plan, item 14/19 originally required "não alterar
        // migration; nenhuma migration nova" (Checkpoints 1-4 through the
        // Property Management-specific checkpoints reused the Checkpoint 1
        // schema unchanged). Fase 10, Checkpoint 4 (Portaria Notification
        // Foundation) is the first legitimate addition — a NEW table
        // (front_desk_contacts), not an alteration of the Checkpoint 1
        // schema — so this test now asserts the closed, known-approved list
        // instead of "exactly one", mirroring the same "Exactly_The_Known_
        // Approved_Types_Exist" update pattern already used elsewhere (e.g.
        // WorkflowOrchestrationArchitectureTests, Fase 10 CP3) when a new
        // checkpoint legitimately adds to a previously-closed count.
        var migrationsDirectory = Path.Combine(
            RepositoryRoot(), "src", "Contexts", "PropertyManagement",
            "IHostPro.Contexts.PropertyManagement.Infrastructure", "Persistence", "Migrations");

        Directory.Exists(migrationsDirectory).Should().BeTrue($"expected {migrationsDirectory} to exist");

        var migrationFiles = Directory.GetFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !name!.EndsWith(".Designer", StringComparison.Ordinal))
            .Select(name => name!)
            .ToArray();

        migrationFiles.Should().BeEquivalentTo(
            ["20260730024157_InitialCreate", "20260827202539_AddFrontDeskContact", "20260828185321_AddPropertyAccessConfiguration"],
            "only the Checkpoint 1 InitialCreate migration, Checkpoint 4's AddFrontDeskContact migration, and " +
            "Checkpoint 6.2's AddPropertyAccessConfiguration migration may exist");
    }
}
