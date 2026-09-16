namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public enum AirbnbEmailDeltaFetchFailureReason
{
    /// <summary>Network/5xx/429-after-retries — safe to retry the same request later.</summary>
    TransientFailure,

    /// <summary>The provider rejected the stored deltaLink as no longer valid (e.g. HTTP 410 Gone) — the sync state must be reset, never silently reused.</summary>
    InvalidDeltaLink,

    /// <summary>The access token was rejected (e.g. HTTP 401) — distinct from a silent-auth failure, which is caught before this client is ever called.</summary>
    AuthenticationFailed,

    Unknown,
}

/// <summary>Result of one page fetch — never throws for an expected provider failure; mirrors <c>OutboundMessageResult</c>'s own structured-outcome convention.</summary>
public sealed record AirbnbEmailDeltaFetchOutcome
{
    public bool IsSuccess { get; }
    public AirbnbEmailDeltaPage? Page { get; }
    public AirbnbEmailDeltaFetchFailureReason? FailureReason { get; }
    public string? SafeErrorCode { get; }

    private AirbnbEmailDeltaFetchOutcome(
        bool isSuccess, AirbnbEmailDeltaPage? page, AirbnbEmailDeltaFetchFailureReason? failureReason, string? safeErrorCode)
    {
        IsSuccess = isSuccess;
        Page = page;
        FailureReason = failureReason;
        SafeErrorCode = safeErrorCode;
    }

    public static AirbnbEmailDeltaFetchOutcome Success(AirbnbEmailDeltaPage page) => new(true, page, null, null);

    public static AirbnbEmailDeltaFetchOutcome Failure(AirbnbEmailDeltaFetchFailureReason reason, string safeErrorCode) =>
        new(false, null, reason, safeErrorCode);
}
