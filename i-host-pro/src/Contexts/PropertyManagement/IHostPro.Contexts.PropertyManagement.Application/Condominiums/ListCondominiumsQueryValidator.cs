using FluentValidation;

namespace IHostPro.Contexts.PropertyManagement.Application.Condominiums;

/// <summary>
/// Only validates a value the caller actually supplied — <c>null</c> is
/// always valid (it means "use the default") — mirrors
/// <c>ListUsersQueryValidator</c>. Max page size is fixed at 100 (Checkpoint
/// 2 plan, item 4).
/// </summary>
public sealed class ListCondominiumsQueryValidator : AbstractValidator<ListCondominiumsQuery>
{
    public const int MaxPageSize = 100;

    public ListCondominiumsQueryValidator()
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
    }
}
