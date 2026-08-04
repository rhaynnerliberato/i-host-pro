using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class CreatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static readonly PropertyAddressInput ValidAddress = new(
        "59090-000", "Rua Exemplo", "100", "Bloco A", "Ponta Negra", "Natal", "RN", "BR");

    private static readonly AddressResult CondominiumAddress = new(
        "59090000", "Rua do Condomínio", "1", null, "Ponta Negra", "Natal", "RN", "BR");

    private sealed record Fixture(
        FakePropertyRepository Repository,
        FakeCondominiumReader CondominiumReader,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        CreatePropertyCommandHandler Handler);

    private static Fixture CreateFixture(AddressResult? condominiumAddress = null)
    {
        var repository = FakePropertyRepository.WithProperty(null);
        var condominiumReader = FakeCondominiumReader.WithAddress(condominiumAddress);
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new CreatePropertyCommandHandler(repository, condominiumReader, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(repository, condominiumReader, auditWriter, eventCollector, handler);
    }

    private static CreatePropertyCommand Command(
        string code = "STUDIO-1", string name = "Studio 1", int capacity = 2,
        Guid? condominiumId = null, PropertyAddressInput? address = null) =>
        new(TenantId, ActorId, code, name, capacity, condominiumId, address);

    // ---- Happy path ---------------------------------------------------------

    [Fact]
    public async Task A_valid_request_with_own_address_and_no_condominium_creates_the_property_as_Draft()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(address: ValidAddress), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("draft");
        result.Value.Address.Should().NotBeNull();
        result.Value.EffectiveAddressSource.Should().Be("property");
        fixture.Repository.AddedProperties.Should().ContainSingle();
    }

    [Fact]
    public async Task A_valid_request_with_condominium_and_no_own_address_resolves_the_effective_address_from_the_condominium()
    {
        var condominiumId = Guid.NewGuid();
        var fixture = CreateFixture(CondominiumAddress);

        var result = await fixture.Handler.Handle(Command(condominiumId: condominiumId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Address.Should().BeNull();
        result.Value.EffectiveAddress.Should().Be(CondominiumAddress);
        result.Value.EffectiveAddressSource.Should().Be("condominium");
    }

    [Fact]
    public async Task The_code_display_casing_is_preserved_while_uniqueness_relies_on_normalization()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(code: "studio-1", address: ValidAddress), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("studio-1");
    }

    // ---- Rejections -----------------------------------------------------------

    [Fact]
    public async Task Neither_condominium_nor_address_fails_with_PropertyAddressRequired_and_performs_no_side_effect()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAddressRequired);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_nonexistent_condominium_fails_with_CondominiumNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(condominiumAddress: null);

        var result = await fixture.Handler.Handle(Command(condominiumId: Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.CondominiumNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_invalid_code_fails_with_property_code_invalid_and_performs_no_side_effect()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(code: "###", address: ValidAddress), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("property_code_invalid");
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_invalid_address_fails_with_property_address_invalid_and_performs_no_side_effect()
    {
        var fixture = CreateFixture();
        var invalidAddress = ValidAddress with { ZipCode = "123" };

        var result = await fixture.Handler.Handle(Command(address: invalidAddress), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("property_address_invalid");
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_non_positive_capacity_reaching_the_handler_fails_defensively_and_performs_no_side_effect()
    {
        // The validator normally rejects this before the handler is ever
        // reached in production — this exercises Property.Create's own
        // defensive guard, mirroring Condominium's equivalent test.
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(capacity: 0, address: ValidAddress), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    // ---- Auditoria / eventos -----------------------------------------------------

    [Fact]
    public async Task Creation_writes_exactly_one_audit_entry_with_the_property_created_action_code_and_empty_ChangedFields()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(address: ValidAddress), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Property");
        entry.ActionCode.Should().Be("property_created");
        entry.AggregateId.Should().Be(result.Value.Id);
        entry.ChangedFields.Should().BeEmpty();
    }

    [Fact]
    public async Task Creation_enqueues_exactly_one_PropertyCreated_event_with_the_stable_draft_status_code()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.Handle(Command(address: ValidAddress), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyCreated>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(result.Value.Id);
        events[0].AggregateType.Should().Be("Property");
        events[0].PropertyId.Should().Be(result.Value.Id);
        events[0].Status.Should().Be("draft");
    }

    [Fact]
    public async Task No_code_name_capacity_or_address_content_ever_reaches_the_event()
    {
        var fixture = CreateFixture();

        await fixture.Handler.Handle(Command(address: ValidAddress), CancellationToken.None);

        // PropertyCreated only ever declares PropertyId/Status (and the base
        // envelope fields) — there is no property to even carry
        // code/name/capacity/address.
        typeof(PropertyCreated).GetProperties().Select(p => p.Name)
            .Should().NotContain(new[] { "Code", "Name", "Capacity", "Address", "ZipCode" });
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var fixture = CreateFixture();
        using var cts = new CancellationTokenSource();

        var act = async () => await fixture.Handler.Handle(Command(address: ValidAddress), cts.Token);

        await act.Should().NotThrowAsync();
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.Repository.AddedProperties.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
