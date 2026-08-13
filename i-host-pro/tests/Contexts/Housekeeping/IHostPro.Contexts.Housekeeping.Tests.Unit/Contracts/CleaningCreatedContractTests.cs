using System.Text.Json;
using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Contracts;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Contracts;

/// <summary>
/// Proves <see cref="CleaningCreated"/>'s real, current JSON shape — added
/// Fase 7, Incremento 1 (Agenda Foundation) when <see cref="CleaningCreated.ScheduledAtUtc"/>
/// was appended to this pre-existing event. Uses plain <see cref="JsonSerializerOptions"/>
/// defaults (no camelCase/enum converter), matching how this solution's
/// Wolverine transport actually serializes Integration Events today — no
/// custom <c>JsonSerializerOptions</c> is registered anywhere in
/// <c>IHostPro.Api</c>/<c>IHostPro.Worker</c>'s <c>Program.cs</c> for message
/// serialization, so PascalCase property names are the real wire shape.
/// </summary>
public class CleaningCreatedContractTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid CleaningId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid CorrelationId = Guid.NewGuid();

    [Fact]
    public void Round_trips_with_a_ScheduledAtUtc_value_preserving_the_exact_offset()
    {
        var scheduledAtUtc = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero);
        var original = new CleaningCreated
        {
            TenantId = TenantId,
            AggregateId = CleaningId,
            AggregateType = "Cleaning",
            CorrelationId = CorrelationId,
            ActorType = "User",
            CleaningId = CleaningId,
            PropertyId = PropertyId,
            Status = "Pending",
            ScheduledAtUtc = scheduledAtUtc,
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<CleaningCreated>(json);

        roundTripped!.ScheduledAtUtc.Should().Be(scheduledAtUtc);
        roundTripped.ScheduledAtUtc!.Value.Offset.Should().Be(scheduledAtUtc.Offset);
    }

    [Fact]
    public void An_envelope_serialized_before_ScheduledAtUtc_existed_deserializes_it_as_null_never_throws_never_defaults()
    {
        var legacyJsonWithoutScheduledAtUtc = $$"""
            {
                "TenantId": "{{TenantId}}",
                "AggregateId": "{{CleaningId}}",
                "AggregateType": "Cleaning",
                "CorrelationId": "{{CorrelationId}}",
                "ActorType": "User",
                "CleaningId": "{{CleaningId}}",
                "PropertyId": "{{PropertyId}}",
                "Status": "Pending"
            }
            """;

        var act = () => JsonSerializer.Deserialize<CleaningCreated>(legacyJsonWithoutScheduledAtUtc);

        act.Should().NotThrow();
        var value = act();
        value!.ScheduledAtUtc.Should().BeNull();
        value.ScheduledAtUtc.Should().NotBe(default(DateTimeOffset));
    }

    [Fact]
    public void A_new_envelope_with_ScheduledAtUtc_explicitly_null_deserializes_it_as_null_a_legitimate_unscheduled_cleaning()
    {
        var jsonWithExplicitNull = $$"""
            {
                "TenantId": "{{TenantId}}",
                "AggregateId": "{{CleaningId}}",
                "AggregateType": "Cleaning",
                "CorrelationId": "{{CorrelationId}}",
                "ActorType": "User",
                "CleaningId": "{{CleaningId}}",
                "PropertyId": "{{PropertyId}}",
                "Status": "Pending",
                "ScheduledAtUtc": null
            }
            """;

        var value = JsonSerializer.Deserialize<CleaningCreated>(jsonWithExplicitNull);

        value!.ScheduledAtUtc.Should().BeNull();
    }
}
