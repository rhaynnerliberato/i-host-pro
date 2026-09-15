namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Bound from <c>ExternalIntegrations:AirbnbEmailBridge</c>. No
/// <c>ClientSecret</c> setting exists by design — this is a public client
/// (Fase 9 review §1-2): the Microsoft Entra app registration must use the
/// "Mobile and desktop applications" platform with redirect URI
/// <c>http://localhost</c>, never a Web/confidential-client registration.
///
/// <see cref="ClientId"/> being unset is a legitimate, common state (no
/// developer has connected a mailbox yet) — resolving it happens lazily,
/// inside <see cref="MsalAirbnbEmailAuthenticator"/>, never validated at host
/// startup, so its absence never blocks Api/Worker startup for anyone not
/// using this feature (same lazy-fail-on-use philosophy as
/// <see cref="AesGcmTokenCacheProtector"/>'s own master key).
/// </summary>
public sealed class AirbnbEmailBridgeOptions
{
    public string? ClientId { get; set; }

    /// <summary>Must match the Entra app registration's "Mobile and desktop applications" redirect URI exactly.</summary>
    public string RedirectUri { get; set; } = "http://localhost";

    /// <summary>
    /// <c>common</c> supports both work/school accounts and personal
    /// Microsoft accounts (Outlook.com/Hotmail) in the same authority —
    /// required since the app registration's supported account type is
    /// "Any Entra ID tenant + personal Microsoft accounts".
    /// </summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com/common";

    /// <summary>
    /// Delegated Microsoft Graph scopes. <c>Mail.Read</c> only — deliberately
    /// not <c>Mail.ReadWrite</c>/<c>Mail.Send</c> (least privilege; nothing in
    /// this bridge ever writes to or sends from the connected mailbox).
    /// </summary>
    public string[] Scopes { get; set; } = ["Mail.Read"];
}
