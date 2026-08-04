using FluentAssertions;
using IHostPro.Contexts.Identity.Contracts;
using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.PropertyManagement.Application.Errors;
using IHostPro.Contexts.PropertyManagement.Application.Owners;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.PropertyManagement.Domain;
using IHostPro.Contexts.PropertyManagement.Domain.ValueObjects;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Condominiums;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Properties;
using IHostPro.Contexts.PropertyManagement.Tests.Unit.Infrastructure;

namespace IHostPro.Contexts.PropertyManagement.Tests.Unit.Application.Owners;

public class LinkPropertyOwnerCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid OwnerUserId = Guid.NewGuid();

    private static readonly Address SomeAddress = Address.Create(
        "59090000", "Rua Exemplo", "100", null, "Ponta Negra", "Natal", "RN", "BR");

    private static readonly IdentityUserEligibility EligibleResult = new(OwnerUserId, IsActive: true, HasRequiredRole: true);

    private static Property CreateProperty() =>
        Property.Create(Guid.NewGuid(), TenantId, PropertyCode.Create("STUDIO-1"), "Studio 1", 2, null, SomeAddress, Now);

    private sealed record Fixture(
        FakeIdentityUserEligibilityReader EligibilityReader,
        FakeLinkPropertyOwnerExecutor Executor,
        FakePropertyRepository PropertyRepository,
        FakePropertyOwnerReader OwnerReader,
        FakePropertyOwnerWriter OwnerWriter,
        FakePropertyAuditWriter AuditWriter,
        FakeIntegrationEventCollector EventCollector,
        LinkPropertyOwnerCommandHandler Handler);

    private static Fixture CreateFixture(
        IdentityUserEligibility? eligibility, Property? property, bool alreadyLinked = false)
    {
        var eligibilityReader = FakeIdentityUserEligibilityReader.WithResult(eligibility);
        var executor = new FakeLinkPropertyOwnerExecutor();
        var propertyRepository = FakePropertyRepository.WithProperty(property);
        var ownerReader = FakePropertyOwnerReader.WithExists(alreadyLinked);
        var ownerWriter = new FakePropertyOwnerWriter();
        var auditWriter = new FakePropertyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = new LinkPropertyOwnerCommandHandler(
            eligibilityReader, executor, propertyRepository, ownerReader, ownerWriter, auditWriter, eventCollector, new FixedTimeProvider(Now));

        return new Fixture(eligibilityReader, executor, propertyRepository, ownerReader, ownerWriter, auditWriter, eventCollector, handler);
    }

    private static LinkPropertyOwnerCommand Command(Guid propertyId) => new(TenantId, ActorId, propertyId, OwnerUserId);

    // ---- Happy path ---------------------------------------------------------

    [Fact]
    public async Task An_eligible_owner_and_an_existing_unlinked_property_link_successfully()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(EligibleResult, property);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.PropertyId.Should().Be(property.Id);
        result.Value.OwnerUserId.Should().Be(OwnerUserId);
        result.Value.CreatedAt.Should().Be(Now);
        fixture.OwnerWriter.LinkedLinks.Should().ContainSingle();
        fixture.OwnerWriter.LinkedLinks[0].OwnerUserId.Should().Be(OwnerUserId);
    }

    [Fact]
    public async Task Eligibility_is_checked_using_the_PROPERTY_OWNER_role_code()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(EligibleResult, property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        fixture.EligibilityReader.LastRequestedRoleCode.Should().Be(IdentityRoleCodes.PropertyOwner);
        fixture.EligibilityReader.LastRequestedTenantId.Should().Be(TenantId);
        fixture.EligibilityReader.LastRequestedUserId.Should().Be(OwnerUserId);
    }

    // ---- Rejections -----------------------------------------------------------

    [Fact]
    public async Task A_nonexistent_owner_user_fails_with_OwnerUserNotFound_and_never_touches_Property_Management()
    {
        var fixture = CreateFixture(eligibility: null, property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotFound);
        fixture.PropertyRepository.GetByIdCallCount.Should().Be(0, "the eligibility check must fail before any Property Management transaction opens");
        fixture.Executor.CallCount.Should().Be(0);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task A_blocked_owner_user_fails_with_OwnerUserNotEligible_and_never_touches_Property_Management()
    {
        var blocked = new IdentityUserEligibility(OwnerUserId, IsActive: false, HasRequiredRole: true);
        var fixture = CreateFixture(blocked, property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotEligible);
        fixture.PropertyRepository.GetByIdCallCount.Should().Be(0);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_active_owner_user_without_the_required_role_fails_with_OwnerUserNotEligible()
    {
        var noRole = new IdentityUserEligibility(OwnerUserId, IsActive: true, HasRequiredRole: false);
        var fixture = CreateFixture(noRole, property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.OwnerUserNotEligible);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_eligible_owner_but_a_nonexistent_property_fails_with_PropertyNotFound()
    {
        var fixture = CreateFixture(EligibleResult, property: null);

        var result = await fixture.Handler.Handle(Command(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyNotFound);
        AssertNoSideEffect(fixture);
    }

    [Fact]
    public async Task An_already_linked_pair_fails_with_PropertyOwnerAlreadyLinked_and_performs_no_side_effect()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(EligibleResult, property, alreadyLinked: true);

        var result = await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PropertyManagementErrorCodes.PropertyOwnerAlreadyLinked);
        AssertNoSideEffect(fixture);
    }

    // ---- Auditoria / eventos -----------------------------------------------------

    [Fact]
    public async Task Linking_writes_exactly_one_audit_entry_with_the_property_owner_linked_action_code_and_owner_user_id_only()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(EligibleResult, property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        fixture.AuditWriter.RecordedEntries.Should().ContainSingle();
        var entry = fixture.AuditWriter.RecordedEntries[0];
        entry.TenantId.Should().Be(TenantId);
        entry.ActorUserId.Should().Be(ActorId);
        entry.EntityType.Should().Be("Property");
        entry.ActionCode.Should().Be("property_owner_linked");
        entry.AggregateId.Should().Be(property.Id);
        entry.ChangedFields.Should().Equal("owner_user_id");
    }

    [Fact]
    public async Task Linking_enqueues_exactly_one_PropertyOwnerLinked_event()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(EligibleResult, property);

        await fixture.Handler.Handle(Command(property.Id), CancellationToken.None);

        var events = fixture.EventCollector.EnqueuedEvents.OfType<PropertyOwnerLinked>().ToArray();
        events.Should().ContainSingle();
        events[0].TenantId.Should().Be(TenantId);
        events[0].ActorId.Should().Be(ActorId.ToString());
        events[0].AggregateId.Should().Be(property.Id);
        events[0].AggregateType.Should().Be("Property");
        events[0].PropertyId.Should().Be(property.Id);
        events[0].OwnerUserId.Should().Be(OwnerUserId);
    }

    [Fact]
    public async Task Cancellation_token_is_accepted_without_throwing()
    {
        var property = CreateProperty();
        var fixture = CreateFixture(EligibleResult, property);
        using var cts = new CancellationTokenSource();

        var act = async () => await fixture.Handler.Handle(Command(property.Id), cts.Token);

        await act.Should().NotThrowAsync();
    }

    private static void AssertNoSideEffect(Fixture fixture)
    {
        fixture.OwnerWriter.LinkedLinks.Should().BeEmpty();
        fixture.AuditWriter.RecordedEntries.Should().BeEmpty();
        fixture.EventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
