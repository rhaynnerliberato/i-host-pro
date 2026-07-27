namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Password policy, externalized to configuration — never a Domain invariant
/// (Incremento 1 plan, "Política de senha"). Values below are safe defaults,
/// not a business decision fixed in code.
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "Identity:PasswordPolicy";

    public int MinimumLength { get; set; } = 10;
    public bool RequireDigit { get; set; } = true;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireLowercase { get; set; } = true;
    public bool RequireSpecialCharacter { get; set; } = true;
}
