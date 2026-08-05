using FluentValidation;
using IHostPro.Contexts.Reservations.Domain.Enums;

namespace IHostPro.Contexts.Reservations.Application.Reservations;

/// <summary>
/// Only validates a value the caller actually supplied — every filter is
/// optional (<c>null</c> means "no filter"/"use the default") — mirrors
/// <c>ListPropertiesQueryValidator</c>. Max page size is fixed at 100.
/// </summary>
public sealed class ListReservationsQueryValidator : AbstractValidator<ListReservationsQuery>
{
    public const int MaxPageSize = 100;

    public ListReservationsQueryValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .When(x => x.Page.HasValue)
            .WithErrorCode("page_must_be_positive")
            .WithMessage("page_must_be_positive");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, MaxPageSize)
            .When(x => x.PageSize.HasValue)
            .WithErrorCode("page_size_out_of_range")
            .WithMessage("page_size_out_of_range");

        RuleFor(x => x.Status)
            .Must(status => status == ReservationStatusCodeMapper.ToCode(ReservationStatus.Confirmed)
                || status == ReservationStatusCodeMapper.ToCode(ReservationStatus.Cancelled))
            .When(x => x.Status is not null)
            .WithErrorCode("status_invalid")
            .WithMessage("status_invalid");
    }
}
