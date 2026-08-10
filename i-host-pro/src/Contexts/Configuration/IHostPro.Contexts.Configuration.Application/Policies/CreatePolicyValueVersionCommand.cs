using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Creates a new, current version of a policy value at TENANT or PROPERTY
/// scope — never GLOBAL (official decision 2.2). <paramref name="ExpectedVersion"/>
/// is the version the caller believes is currently active (<c>null</c> means
/// "I believe no version exists yet"); a mismatch against the actual current
/// version is a <c>version_conflict</c>, never silently overwritten.
/// <paramref name="Reason"/> is mandatory — enforced by
/// <see cref="CreatePolicyValueVersionCommandValidator"/> before the handler
/// runs, and again by <c>PolicyValue.CreateInitialVersion</c>/<c>CreateNextVersion</c>
/// as a domain-level backstop.
/// </summary>
public sealed record CreatePolicyValueVersionCommand(
    Guid TenantId,
    Guid ActorId,
    string PolicyCode,
    string ScopeType,
    Guid? PropertyId,
    string Value,
    string Reason,
    int? ExpectedVersion,
    string? IpAddress)
    : ICommand<PolicyValueDetailResult>;
