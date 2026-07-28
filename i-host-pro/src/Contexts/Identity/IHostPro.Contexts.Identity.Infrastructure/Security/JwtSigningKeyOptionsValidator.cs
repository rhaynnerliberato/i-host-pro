using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Fails IHostPro.Api's startup immediately (via <c>ValidateOnStart</c>) when
/// the configured signing key is missing, malformed, or smaller than
/// <see cref="JwtSigningKeyParser.MinimumKeySizeBits"/> — never lazily on the
/// first login attempt. Delegates the actual parsing/validation to
/// <see cref="JwtSigningKeyParser"/> (single source of truth), so the failure
/// message is guaranteed to never contain the key material.
/// </summary>
public sealed class JwtSigningKeyOptionsValidator : IValidateOptions<JwtSigningKeyOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtSigningKeyOptions options)
    {
        try
        {
            using var rsa = JwtSigningKeyParser.ParseAndValidate(options.PrivateKeyPem);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(
                $"{JwtSigningKeyOptions.SectionName}:{nameof(JwtSigningKeyOptions.PrivateKeyPem)}: {ex.Message}");
        }
    }
}
