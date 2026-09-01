using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Application.Policies;

/// <summary>
/// Validates a submitted raw JSON value against its policy code's catalog
/// shape (§3) — technical validation only, never a Reservations business
/// rule (official decisions §2.3). On success, returns the value
/// re-serialized through the same canonical options used everywhere else in
/// this context, so a stored row's formatting is always consistent
/// regardless of how the client submitted it. Declared here (not
/// Infrastructure) because it must run before
/// <see cref="ICreatePolicyValueVersionExecutor"/> opens the write
/// transaction — this is request validation, not persistence.
/// </summary>
internal static class PolicyValueValidation
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Error InvalidPolicyValueError = new(PolicyErrorCodes.InvalidPolicyValue, PolicyErrorCodes.InvalidPolicyValue);

    public static Result<string> ValidateAndNormalize(string policyCode, string rawValue)
    {
        try
        {
            switch (policyCode)
            {
                case "EARLY_CHECKIN":
                    return ValidateEarlyCheckIn(rawValue);
                case "LATE_CHECKOUT":
                    return ValidateLateCheckout(rawValue);
                case "AI_AGENT_BEHAVIOR":
                    return ValidateAiAgentBehavior(rawValue);
                default:
                    // The caller already confirmed policyCode exists in the
                    // catalog before reaching here — an unknown code at this
                    // point means the catalog grew without this validator
                    // being updated, which must fail closed, never silently
                    // accept an unvalidated shape.
                    return Result.Failure<string>(InvalidPolicyValueError);
            }
        }
        catch (JsonException)
        {
            return Result.Failure<string>(InvalidPolicyValueError);
        }
    }

    private static Result<string> ValidateEarlyCheckIn(string rawValue)
    {
        var value = JsonSerializer.Deserialize<EarlyCheckInPolicy>(rawValue, Options);
        if (value is null)
            return Result.Failure<string>(InvalidPolicyValueError);

        return Result.Success(JsonSerializer.Serialize(value, Options));
    }

    private static Result<string> ValidateLateCheckout(string rawValue)
    {
        var value = JsonSerializer.Deserialize<LateCheckoutPolicy>(rawValue, Options);
        if (value is null)
            return Result.Failure<string>(InvalidPolicyValueError);

        if (value.ChargeType == LateCheckoutChargeType.Percentage &&
            (value.ChargeValue is null || value.ChargeValue < 0 || value.ChargeValue > 100))
        {
            return Result.Failure<string>(InvalidPolicyValueError);
        }

        if (value.ChargeType == LateCheckoutChargeType.FixedAmount &&
            (value.ChargeValue is null || value.ChargeValue < 0))
        {
            return Result.Failure<string>(InvalidPolicyValueError);
        }

        if (value.ChargeType == LateCheckoutChargeType.None && value.ChargeValue is not null)
            return Result.Failure<string>(InvalidPolicyValueError);

        return Result.Success(JsonSerializer.Serialize(value, Options));
    }

    /// <summary>Fase 11, Checkpoint 7 — <c>SystemPrompt</c> is the only required field; a blank value is rejected the same way Documento 16 §20 forbids a fixed/empty prompt.</summary>
    private static Result<string> ValidateAiAgentBehavior(string rawValue)
    {
        var value = JsonSerializer.Deserialize<AiAgentBehaviorPolicy>(rawValue, Options);
        if (value is null || string.IsNullOrWhiteSpace(value.SystemPrompt))
            return Result.Failure<string>(InvalidPolicyValueError);

        return Result.Success(JsonSerializer.Serialize(value, Options));
    }
}
