using IHostPro.Contexts.Configuration.Application;
using IHostPro.Contexts.GuestOperations.Application;
using IHostPro.Contexts.Housekeeping.Application;
using IHostPro.Contexts.Payments.Application;
using IHostPro.Contexts.PropertyManagement.Application;
using IHostPro.Contexts.Reservations.Application;
using Mediator;

namespace IHostPro.Contexts.AIAgent.Tests.Unit.Tools;

/// <summary>
/// Shared minimal dispatcher stub (Fase 11, Checkpoint 3) — records every
/// request it receives and returns a preset response keyed by the request's
/// own runtime type. Each Bounded Context's own fake dispatcher below just
/// wraps one instance of this, mirroring the real
/// <c>I&lt;Context&gt;RequestDispatcher</c>'s own single-method shape.
/// </summary>
internal sealed class RequestDispatcherStub
{
    private readonly Dictionary<object, object> _responsesByValue = new();
    private readonly Dictionary<Type, object> _responsesByType = new();

    public List<object> ReceivedRequests { get; } = [];

    /// <summary>
    /// Registers a response matched first by the exact request VALUE (record
    /// equality — needed when a Tool sends more than one request of the same
    /// type with different field values, e.g. <c>GetEffectivePolicyQuery</c>
    /// for two different policy codes), falling back to a per-TYPE match
    /// when the exact value sent isn't known ahead of time (e.g. a schedule
    /// query whose date range is computed at call time).
    /// </summary>
    public void SetResponse<TRequest>(TRequest request, object response) where TRequest : notnull
    {
        _responsesByValue[request] = response;
        _responsesByType[typeof(TRequest)] = response;
    }

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ReceivedRequests.Add(request);

        if (_responsesByValue.TryGetValue(request, out var exact))
            return ValueTask.FromResult((TResponse)exact);

        if (_responsesByType.TryGetValue(request.GetType(), out var byType))
            return ValueTask.FromResult((TResponse)byType);

        throw new InvalidOperationException($"RequestDispatcherStub: no response configured for {request}.");
    }
}

internal sealed class FakeReservationsRequestDispatcher : IReservationsRequestDispatcher
{
    public RequestDispatcherStub Stub { get; } = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Stub.Send(request, cancellationToken);
}

internal sealed class FakePropertyManagementRequestDispatcher : IPropertyManagementRequestDispatcher
{
    public RequestDispatcherStub Stub { get; } = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Stub.Send(request, cancellationToken);
}

internal sealed class FakeHousekeepingRequestDispatcher : IHousekeepingRequestDispatcher
{
    public RequestDispatcherStub Stub { get; } = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Stub.Send(request, cancellationToken);
}

internal sealed class FakeConfigurationRequestDispatcher : IConfigurationRequestDispatcher
{
    public RequestDispatcherStub Stub { get; } = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Stub.Send(request, cancellationToken);
}

internal sealed class FakePaymentsRequestDispatcher : IPaymentsRequestDispatcher
{
    public RequestDispatcherStub Stub { get; } = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Stub.Send(request, cancellationToken);
}

/// <summary>Fase 11, Checkpoint 4.</summary>
internal sealed class FakeGuestOperationsRequestDispatcher : IGuestOperationsRequestDispatcher
{
    public RequestDispatcherStub Stub { get; } = new();

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Stub.Send(request, cancellationToken);
}
