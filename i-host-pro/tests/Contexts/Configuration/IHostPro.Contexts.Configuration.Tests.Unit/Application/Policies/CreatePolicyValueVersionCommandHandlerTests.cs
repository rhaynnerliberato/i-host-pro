using FluentAssertions;
using IHostPro.Contexts.Configuration.Application.Errors;
using IHostPro.Contexts.Configuration.Application.Policies;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Configuration.Domain;

namespace IHostPro.Contexts.Configuration.Tests.Unit.Application.Policies;

public class CreatePolicyValueVersionCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    private static CreatePolicyValueVersionCommandHandler CreateHandler(
        FakePolicyDefinitionReader definitionReader, FakePolicyValueRepository repository, FakePolicyAuditWriter auditWriter,
        FakeIntegrationEventCollector? eventCollector = null) =>
        new(definitionReader, repository, auditWriter, new PassThroughCreatePolicyValueVersionExecutor(),
            eventCollector ?? new FakeIntegrationEventCollector(), TimeProvider.System);

    [Fact]
    public async Task Returns_policy_not_found_for_an_unknown_code()
    {
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes(), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "NOT_A_REAL_CODE", "Tenant", null, """{"allowed":true}""", "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.PolicyNotFound);
    }

    [Fact]
    public async Task Returns_forbidden_for_Global_scope()
    {
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Global", null, """{"allowed":true}""", "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Returns_scope_not_supported_for_Property_scope_without_a_propertyId()
    {
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Property", null, """{"allowed":true}""", "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.ScopeNotSupported);
    }

    [Fact]
    public async Task Returns_invalid_policy_value_for_a_malformed_shape()
    {
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Tenant", null, """{"allowed":"not-a-boolean"}""", "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.InvalidPolicyValue);
    }

    [Theory]
    [InlineData("""{"allowed":true,"chargeType":"percentage","requiresPix":false,"blocksCalendar":false,"updatesCleaning":false}""")]
    [InlineData("""{"allowed":true,"chargeType":"percentage","chargeValue":150,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":false}""")]
    [InlineData("""{"allowed":true,"chargeType":"none","chargeValue":10,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":false}""")]
    [InlineData("""{"allowed":true,"chargeType":"fixedAmount","chargeValue":-5,"requiresPix":false,"blocksCalendar":false,"updatesCleaning":false}""")]
    public async Task Returns_invalid_policy_value_for_LATE_CHECKOUT_charge_rule_violations(string rawValue)
    {
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("LATE_CHECKOUT"), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "LATE_CHECKOUT", "Tenant", null, rawValue, "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.InvalidPolicyValue);
    }

    [Fact]
    public async Task Returns_version_conflict_when_expectedVersion_is_null_but_a_current_version_exists()
    {
        var current = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""", DateTimeOffset.UtcNow, ActorId, "initial");
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueRepository.WithCurrent(current), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Tenant", null, """{"allowed":false}""", "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.VersionConflict);
    }

    [Fact]
    public async Task Returns_version_conflict_when_expectedVersion_does_not_match_the_current_version()
    {
        var current = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""", DateTimeOffset.UtcNow, ActorId, "initial");
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueRepository.WithCurrent(current), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Tenant", null, """{"allowed":false}""", "reason", 99, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.VersionConflict);
    }

    [Fact]
    public async Task Returns_version_conflict_when_expectedVersion_is_given_but_no_current_version_exists()
    {
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter());

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Tenant", null, """{"allowed":true}""", "reason", 1, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(PolicyErrorCodes.VersionConflict);
    }

    [Fact]
    public async Task Creates_version_1_when_no_current_version_exists_and_expectedVersion_is_null()
    {
        var repository = FakePolicyValueRepository.WithCurrent(null);
        var auditWriter = new FakePolicyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), repository, auditWriter, eventCollector);

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Tenant", null, """{"allowed":true,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""", "initial setup", null, "203.0.113.1"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(1);
        result.Value.IsCurrent.Should().BeTrue();
        repository.AddedValues.Should().ContainSingle();
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.PreviousVersion == null && e.NewVersion == 1 && e.IpAddress == "203.0.113.1");

        var @event = eventCollector.EnqueuedEvents.Should().ContainSingle().Subject.Should().BeOfType<PolicyUpdated>().Subject;
        @event.TenantId.Should().Be(TenantId);
        @event.AggregateId.Should().Be(result.Value.Id);
        @event.AggregateType.Should().Be("PolicyValue");
        @event.ActorType.Should().Be("User");
        @event.ActorId.Should().Be(ActorId.ToString());
        @event.PolicyCode.Should().Be("EARLY_CHECKIN");
        @event.ScopeType.Should().Be("Tenant");
        @event.ScopeReferenceId.Should().BeNull();
        @event.PolicyVersion.Should().Be(1);
    }

    [Fact]
    public async Task Creates_the_next_version_and_supersedes_the_previous_one_when_expectedVersion_matches()
    {
        var current = PolicyValue.CreateInitialVersion(
            Guid.NewGuid(), TenantId, "EARLY_CHECKIN", PolicyScope.Tenant(), """{"allowed":true}""", DateTimeOffset.UtcNow, ActorId, "initial");
        var repository = FakePolicyValueRepository.WithCurrent(current);
        var auditWriter = new FakePolicyAuditWriter();
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes("EARLY_CHECKIN"), repository, auditWriter, eventCollector);

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "EARLY_CHECKIN", "Tenant", null, """{"allowed":false,"requiresCleaningCompleted":false,"requiresForm":false,"notifyFrontDesk":false}""", "policy change", 1, null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(2);
        current.IsCurrent.Should().BeFalse("the previous current row must be superseded, never deleted");
        repository.AddedValues.Should().ContainSingle();
        auditWriter.RecordedEntries.Should().ContainSingle(e => e.PreviousVersion == 1 && e.NewVersion == 2);

        var @event = eventCollector.EnqueuedEvents.Should().ContainSingle().Subject.Should().BeOfType<PolicyUpdated>().Subject;
        @event.PolicyVersion.Should().Be(2);
    }

    [Fact]
    public async Task Enqueues_no_event_when_the_command_is_rejected_before_the_executor_runs()
    {
        var eventCollector = new FakeIntegrationEventCollector();
        var handler = CreateHandler(FakePolicyDefinitionReader.WithCodes(), FakePolicyValueRepository.WithCurrent(null), new FakePolicyAuditWriter(), eventCollector);

        var result = await handler.Handle(
            new CreatePolicyValueVersionCommand(TenantId, ActorId, "NOT_A_REAL_CODE", "Tenant", null, """{"allowed":true}""", "reason", null, null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        eventCollector.EnqueuedEvents.Should().BeEmpty();
    }
}
