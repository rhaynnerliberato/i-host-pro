using FluentValidation;
using IHostPro.Contexts.Housekeeping.Domain.Enums;

namespace IHostPro.Contexts.Housekeeping.Application.Cleanings;

/// <summary>
/// Mirrors <see cref="ListCleaningsQueryValidator"/> exactly, minus the
/// filters that <see cref="ListOwnCleaningsQuery"/> does not expose to the
/// caller.
/// </summary>
public sealed class ListOwnCleaningsQueryValidator : AbstractValidator<ListOwnCleaningsQuery>
{
    public const int MaxPageSize = 100;

    private static readonly string[] ValidStatusCodes =
        Enum.GetValues<CleaningStatus>().Select(CleaningStatusCodeMapper.ToCode).ToArray();

    public ListOwnCleaningsQueryValidator()
    {
        RuleFor(x => x.HousekeeperUserId).NotEmpty().WithErrorCode("housekeeper_user_id_required").WithMessage("housekeeper_user_id_required");

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
