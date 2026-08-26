using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Reservations.Application;
using IHostPro.Contexts.Reservations.Application.Reservations;

namespace IHostPro.Contexts.Reservations.Tests.Unit.Application.Reservations;

/// <summary>
/// Test doubles that simply invoke <c>operation</c> directly, with no real
/// transaction/outbox — these unit tests exercise handler logic only; the
/// real executors are covered by the integration test suite (a real
/// PostgreSQL transaction/advisory lock cannot be meaningfully faked here).
/// </summary>
internal sealed class PassThroughCreateReservationExecutor : ICreateReservationExecutor
{
    public Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken) => operation();
}

internal sealed class PassThroughUpdateReservationExecutor : IUpdateReservationExecutor
{
    public Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken) => operation();
}

internal sealed class PassThroughCancelReservationExecutor : ICancelReservationExecutor
{
    public Task<Result<ReservationResult>> ExecuteAsync(
        Func<Task<Result<ReservationResult>>> operation, CancellationToken cancellationToken) => operation();
}

/// <summary>Fase 9, Checkpoint 3.2.1 — same pass-through rationale, for the Airbnb import/update/cancel processors' own <see cref="IReservationsTransactionExecutor"/> dependency.</summary>
internal sealed class PassThroughReservationsTransactionExecutor : IReservationsTransactionExecutor
{
    public Task<TResponse> ExecuteAsync<TResponse>(
        Func<Task<TResponse>> operation, CancellationToken cancellationToken) => operation();
}
