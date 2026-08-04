using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

public class CreateCondominiumCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static readonly CondominiumAddressInput ValidAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    private sealed record Fixture(
        FakeCondominiumRepository Repository,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        CreateCondominiumCommandHandler Handler);

    private static Fixture CreateFixture()
    {
        var repository = FakeCondominiumRepository.WithCondominium(null);
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CreateCondominiumCommandHandler(repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, handler);
    }

    private static CreateCondominiumCommand Command(string name = "Condomínio Exemplo", CondominiumAddressInput? address = null) =>
        new(TenantId, ActorId, name, address ?? ValidAddress);

    [Fact]
    public async Task A_valid_request_creates_the_condominium()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Condomínio Exemplo");
        fixture.Repository.AddedCondominiums.Should().ContainSingle();
    }

    [Fact]
    public async Task The_name_is_normalized_with_Trim()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(name: "  Condomínio Exemplo  "), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Condomínio Exemplo");
    }

    [Fact]
    public async Task The_address_is_normalized_via_the_Address_Value_Object()
    {
        var fixture = CreateFixture();
        var address = ValidAddress with { ZipCode = "59090-000", State = "rn" };

        var result = await fixture.Handler.Handle(Command(address: address), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Address.ZipCode.Should().Be("59090000");
        result.Value.Address.State.Should().Be("RN");
    }

    [Fact]
    public async Task An_invalid_address_fails_without_creating_anything()
    {
        var fixture = CreateFixture();
        var invalidAddress = ValidAddress with { ZipCode = "123" };

        var result = await fixture.Handler.Handle(Command(address: invalidAddress), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("condominium_address_invalid");
        fixture.Repository.AddedCondominiums.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Creation_writes_exactly_one_audit_entry_with_the_condominium_created_action_code()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Condominium");
        entry.ActionCode.Should().Be("condominium_created");
        entry.AggregateId.Should().Be(result.Value.Id);
        entry.ChangedFields.Should().BeEmpty();
    }

    [Fact]
    public async Task Creation_enqueues_exactly_one_CondominiumCreated_event_with_correct_ActorId_and_AggregateId()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<CondominiumCreated>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(result.Value.Id);
        events[0].AggregateType.Should().Be("Condominium");
        events[0].CondominiumId.Should().Be(result.Value.Id);
    }

    [Fact]
    public async Task No_name_or_address_content_ever_reaches_the_audit_entry_or_the_event()
    {
        var fixture = CreateFixture();

        await fixture.Handler.Handle(Command(), CancellationToken.None);

        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.ChangedFields.Should().NotContain(f => f.Contains("Condomínio", StringComparison.OrdinalIgnoreCase));

        var @event = fixture.EventCollector.EnqueuedEvents.OfType<CondominiumCreated>().Single();
        // CondominiumCreated only ever declares CondominiumId (and the base
        // envelope fields) — there is no property to even carry name/address.
        typeof(CondominiumCreated).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "Name", "Address", "ZipCode" });
        _ = @event;
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var fixture = CreateFixture();
        using var cts = new CancellationTokenSource();

        var act = async () => await fixture.Handler.Handle(Command(), cts.Token);

        await act.Should().NotThrowAsync();
    }
}
