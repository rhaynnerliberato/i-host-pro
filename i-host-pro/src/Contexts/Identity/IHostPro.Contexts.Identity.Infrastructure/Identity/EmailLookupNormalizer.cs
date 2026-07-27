using IHostPro.Contexts.Identity.Domain.ValueObjects;
using Microsoft.AspNetCore.Identity;

namespace IHostPro.Contexts.Identity.Infrastructure.Identity;

/// <summary>
/// Delegates to the exact same normalization function the Domain's
/// <c>Email</c> Value Object uses. Registering this as the platform's
/// <see cref="ILookupNormalizer"/> guarantees UserManager can never compute a
/// normalized username/email that diverges from
/// <c>Email.NormalizedValue</c> — there is exactly one normalization
/// algorithm, referenced from both sides (Incremento 1 plan, Section 2).
/// </summary>
public sealed class EmailLookupNormalizer : ILookupNormalizer
{
    public string? NormalizeName(string? name) => Normalize(name);

    public string? NormalizeEmail(string? email) => Normalize(email);

    private static string? Normalize(string? value) => value is null ? null : EmailNormalizer.Normalize(value);
}
