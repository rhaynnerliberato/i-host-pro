using System.Security.Claims;
using FluentAssertions;
using IHostPro.Contexts.Identity.Api.Authorization;
using IHostPro.Contexts.Identity.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Identity.Tests.Unit.Api;

/// <summary>
/// True unit coverage of <see cref="PermissionAuthorizationHandler"/>
/// (Incremento 3, Checkpoint 2 — moved here from
/// <c>IHostPro.Contexts.Identity.Tests.Integration</c> during Checkpoint 2
/// stabilization): no PostgreSQL, no Docker, no real host, no Testcontainers
/// — a hand-written <see cref="FakePermissionReader"/> stands in for
/// <see cref="IPermissionReader"/>, and the handler is invoked directly via
/// the same public <see cref="IAuthorizationHandler.HandleAsync"/> ASP.NET
/// Core itself calls. <c>Identity.Tests.Unit</c> gained a project reference
/// to <c>Identity.Api</c> specifically to make this possible — neither type
/// under test has any database or HTTP dependency of its own.
///
/// PostgreSQL-backed behavior (the real <c>PermissionReader</c>) is covered
/// separately by <c>PermissionReaderTests</c>
/// (<c>Identity.Tests.Integration</c>); the full real pipeline (real host,
/// real JWT) by <c>PermissionAuthorizationEndToEndTests</c> — both of those
/// remain in <c>Identity.Tests.Integration</c> unchanged, since they
/// genuinely need PostgreSQL/a real host.
/// </summary>
public class PermissionAuthorizationHandlerTests
{
    private const string RequiredPermissionCode = "USERS:MANAGE";

    private static PermissionAuthorizationHandler CreateHandler(IPermissionReader reader) =>
        new(reader, NullLogger<PermissionAuthorizationHandler>.Instance);

    private static ClaimsPrincipal AuthenticatedPrincipal(params string[] roleClaimValues)
    {
        var claims = roleClaimValues.Select(value => new Claim("role", value));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
    }

    /// <summary>An identity with no <c>authenticationType</c> is, by ClaimsIdentity's own contract, not authenticated.</summary>
    private static ClaimsPrincipal AnonymousPrincipal() => new(new ClaimsIdentity());

    private static async Task<AuthorizationHandlerContext> AuthorizeAsync(
        IPermissionReader reader, ClaimsPrincipal principal, string permissionCode = RequiredPermissionCode)
    {
        var requirement = new PermissionRequirement(permissionCode);
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await CreateHandler(reader).HandleAsync(context);

        return context;
    }

    // ---- Denial paths ---------------------------------------------------

    [Fact]
    public async Task Unauthenticated_user_is_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());

        var context = await AuthorizeAsync(reader, AnonymousPrincipal());

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Missing_role_claim_is_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var principal = AuthenticatedPrincipal(); // authenticated, but no "role" claim at all

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Empty_role_claim_value_is_treated_as_absent_and_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var principal = AuthenticatedPrincipal("");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Whitespace_only_role_claim_value_is_treated_as_absent_and_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var principal = AuthenticatedPrincipal("   ");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_role_is_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>());
        var principal = AuthenticatedPrincipal("NOT_A_REAL_ROLE");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Role_without_the_required_permission_is_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["HOUSEKEEPER"] = ["SCHEDULE:READ"],
        });
        var principal = AuthenticatedPrincipal("HOUSEKEEPER");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task IPermissionReader_returning_an_empty_collection_is_denied()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = [], // a known role that happens to grant nothing
        });
        var principal = AuthenticatedPrincipal("ADMIN");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Permission_code_comparison_is_ordinal_case_sensitive_not_case_insensitive()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = ["users:manage"], // same code, wrong case
        });
        var principal = AuthenticatedPrincipal("ADMIN");

        var context = await AuthorizeAsync(reader, principal, RequiredPermissionCode); // "USERS:MANAGE"

        context.HasSucceeded.Should().BeFalse("permission codes must match exactly, including case — never case-insensitively");
    }

    [Fact]
    public async Task Permission_code_must_match_exactly_never_as_a_prefix_or_superset()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = ["USERS:MANAGE:EXTRA", "USERS"],
        });
        var principal = AuthenticatedPrincipal("ADMIN");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Unexpected_reader_failure_propagates_and_is_never_converted_into_an_ordinary_denial()
    {
        var reader = FakePermissionReader.ThatThrows(new InvalidOperationException("simulated PostgreSQL failure"));
        var principal = AuthenticatedPrincipal("ADMIN");

        var act = () => AuthorizeAsync(reader, principal);

        // Never becomes a normal denial (HasSucceeded == false with no
        // exception) — it must be impossible to mistake an infrastructure
        // failure for "this role has no permission" from the caller's side.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Grant path -------------------------------------------------------

    [Fact]
    public async Task Role_with_the_required_permission_is_authorized()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = [RequiredPermissionCode],
        });
        var principal = AuthenticatedPrincipal("ADMIN");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Multiple_roles_with_only_one_granting_the_permission_is_authorized()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["OPERATOR"] = ["RESERVATIONS:MANAGE"],
            ["ADMIN"] = [RequiredPermissionCode],
        });
        var principal = AuthenticatedPrincipal("OPERATOR", "ADMIN");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeTrue();
    }

    // ---- What reaches IPermissionReader ------------------------------------

    [Fact]
    public async Task Only_distinct_role_codes_reach_the_reader()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = [RequiredPermissionCode],
        });
        var principal = AuthenticatedPrincipal("ADMIN", "ADMIN", "ADMIN");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeTrue();
        reader.LastRequestedRoleCodes.Should().BeEquivalentTo(["ADMIN"]);
    }

    [Fact]
    public async Task Only_non_empty_role_codes_reach_the_reader()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = [RequiredPermissionCode],
        });
        var principal = AuthenticatedPrincipal("", "   ", "ADMIN");

        var context = await AuthorizeAsync(reader, principal);

        context.HasSucceeded.Should().BeTrue();
        reader.LastRequestedRoleCodes.Should().BeEquivalentTo(["ADMIN"]);
    }

    // ---- No side effects ----------------------------------------------------

    [Fact]
    public async Task Handler_never_mutates_the_principals_claims()
    {
        var reader = FakePermissionReader.WithGrants(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["ADMIN"] = [RequiredPermissionCode],
        });
        var principal = AuthenticatedPrincipal("ADMIN");
        var claimsBefore = principal.Claims.Select(c => (c.Type, c.Value)).ToList();

        await AuthorizeAsync(reader, principal);

        var claimsAfter = principal.Claims.Select(c => (c.Type, c.Value)).ToList();
        claimsAfter.Should().BeEquivalentTo(claimsBefore, options => options.WithStrictOrdering());
    }

    // Not touching ITenantContext is confirmed by inspection, not a runtime
    // test: PermissionAuthorizationHandler's constructor takes only
    // IPermissionReader and ILogger<PermissionAuthorizationHandler> — it
    // holds no reference to ITenantContext at all, so there is no handle
    // through which it could read or mutate it (Incremento 3, Checkpoint 2
    // stabilization).
}

/// <summary>Hand-written test double — this project uses no mocking library, consistent with the rest of the solution.</summary>
internal sealed class FakePermissionReader : IPermissionReader
{
    private readonly IReadOnlyDictionary<string, IReadOnlyCollection<string>> _permissionCodesByRole;
    private readonly Exception? _exceptionToThrow;

    private FakePermissionReader(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionCodesByRole, Exception? exceptionToThrow)
    {
        _permissionCodesByRole = permissionCodesByRole;
        _exceptionToThrow = exceptionToThrow;
    }

    public static FakePermissionReader WithGrants(
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionCodesByRole) =>
        new(permissionCodesByRole, exceptionToThrow: null);

    public static FakePermissionReader ThatThrows(Exception exception) =>
        new(new Dictionary<string, IReadOnlyCollection<string>>(), exception);

    public IReadOnlyCollection<string>? LastRequestedRoleCodes { get; private set; }

    public Task<IReadOnlyCollection<string>> GetPermissionCodesAsync(
        IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        LastRequestedRoleCodes = roleCodes;

        if (_exceptionToThrow is not null)
            throw _exceptionToThrow;

        var granted = roleCodes
            .SelectMany(role => _permissionCodesByRole.TryGetValue(role, out var codes) ? codes : [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult<IReadOnlyCollection<string>>(granted);
    }
}
