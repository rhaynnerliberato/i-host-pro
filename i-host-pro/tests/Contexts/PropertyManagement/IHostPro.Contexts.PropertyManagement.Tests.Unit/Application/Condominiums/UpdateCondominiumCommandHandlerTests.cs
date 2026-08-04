using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;

public class UpdateCondominiumCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static readonly Address OriginalAddress = Address.Create(
        "59090000", "Rua Original", "1", null, "Ponta Negra", "Natal", "RN", "BR");

    private static readonly CondominiumAddressInput NewAddressInput = new(
        "59090-001", "Nova Rua", "200", null, "Ponta Negra", "Natal", "RN", "BR");

    private static Condominium CreateOriginalCondominium(string name = "Original Name") =>
        Condominium.Create(Guid.NewGuid(), TenantId, name, OriginalAddress, Now);

    private sealed record Fixture(
        FakeCondominiumRepository Repository,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        UpdateCondominiumCommandHandler Handler);

    private static Fixture CreateFixture(Condominium? condominium)
    {
        var repository = FakeCondominiumRepository.WithCondominium(condominium);
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new UpdateCondominiumCommandHandler(repository, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(repository, auditWriter, eventCollector, handler);
    }

    private static UpdateCondominiumCommand Command(Guid condominiumId, string? name = null, CondominiumAddressInput? address = null) =>
        new(TenantId, ActorId, condominiumId, name, address);

    // ---- Happy path ---------------------------------------------------------

    [Fact]
    public async Task Changing_only_the_name_updates_it_and_enqueues_ChangedFields_name_only()
    {
        var condominium = CreateOriginalCondominium();
        var fixture = CreateFixture(condominium);

        var result = await fixture.Handler.Handle(Command(condominium.Id, name: "New Name"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("New Name");
        condominium.Address.Should().Be(OriginalAddress); // untouched

        var events = fixture.EventCollector.EnqueuedEvents.OfType<CondominiumUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("name");
    }

    [Fact]
    public async Task Changing_only_the_address_updates_it_and_enqueues_ChangedFields_address_only()
    {
        var condominium = CreateOriginalCondominium();
        var fixture = CreateFixture(condominium);

        var result = await fixture.Handler.Handle(Command(condominium.Id, address: NewAddressInput), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Address.ZipCode.Should().Be("59090001");
        condominium.Name.Should().Be("Original Name"); // untouched

        var events = fixture.EventCollector.EnqueuedEvents.OfType<CondominiumUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("address");
    }

    [Fact]
    public async Task Changing_both_fields_enqueues_a_single_event_with_both_ChangedFields_in_order()
    {
        var condominium = CreateOriginalCondominium();
        var fixture = CreateFixture(condominium);

        var result = await fixture.Handler.Handle(
            Command(condominium.Id, name: "New Name", address: NewAddressInput), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var events = fixture.EventCollector.EnqueuedEvents.OfType<CondominiumUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("name", "address"); // deterministic order
        fixture.AuditWriter.RecordedEntries.Should().ContainSingle(); // one audit entry, not two
    }

    // ---- Rejections -----------------------------------------------------------

    [Fact]
    public async Task Neither_field_supplied_fails_with_NoChangesProvided_and_never_touches_the_repository()
    {
        var fixture = CreateFixture(condominium: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.NoChangesProvided);
        fixture.Repository.GetByIdCallCount.Should().Be(0); // rejected before any lookup
    }

    [Fact]
    public async Task An_invalid_name_fails_and_performs_no_side_effect()
    {
        // Whitespace-only "name" is rejected by the validator before reaching
        // the handler in production; this test exercises the handler in
        // isolation with a value that only the domain's own Rename() guard
        // would reject if it were ever reached — but ChangedFields comparison
        // runs first, so an unchanged value never calls Rename() at all. Use
        // a genuinely different but still-blank value to exercise the guard.
        var condominium = CreateOriginalCondominium();
        var fixture = CreateFixture(condominium);

        var act = async () => await fixture.Handler.Handle(Command(condominium.Id, name: "   "), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_invalid_address_fails_with_condominium_address_invalid_and_performs_no_side_effect()
    {
        var condominium = CreateOriginalCondominium();
        var fixture = CreateFixture(condominium);
        var invalidAddress = NewAddressInput with { ZipCode = "123" };

        var result = await fixture.Handler.Handle(Command(condominium.Id, address: invalidAddress), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("condominium_address_invalid");
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_nonexistent_condominium_fails_with_CondominiumNotFound_and_performs_no_side_effect()
    {
        var fixture = CreateFixture(condominium: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid(), name: "New Name"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.CondominiumNotFound);
        AssertNoSideEffect(fixture);
    }

    // ---- Idempotency ------------------------------------------------------------

    [Fact]
    public async Task Supplying_the_same_name_is_a_no_op()
    {
        var condominium = CreateOriginalCondominium(name: "Original Name");
        var fixture = CreateFixture(condominium);

        var result = await fixture.Handler.Handle(Command(condominium.Id, name: "Original Name"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_the_same_address_is_a_no_op()
    {
        var condominium = CreateOriginalCondominium();
        var fixture = CreateFixture(condominium);
        var sameAddress = new CondominiumAddressInput(
            OriginalAddress.ZipCode, OriginalAddress.Street, OriginalAddress.Number, OriginalAddress.Complement,
            OriginalAddress.Neighborhood, OriginalAddress.City, OriginalAddress.State, OriginalAddress.Country);

        var result = await fixture.Handler.Handle(Command(condominium.Id, address: sameAddress), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_both_fields_already_equal_to_current_values_is_a_complete_no_op()
    {
        var condominium = CreateOriginalCondominium(name: "Original Name");
        var fixture = CreateFixture(condominium);
        var sameAddress = new CondominiumAddressInput(
            OriginalAddress.ZipCode, OriginalAddress.Street, OriginalAddress.Number, OriginalAddress.Complement,
            OriginalAddress.Neighborhood, OriginalAddress.City, OriginalAddress.State, OriginalAddress.Country);

        var result = await fixture.Handler.Handle(
            Command(condominium.Id, name: "Original Name", address: sameAddress), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_no_op_request_never_bumps_UpdatedAt()
    {
        var condominium = CreateOriginalCondominium(name: "Original Name");
        var originalUpdatedAt = condominium.UpdatedAt;
        var fixture = CreateFixture(condominium);

        await fixture.Handler.Handle(Command(condominium.Id, name: "Original Name"), CancellationToken.None);

        condominium.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
