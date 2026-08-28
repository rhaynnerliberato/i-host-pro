using FluentAssertions;
using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.GuestOperations.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>
/// Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery Corrective
/// Implementation) — proves <see cref="GuestAccessDeliveryProcessor"/>
/// deterministically, with special emphasis on the CRITICAL security
/// property from CP6.1 Decision Gate item 16: the credential reaches the
/// connector in full, but is NEVER persisted in <c>Message.RenderedContent</c>.
/// </summary>
public class GuestAccessDeliveryProcessorTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ReservationId = Guid.NewGuid();
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid GuestStayOperationId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
    private const string CredentialTemplateKey = "GUEST_ACCESS_CREDENTIAL";
    private const string InstructionsTemplateKey = "GUEST_ACCESS_INSTRUCTIONS";
    private const string CredentialTemplateContent = "Ola {{GuestName}}, o codigo de acesso e: {{AccessCredential}}";
    private const string InstructionsTemplateContent = "Ola {{GuestName}}, instrucoes: {{AccessInstructions}}";
    private const string GuestPhone = "+5511999998888";
    private const string RedactedContentMarker = "[SENSITIVE CONTENT REDACTED]";

    /// <summary>A sentinel value that must NEVER appear anywhere except inside a connector dispatch — never in a persisted Message.</summary>
    private const string SentinelCredential = "SENTINEL-DOOR-CODE-9F3A7C";

    private const string SomeInstructions = "Wi-Fi: guest / senha: convidado123";

    private static GuestAccessDeliveryRequested BuildEvent() => new()
    {
        TenantId = TenantId,
        AggregateId = GuestStayOperationId,
        AggregateType = "GuestStayOperation",
        CorrelationId = Guid.NewGuid(),
        ActorType = "System",
        ReservationId = ReservationId,
        PropertyId = PropertyId,
    };

    private static GuestAccessDeliveryProcessor CreateProcessor(
        FakePropertyGuestAccessReader accessReader, ITemplateReader templateReader,
        FakeReservationGuestContactReader guestContactReader, FakeMessageRepository repository, FakeOutboundMessageConnector connector) =>
        new(
            accessReader, templateReader, guestContactReader, repository, new PassThroughCommunicationTransactionExecutor(),
            connector, new FixedTimeProvider(Now), NullLogger<GuestAccessDeliveryProcessor>.Instance);

    /// <summary>Ambiguous key: only one FakeTemplateReader can be configured with a single Returning() result, but the processor looks up TWO different template keys. This double serves both lookups by key.</summary>
    private sealed class TwoKeyTemplateReader : ITemplateReader
    {
        private readonly Dictionary<string, ActiveTemplate> _templates;

        public TwoKeyTemplateReader(params ActiveTemplate[] templates) => _templates = templates.ToDictionary(t => t.Key);

        public Task<ActiveTemplate?> GetActiveByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
            Task.FromResult(_templates.GetValueOrDefault(key));
    }

    private static readonly ITemplateReader BothTemplates = new TwoKeyTemplateReader(
        new ActiveTemplate(CredentialTemplateKey, CredentialTemplateContent),
        new ActiveTemplate(InstructionsTemplateKey, InstructionsTemplateContent));

    [Fact]
    public async Task HandleAsync_sends_the_real_credential_to_the_connector_but_never_persists_it()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = new GuestAccessDeliveryProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(SentinelCredential, null)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, new PassThroughCommunicationTransactionExecutor(), connector, new FixedTimeProvider(Now),
            NullLogger<GuestAccessDeliveryProcessor>.Instance);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        // The connector — the credential's legitimate final destination — DOES receive the real value.
        connector.ReceivedDispatches.Should().ContainSingle();
        connector.ReceivedDispatches[0].Destination.Should().Be(GuestPhone);
        connector.ReceivedDispatches[0].Content.Should().Contain(SentinelCredential,
            "the connector is the credential's own intended final destination — this is not a leak");

        // The persisted Message NEVER carries it — this is the central security property of this checkpoint.
        repository.AddedMessages.Should().ContainSingle();
        var persistedMessage = repository.AddedMessages[0];
        persistedMessage.TemplateKey.Should().Be(CredentialTemplateKey);
        persistedMessage.RenderedContent.Should().Be(RedactedContentMarker);
        persistedMessage.RenderedContent.Should().NotContain(SentinelCredential,
            "the raw credential must never reach communication.messages.rendered_content in any form");
    }

    [Fact]
    public async Task HandleAsync_delivers_instructions_normally_persisted_as_is()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(null, SomeInstructions)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle();
        var message = repository.AddedMessages[0];
        message.TemplateKey.Should().Be(InstructionsTemplateKey);
        message.RenderedContent.Should().Contain(SomeInstructions,
            "instructions are not a secret — the ordinary persistence pipeline applies");
    }

    [Fact]
    public async Task HandleAsync_sends_both_intents_independently_when_both_are_configured()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(SentinelCredential, SomeInstructions)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().HaveCount(2);
        repository.AddedMessages.Select(m => m.TemplateKey).Should().BeEquivalentTo([CredentialTemplateKey, InstructionsTemplateKey]);
        connector.ReceivedDispatches.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_is_a_deliberate_no_op_when_no_active_configuration_exists()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(null),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_sends_only_instructions_when_no_credential_is_configured()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(null, SomeInstructions)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle().Which.TemplateKey.Should().Be(InstructionsTemplateKey);
    }

    [Fact]
    public async Task HandleAsync_sends_only_credential_when_no_instructions_are_configured()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(SentinelCredential, null)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().ContainSingle().Which.TemplateKey.Should().Be(CredentialTemplateKey);
    }

    [Fact]
    public async Task HandleAsync_skips_both_when_the_guest_has_no_phone_on_file()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(SentinelCredential, SomeInstructions)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, null, "Ana Silva")),
            repository, connector);

        await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_is_idempotent_per_intent_a_duplicate_event_never_duplicates_either_message()
    {
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Returning(new PropertyGuestAccessReadResult(SentinelCredential, SomeInstructions)),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        var @event = BuildEvent();
        await processor.HandleAsync(@event, CancellationToken.None);
        await processor.HandleAsync(@event, CancellationToken.None);

        repository.AddedMessages.Should().HaveCount(2, "exactly one credential message and one instructions message — never duplicated");
        connector.ReceivedDispatches.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_propagates_a_misconfigured_secret_reference_failure_loudly()
    {
        // CP6.2 mandate item 24: a configured-but-unresolvable credential
        // reference is an infrastructure failure — never silently swallowed.
        var repository = FakeMessageRepository.WithExisting(null);
        var connector = FakeOutboundMessageConnector.Succeeding();
        var processor = CreateProcessor(
            FakePropertyGuestAccessReader.Throwing(new InvalidOperationException("secret unresolved")),
            BothTemplates,
            FakeReservationGuestContactReader.Returning(new ReservationGuestContact(ReservationId, GuestPhone, "Ana Silva")),
            repository, connector);

        var act = async () => await processor.HandleAsync(BuildEvent(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        repository.AddedMessages.Should().BeEmpty();
        connector.ReceivedDispatches.Should().BeEmpty();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
