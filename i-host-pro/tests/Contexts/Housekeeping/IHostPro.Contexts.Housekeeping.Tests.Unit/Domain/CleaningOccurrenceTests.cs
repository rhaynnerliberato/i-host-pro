using FluentAssertions;
using IHostPro.Contexts.Housekeeping.Domain;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Tests.Unit.Domain;

public class CleaningOccurrenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_stores_every_field_verbatim()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var cleaningId = Guid.NewGuid();
        var registeredByUserId = Guid.NewGuid();

        var occurrence = CleaningOccurrence.Create(
            id, tenantId, cleaningId, OccurrenceType.Damage, "Broken lamp in the living room", registeredByUserId, Now);

        occurrence.Id.Should().Be(id);
        occurrence.TenantId.Should().Be(tenantId);
        occurrence.CleaningId.Should().Be(cleaningId);
        occurrence.Type.Should().Be(OccurrenceType.Damage);
        occurrence.Description.Should().Be("Broken lamp in the living room");
        occurrence.RegisteredByUserId.Should().Be(registeredByUserId);
        occurrence.RegisteredAtUtc.Should().Be(Now);
    }

    [Fact]
    public void Create_with_no_description_leaves_it_null()
    {
        var occurrence = CleaningOccurrence.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), OccurrenceType.Noise, null, Guid.NewGuid(), Now);

        occurrence.Description.Should().BeNull();
    }

    [Fact]
    public void Create_normalizes_the_registration_instant_to_UTC()
    {
        var localNow = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(-3));

        var occurrence = CleaningOccurrence.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), OccurrenceType.Animal, null, Guid.NewGuid(), localNow);

        occurrence.RegisteredAtUtc.Offset.Should().Be(TimeSpan.Zero);
        occurrence.RegisteredAtUtc.Should().Be(localNow.ToUniversalTime());
    }
}
