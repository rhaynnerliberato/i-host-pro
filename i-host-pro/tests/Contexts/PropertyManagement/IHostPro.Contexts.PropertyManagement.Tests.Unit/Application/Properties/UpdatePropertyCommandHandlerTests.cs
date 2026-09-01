using FluentAssertions;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.PropertyManagement.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;

public class UpdatePropertyCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static readonly Address OriginalAddress = Address.Create(
        "59090000", "Rua Original", "1", null, "Ponta Negra", "Natal", "RN", "BR");

    private static readonly PropertyAddressInput NewAddressInput = new(
        "59090-001", "Nova Rua", "200", null, "Ponta Negra", "Natal", "RN", "BR");

    private static readonly AddressResult CondominiumAddress = new(
        "59090002", "Rua do Condomínio", "2", null, "Ponta Negra", "Natal", "RN", "BR");

    private static Property CreatePropertyWithOwnAddress(string code = "STUDIO-1", string name = "Studio 1", int capacity = 2, Guid? condominiumId = null) =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create(code), name, capacity, condominiumId, OriginalAddress, Now);

    private static Property CreatePropertyWithCondominium(Guid condominiumId, string code = "STUDIO-1", string name = "Studio 1", int capacity = 2) =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create(code), name, capacity, condominiumId, address: null, Now);

    private sealed record Fixture(
        FakePropertyRepository Repository,
        FakeCondominiumReader CondominiumReader,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        UpdatePropertyCommandHandler Handler);

    private static Fixture CreateFixture(Property? property, AddressResult? condominiumAddress = null)
    {
        var repository = FakePropertyRepository.WithProperty(property);
        var condominiumReader = FakeCondominiumReader.WithAddress(condominiumAddress);
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new UpdatePropertyCommandHandler(repository, condominiumReader, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(repository, condominiumReader, auditWriter, eventCollector, handler);
    }

    private static UpdatePropertyCommand Command(
        Guid propertyId,
        Optional<string> code = default,
        Optional<string> name = default,
        Optional<int> capacity = default,
        Optional<Guid?> condominiumId = default,
        Optional<PropertyAddressInput?> address = default,
        Optional<string?> timeZoneId = default) =>
        new(TenantId, ActorId, propertyId, code, name, capacity, condominiumId, address, timeZoneId);

    // ---- Happy path: single-field changes ----------------------------------------

    [Fact]
    public async Task Changing_only_the_code_updates_it_and_enqueues_ChangedFields_code_only()
    {
        var property = CreatePropertyWithOwnAddress(code: "STUDIO-1");
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, code: Optional<string>.Of("STUDIO-2")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Code.Should().Be("STUDIO-2");
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("code");
    }

    [Fact]
    public async Task Changing_only_the_name_updates_it_and_enqueues_ChangedFields_name_only()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("New Name");
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("name");
    }

    [Fact]
    public async Task Changing_only_the_capacity_updates_it_and_enqueues_ChangedFields_capacity_only()
    {
        var property = CreatePropertyWithOwnAddress(capacity: 2);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, capacity: Optional<int>.Of(5)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Capacity.Should().Be(5);
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("capacity");
    }

    [Fact]
    public async Task Reassigning_the_condominium_updates_it_and_enqueues_ChangedFields_condominium_id_only()
    {
        var property = CreatePropertyWithOwnAddress();
        var newCondominiumId = Guid.NewGuid();
        var fixture = CreateFixture(property, CondominiumAddress);

        var result = await fixture.Handler.Handle(
            Command(property.Id, condominiumId: Optional<Guid?>.Of(newCondominiumId)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CondominiumId.Should().Be(newCondominiumId);
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("condominium_id");
    }

    [Fact]
    public async Task Replacing_the_own_address_wholesale_updates_it_and_enqueues_ChangedFields_address_only()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, address: Optional<PropertyAddressInput?>.Of(NewAddressInput)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Address!.ZipCode.Should().Be("59090001");
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("address");
    }

    [Fact]
    public async Task Changing_every_field_at_once_enqueues_a_single_event_with_all_ChangedFields_in_the_approved_order()
    {
        var property = CreatePropertyWithOwnAddress(code: "STUDIO-1", name: "Studio 1", capacity: 2);
        var newCondominiumId = Guid.NewGuid();
        var fixture = CreateFixture(property, CondominiumAddress);

        var result = await fixture.Handler.Handle(
            Command(
                property.Id,
                code: Optional<string>.Of("STUDIO-2"),
                name: Optional<string>.Of("New Name"),
                capacity: Optional<int>.Of(9),
                condominiumId: Optional<Guid?>.Of(newCondominiumId),
                address: Optional<PropertyAddressInput?>.Of(NewAddressInput)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("code", "name", "capacity", "condominium_id", "address");
        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
    }

    // ---- Time zone (Fase 11, Checkpoint 7) ----------------------------------------

    [Fact]
    public async Task Changing_only_the_time_zone_id_to_a_valid_IANA_id_updates_it_and_enqueues_ChangedFields_time_zone_id_only()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, timeZoneId: Optional<string?>.Of("America/Sao_Paulo")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TimeZoneId.Should().Be("America/Sao_Paulo");
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("time_zone_id");
    }

    [Fact]
    public async Task An_invalid_IANA_time_zone_id_fails_with_property_timezone_invalid_and_performs_no_side_effect()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, timeZoneId: Optional<string?>.Of("Not/A_Real_Zone")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("property_timezone_invalid");
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Explicitly_clearing_an_already_configured_time_zone_id_updates_it_to_null_and_enqueues_ChangedFields_time_zone_id_only()
    {
        var property = CreatePropertyWithOwnAddress();
        property.ChangeTimeZone("America/Sao_Paulo", Now);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, timeZoneId: Optional<string?>.Of(null)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TimeZoneId.Should().BeNull();
        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyUpdated>().ToArray();
        events.Should().ContainSingle();
        events[0].ChangedFields.Should().Equal("time_zone_id");
    }

    [Fact]
    public async Task Omitting_the_time_zone_id_leaves_the_current_value_unchanged()
    {
        var property = CreatePropertyWithOwnAddress();
        property.ChangeTimeZone("America/Sao_Paulo", Now);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TimeZoneId.Should().Be("America/Sao_Paulo");
    }

    [Fact]
    public async Task Supplying_the_same_time_zone_id_is_a_no_op()
    {
        var property = CreatePropertyWithOwnAddress();
        property.ChangeTimeZone("America/Sao_Paulo", Now);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, timeZoneId: Optional<string?>.Of("America/Sao_Paulo")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    // ---- Condominium/address final-state rules -----------------------------------

    [Fact]
    public async Task Removing_the_condominium_link_while_supplying_an_own_address_in_the_same_request_succeeds()
    {
        var condominiumId = Guid.NewGuid();
        var property = CreatePropertyWithCondominium(condominiumId);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(
                property.Id,
                condominiumId: Optional<Guid?>.Of(null),
                address: Optional<PropertyAddressInput?>.Of(NewAddressInput)),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CondominiumId.Should().BeNull();
        result.Value.EffectiveAddressSource.Should().Be("property");
    }

    [Fact]
    public async Task Removing_the_condominium_link_without_supplying_an_own_address_fails_with_PropertyAddressRequired()
    {
        var condominiumId = Guid.NewGuid();
        var property = CreatePropertyWithCondominium(condominiumId);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, condominiumId: Optional<Guid?>.Of(null)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAddressRequired);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Removing_the_own_address_when_no_condominium_exists_fails_with_PropertyAddressRequired()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, address: Optional<PropertyAddressInput?>.Of(null)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyAddressRequired);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Removing_the_own_address_when_a_condominium_already_exists_succeeds_and_falls_back_to_the_condominium_address()
    {
        var condominiumId = Guid.NewGuid();
        var property = CreatePropertyWithOwnAddress(condominiumId: condominiumId);
        var fixture = CreateFixture(property, CondominiumAddress);

        var result = await fixture.Handler.Handle(
            Command(property.Id, address: Optional<PropertyAddressInput?>.Of(null)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Address.Should().BeNull();
        result.Value.EffectiveAddress.Should().Be(CondominiumAddress);
        result.Value.EffectiveAddressSource.Should().Be("condominium");
    }

    [Fact]
    public async Task Reassigning_to_a_nonexistent_condominium_fails_with_CondominiumNotFound_and_performs_no_side_effect()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property, condominiumAddress: null);

        var result = await fixture.Handler.Handle(
            Command(property.Id, condominiumId: Optional<Guid?>.Of(Guid.NewGuid())), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.CondominiumNotFound);
        AssertNoSideEffect(fixture);
    }

    // ---- Structural rejections ----------------------------------------------------

    [Fact]
    public async Task No_field_supplied_fails_with_NoChangesProvided_and_never_touches_the_repository()
    {
        var fixture = CreateFixture(property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.NoChangesProvided);
        fixture.Repository.GetByIdCallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_nonexistent_property_fails_with_PropertyNotFound()
    {
        var fixture = CreateFixture(property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid(), name: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
    }

    [Fact]
    public async Task An_invalid_code_fails_with_property_code_invalid_and_performs_no_side_effect()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, code: Optional<string>.Of("###")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("property_code_invalid");
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_invalid_address_fails_with_property_address_invalid_and_performs_no_side_effect()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);
        var invalidAddress = NewAddressInput with { ZipCode = "123" };

        var result = await fixture.Handler.Handle(
            Command(property.Id, address: Optional<PropertyAddressInput?>.Of(invalidAddress)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("property_address_invalid");
        AssertNoSideEffect(fixture);
    }

    // ---- Idempotency ------------------------------------------------------------

    [Fact]
    public async Task Supplying_the_same_code_with_different_casing_is_a_no_op()
    {
        var property = CreatePropertyWithOwnAddress(code: "STUDIO-1");
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, code: Optional<string>.Of("studio-1")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_the_same_name_is_a_no_op()
    {
        var property = CreatePropertyWithOwnAddress(name: "Studio 1");
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("Studio 1")), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_the_same_capacity_is_a_no_op()
    {
        var property = CreatePropertyWithOwnAddress(capacity: 3);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, capacity: Optional<int>.Of(3)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_the_same_condominium_id_is_a_no_op()
    {
        var condominiumId = Guid.NewGuid();
        var property = CreatePropertyWithCondominium(condominiumId);
        var fixture = CreateFixture(property, CondominiumAddress);

        var result = await fixture.Handler.Handle(
            Command(property.Id, condominiumId: Optional<Guid?>.Of(condominiumId)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task Supplying_the_same_address_content_is_a_no_op()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);
        var sameAddress = new PropertyAddressInput(
            OriginalAddress.ZipCode, OriginalAddress.Street, OriginalAddress.Number, OriginalAddress.Complement,
            OriginalAddress.Neighborhood, OriginalAddress.City, OriginalAddress.State, OriginalAddress.Country);

        var result = await fixture.Handler.Handle(
            Command(property.Id, address: Optional<PropertyAddressInput?>.Of(sameAddress)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_no_op_request_never_bumps_UpdatedAt()
    {
        var property = CreatePropertyWithOwnAddress(name: "Studio 1");
        var originalUpdatedAt = property.UpdatedAt;
        var fixture = CreateFixture(property);

        await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("Studio 1")), CancellationToken.None);

        property.UpdatedAt.Should().Be(originalUpdatedAt);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var property = CreatePropertyWithOwnAddress();
        var fixture = CreateFixture(property);
        using var cts = new CancellationTokenSource();

        var act = async () => await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("New Name")), cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ---- Archived regressions (Checkpoint 4 plan, item 6/16) -----------------------

    private static Property CreateArchivedPropertyWithOwnAddress(string code = "STUDIO-1", string name = "Studio 1", int capacity = 2)
    {
        var property = CreatePropertyWithOwnAddress(code, name, capacity);
        property.Archive(Now);
        return property;
    }

    [Fact]
    public async Task An_archived_property_rejects_a_code_change_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress(code: "STUDIO-1");
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, code: Optional<string>.Of("STUDIO-2")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_rejects_a_name_change_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("New Name")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_rejects_a_capacity_change_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress(capacity: 2);
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, capacity: Optional<int>.Of(5)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_rejects_a_condominium_change_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress();
        var fixture = CreateFixture(property, CondominiumAddress);

        var result = await fixture.Handler.Handle(
            Command(property.Id, condominiumId: Optional<Guid?>.Of(Guid.NewGuid())), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_rejects_an_address_change_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, address: Optional<PropertyAddressInput?>.Of(NewAddressInput)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_rejects_a_time_zone_id_change_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress();
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(
            Command(property.Id, timeZoneId: Optional<string?>.Of("America/Sao_Paulo")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_archived_property_rejects_even_a_no_op_PATCH_with_ArchivedPropertyCannotBeModified()
    {
        var property = CreateArchivedPropertyWithOwnAddress(name: "Studio 1");
        var fixture = CreateFixture(property);

        var result = await fixture.Handler.Handle(Command(property.Id, name: Optional<string>.Of("Studio 1")), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.ArchivedPropertyCannotBeModified);
        AssertNoSideEffect(fixture);
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
