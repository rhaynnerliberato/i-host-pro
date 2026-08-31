using IHostPro.BuildingBlocks.Application;
using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.Payments.Application.Errors;

namespace IHostPro.Contexts.Payments.Application;

/// <inheritdoc cref="GetPaymentStatusByReservationQuery"/>
public sealed class GetPaymentStatusByReservationQueryHandler
    : IQueryHandler<GetPaymentStatusByReservationQuery, PaymentStatusResult>
{
    private static readonly Error PixChargeNotFoundError = new(
        PaymentsErrorCodes.PixChargeNotFound, PaymentsErrorCodes.PixChargeNotFound);

    private readonly IPixChargeReader _reader;

    public GetPaymentStatusByReservationQueryHandler(IPixChargeReader reader) => _reader = reader;

    public async ValueTask<Result<PaymentStatusResult>> Handle(
        GetPaymentStatusByReservationQuery query, CancellationToken cancellationToken)
    {
        var result = await _reader.GetStatusByReservationIdAsync(query.ReservationId, cancellationToken);

        return result is null
            ? Result.Failure<PaymentStatusResult>(PixChargeNotFoundError)
            : Result.Success(result);
    }
}
