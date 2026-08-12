using FluentValidation;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Only validates a value the caller actually supplied — every filter is
/// optional (<c>null</c> means "no filter"/"use the default") — mirrors
/// <c>ListReservationsQueryValidator</c>. Max page size is fixed at 100.
/// </summary>
public sealed class ListCleaningsQueryValidator : AbstractValidator<ListCleaningsQuery>
{
    public const int MaxPageSize = 100;

    private static readonly string[] ValidStatusCodes =
        Enum.GetValues<CleaningStatus>().Select(CleaningStatusCodeMapper.ToCode).ToArray();

    public ListCleaningsQueryValidator()
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
            .Must(status => ValidStatusCodes.Contains(status))
            .When(x => x.Status is not null)
            .WithErrorCode("status_invalid")
            .WithMessage("status_invalid");
    }
}
