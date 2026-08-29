using IHostPro.Contexts.Communication.Application;
using IHostPro.Contexts.Communication.Domain;
using IHostPro.Contexts.Configuration.Contracts;
using IHostPro.Contexts.Payments.Contracts;
using IHostPro.Contexts.PropertyManagement.Contracts;
using IHostPro.Contexts.Reservations.Contracts;

namespace IHostPro.Contexts.Communication.Tests.Unit.Application;

/// <summary>Fase 10, Checkpoint 4 (Portaria Notification Foundation) — ADR-026's fake, mirrors <see cref="FakeReservationGuestContactReader"/> exactly.</summary>
internal sealed class FakeFrontDeskContactReader : IFrontDeskContactReader
{
    private readonly FrontDeskContactReadResult? _result;

    private FakeFrontDeskContactReader(FrontDeskContactReadResult? result) => _result = result;

    public static FakeFrontDeskContactReader Returning(FrontDeskContactReadResult? result) => new(result);

    public Task<FrontDeskContactReadResult?> GetActiveByPropertyIdAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken) =>
        Task.FromResult(_result);
}

internal sealed class FakeTemplateReader : ITemplateReader
{
    private readonly ActiveTemplate? _template;

    private FakeTemplateReader(ActiveTemplate? template) => _template = template;

    public static FakeTemplateReader Returning(ActiveTemplate? template) => new(template);

    public Task<ActiveTemplate?> GetActiveByKeyAsync(Guid tenantId, string key, CancellationToken cancellationToken) =>
        Task.FromResult(_template);
}

internal sealed class FakeReservationGuestContactReader : IReservationGuestContactReader
{
    private readonly ReservationGuestContact? _contact;

    private FakeReservationGuestContactReader(ReservationGuestContact? contact) => _contact = contact;

    public static FakeReservationGuestContactReader Returning(ReservationGuestContact? contact) => new(contact);

    public Task<ReservationGuestContact?> GetGuestContactAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken) =>
        Task.FromResult(_contact);
}

/// <summary>Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — ADR-029's fake, mirrors <see cref="FakeReservationGuestContactReader"/> exactly.</summary>
internal sealed class FakeReservationByGuestPhoneReader : IReservationByGuestPhoneReader
{
    private readonly IReadOnlyList<ReservationCandidate> _candidates;

    private FakeReservationByGuestPhoneReader(IReadOnlyList<ReservationCandidate> candidates) => _candidates = candidates;

    public static FakeReservationByGuestPhoneReader Returning(params ReservationCandidate[] candidates) => new(candidates);

    public Task<IReadOnlyList<ReservationCandidate>> FindEligibleByGuestPhoneAsync(
        Guid tenantId, string guestPhoneNormalized, CancellationToken cancellationToken) =>
        Task.FromResult(_candidates);
}

/// <summary>Fase 10, Checkpoint 5 (PIX/Payment Deterministic Foundation) — ADR-027's fake, mirrors <see cref="FakeFrontDeskContactReader"/> exactly.</summary>
internal sealed class FakePixChargeDeliveryReader : IPixChargeDeliveryReader
{
    private readonly PixChargeDeliveryReadResult? _result;

    private FakePixChargeDeliveryReader(PixChargeDeliveryReadResult? result) => _result = result;

    public static FakePixChargeDeliveryReader Returning(PixChargeDeliveryReadResult? result) => new(result);

    public Task<PixChargeDeliveryReadResult?> GetForDeliveryAsync(Guid tenantId, Guid pixChargeId, CancellationToken cancellationToken) =>
        Task.FromResult(_result);
}

/// <summary>Fase 10, Checkpoint 6.2 (Guest Access Secure Delivery Corrective Implementation) — ADR-028's fake, mirrors <see cref="FakeFrontDeskContactReader"/>/<see cref="FakePixChargeDeliveryReader"/> exactly.</summary>
internal sealed class FakePropertyGuestAccessReader : IPropertyGuestAccessReader
{
    private readonly PropertyGuestAccessReadResult? _result;
    private readonly Exception? _exceptionToThrow;

    private FakePropertyGuestAccessReader(PropertyGuestAccessReadResult? result, Exception? exceptionToThrow)
    {
        _result = result;
        _exceptionToThrow = exceptionToThrow;
    }

    public static FakePropertyGuestAccessReader Returning(PropertyGuestAccessReadResult? result) => new(result, null);

    public static FakePropertyGuestAccessReader Throwing(Exception exception) => new(null, exception);

    public Task<PropertyGuestAccessReadResult?> GetForGuestAccessDeliveryAsync(Guid tenantId, Guid propertyId, CancellationToken cancellationToken) =>
        _exceptionToThrow is not null ? throw _exceptionToThrow : Task.FromResult(_result);
}

internal sealed class FakeMessageRepository : IMessageRepository
{
    private readonly Dictionary<string, Message> _byIdempotencyKey = new();
    private readonly Dictionary<Guid, Message> _byId = new();

    public static FakeMessageRepository WithExisting(Message? existing)
    {
        var repository = new FakeMessageRepository();
        if (existing is not null)
        {
            repository._byIdempotencyKey[existing.IdempotencyKey] = existing;
            repository._byId[existing.Id] = existing;
        }
        return repository;
    }

    public List<Message> AddedMessages { get; } = [];
    public List<Message> UpdatedMessages { get; } = [];

    public Task<Message?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        Task.FromResult(_byIdempotencyKey.GetValueOrDefault(idempotencyKey));

    public Task<Message?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    /// <summary>Mirrors the real repository's <c>SingleOrDefaultAsync</c> semantics (Fase 9, Checkpoint 2.3.3) — throws on more than one match, never silently picks one.</summary>
    public Task<Message?> GetByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.SingleOrDefault(m => m.ProviderMessageId == providerMessageId));

    public void Add(Message aggregate)
    {
        _byIdempotencyKey[aggregate.IdempotencyKey] = aggregate;
        _byId[aggregate.Id] = aggregate;
        AddedMessages.Add(aggregate);
    }

    public void Update(Message aggregate)
    {
        _byIdempotencyKey[aggregate.IdempotencyKey] = aggregate;
        _byId[aggregate.Id] = aggregate;
        UpdatedMessages.Add(aggregate);
    }

    public void Remove(Message aggregate) => _byIdempotencyKey.Remove(aggregate.IdempotencyKey);
}

/// <summary>Runs the operation directly — no real transaction/RLS needed for these fast unit tests (that guarantee is covered by the real-Postgres Integration suite).</summary>
internal sealed class PassThroughCommunicationTransactionExecutor : ICommunicationTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(Func<Task<TResponse>> operation, CancellationToken cancellationToken) =>
        operation();
}

/// <summary>Fase 11, Checkpoint 1 (Inbound Conversation Foundation) — returns a fixed ConversationId, no real lookup/create; every outbound processor's own Conversation-resolution behavior is exercised by the real <c>ConversationResolver</c>'s own tests instead.</summary>
internal sealed class FakeConversationResolver : IConversationResolver
{
    private readonly Guid _conversationId;

    private FakeConversationResolver(Guid conversationId) => _conversationId = conversationId;

    public static FakeConversationResolver Returning(Guid conversationId) => new(conversationId);

    public Task<Guid> GetOrCreateActiveConversationIdAsync(
        Guid tenantId, Guid reservationId, string channel, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
        Task.FromResult(_conversationId);
}

internal sealed class FakeOutboundMessageConnector : IOutboundMessageConnector
{
    private readonly Func<OutboundMessageDispatch, OutboundMessageDispatchResult> _behavior;

    private FakeOutboundMessageConnector(Func<OutboundMessageDispatch, OutboundMessageDispatchResult> behavior) =>
        _behavior = behavior;

    public static FakeOutboundMessageConnector Succeeding() =>
        new(_ => new OutboundMessageDispatchResult(Success: true, ProviderMessageId: null, FailureReason: null));

    public static FakeOutboundMessageConnector SucceedingWithProviderMessageId(string providerMessageId) =>
        new(_ => new OutboundMessageDispatchResult(Success: true, ProviderMessageId: providerMessageId, FailureReason: null));

    public static FakeOutboundMessageConnector Rejecting(string failureReason) =>
        new(_ => new OutboundMessageDispatchResult(Success: false, ProviderMessageId: null, FailureReason: failureReason));

    public static FakeOutboundMessageConnector Throwing(Exception exception) =>
        new(_ => throw exception);

    public List<OutboundMessageDispatch> ReceivedDispatches { get; } = [];

    public Task<OutboundMessageDispatchResult> SendAsync(OutboundMessageDispatch dispatch, CancellationToken cancellationToken)
    {
        ReceivedDispatches.Add(dispatch);
        return Task.FromResult(_behavior(dispatch));
    }
}
