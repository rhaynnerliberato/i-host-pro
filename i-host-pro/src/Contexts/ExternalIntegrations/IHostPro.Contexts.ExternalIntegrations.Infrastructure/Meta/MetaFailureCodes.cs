using System.Net;
using IHostPro.Contexts.ExternalIntegrations.Contracts;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Maps a Meta Graph API error (HTTP status + <c>error.code</c>) to the
/// provider-neutral <see cref="ProviderFailureCategory"/> (Fase 9, Checkpoint
/// 2.2 — mandate §32/§33: a small, stable set of categories, never a full
/// Meta error-code catalog). Codes below were confirmed against Meta's own
/// official error-codes reference during this checkpoint's research —
/// deliberately not exhaustive: anything not explicitly recognized falls back
/// to <see cref="ProviderFailureCategory.PermanentFailure"/> for a 4xx, or
/// <see cref="ProviderFailureCategory.TransientProviderFailure"/> for a 5xx —
/// never silently dropped, never misclassified as success.
/// </summary>
internal static class MetaFailureCodes
{
    private static readonly HashSet<int> AuthenticationFailedCodes = [0, 190, 200, 10];
    private static readonly HashSet<int> InvalidRecipientCodes = [131026, 131021];
    private static readonly HashSet<int> InvalidTemplateCodes = [132001, 132015, 132016];
    private static readonly HashSet<int> RateLimitedCodes = [4, 80007, 130429, 131056, 133016];
    private static readonly HashSet<int> TransientCodes = [134102];

    public static ProviderFailureCategory MapToCategory(HttpStatusCode statusCode, int? errorCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return ProviderFailureCategory.AuthenticationFailed;

        if (statusCode == HttpStatusCode.TooManyRequests)
            return ProviderFailureCategory.RateLimited;

        if (errorCode is int code)
        {
            if (AuthenticationFailedCodes.Contains(code))
                return ProviderFailureCategory.AuthenticationFailed;
            if (InvalidRecipientCodes.Contains(code))
                return ProviderFailureCategory.InvalidRecipient;
            if (InvalidTemplateCodes.Contains(code))
                return ProviderFailureCategory.InvalidTemplate;
            if (RateLimitedCodes.Contains(code))
                return ProviderFailureCategory.RateLimited;
            if (TransientCodes.Contains(code))
                return ProviderFailureCategory.TransientProviderFailure;
        }

        return (int)statusCode >= 500
            ? ProviderFailureCategory.TransientProviderFailure
            : ProviderFailureCategory.PermanentFailure;
    }
}
