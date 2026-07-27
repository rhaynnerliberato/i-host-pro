namespace IHostPro.Contexts.Identity.Domain.ValueObjects;

/// <summary>
/// The single normalization algorithm for email/username lookup, shared by the
/// <see cref="Email"/> Value Object (Domain) and the custom
/// <c>ILookupNormalizer</c> registered for ASP.NET Core Identity
/// (Infrastructure). Having exactly one implementation, referenced from both
/// sides, is what guarantees `UserManager` can never compute a normalized value
/// that diverges from what <see cref="Email.NormalizedValue"/> already holds —
/// see the Incremento 1 plan, Section 2 ("como será evitada divergência").
/// </summary>
public static class EmailNormalizer
{
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
