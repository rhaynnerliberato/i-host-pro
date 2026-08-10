using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using IHostPro.Contexts.Configuration.Contracts;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Contracts;

/// <summary>
/// Proves <see cref="EarlyCheckInPolicy"/>/<see cref="LateCheckoutPolicy"/>
/// round-trip correctly against the exact camelCase field names approved in
/// the Fase 5, Incremento 1 catalog (§3) — uses its own
/// <see cref="JsonSerializerOptions"/> (deliberately not
/// <c>Infrastructure.Resolution.PolicyJsonOptions</c>, which is internal to
/// that assembly) built the same way (<see cref="JsonSerializerDefaults.Web"/>
/// + a camelCase enum converter), so this test verifies the Contracts shape
/// itself is compatible with that realistic configuration, independent of
/// the Infrastructure-layer reader that actually uses it.
/// </summary>
public class PolicyValueJsonShapeTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void EarlyCheckInPolicy_round_trips_through_the_catalogs_exact_field_names()
    {
        const string json = """{"allowed":true,"earliestTime":"14:00:00","requiresCleaningCompleted":true,"requiresForm":false,"notifyFrontDesk":true}""";

        var value = JsonSerializer.Deserialize<EarlyCheckInPolicy>(json, Options);

        value.Should().Be(new EarlyCheckInPolicy(true, new TimeOnly(14, 0), true, false, true));
    }

    [Fact]
    public void EarlyCheckInPolicy_with_no_earliestTime_deserializes_a_null_TimeOnly()
    {
        const string json = """{"allowed":false,"earliestTime":null,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""";

        var value = JsonSerializer.Deserialize<EarlyCheckInPolicy>(json, Options);

        value!.EarliestTime.Should().BeNull();
    }

    [Theory]
    [InlineData("none", LateCheckoutChargeType.None)]
    [InlineData("fixedAmount", LateCheckoutChargeType.FixedAmount)]
    [InlineData("percentage", LateCheckoutChargeType.Percentage)]
    public void LateCheckoutPolicy_chargeType_round_trips_the_catalogs_exact_string_values(string rawValue, LateCheckoutChargeType expected)
    {
        var json = $$"""{"allowed":true,"latestTime":"15:00:00","chargeType":"{{rawValue}}","chargeValue":null,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":false}""";

        var value = JsonSerializer.Deserialize<LateCheckoutPolicy>(json, Options);

        value!.ChargeType.Should().Be(expected);
    }

    [Fact]
    public void LateCheckoutPolicy_round_trips_a_percentage_charge_with_decimal_precision()
    {
        const string json = """{"allowed":true,"latestTime":"15:00:00","chargeType":"percentage","chargeValue":12.5,"requiresPix":true,"blocksCalendar":true,"updatesCleaning":true}""";

        var value = JsonSerializer.Deserialize<LateCheckoutPolicy>(json, Options);

        value.Should().Be(new LateCheckoutPolicy(true, new TimeOnly(15, 0), LateCheckoutChargeType.Percentage, 12.5m, true, true, true));
    }
}
